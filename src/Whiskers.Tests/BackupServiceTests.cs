using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Services.Backup;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>End-to-end self-backup service against a real SQLite database: encrypted vs plaintext archives,
/// the snapshot exclude set (backups/ + *.tmp + live DB), list/validate round-trip, and the restore
/// compatibility checks (reject a newer schema / a provider mismatch; accept an equal-or-older schema).</summary>
public class BackupServiceTests
{
    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static (BackupService svc, DataPathOptions paths, string root, ServiceProvider sp) NewService(string? vaultKey)
        => NewService(vaultKey, new MaintenanceStateService());

    private static (BackupService svc, DataPathOptions paths, string root, ServiceProvider sp) NewService(string? vaultKey,
        IMaintenanceStateService maintenance, bool migrate = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sw-bak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new DataPathOptions(root);

        var services = new ServiceCollection();
        // As DatabaseRegistration does when migrate is set (the migrations' assembly, the identity history table).
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={paths.DbPath}",
            sql => { if (migrate) sql.MigrationsAssembly("Whiskers.Migrations.Sqlite"); }));
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={paths.DbPath}", sql =>
        {
            if (!migrate) return;
            sql.MigrationsAssembly("Whiskers.Migrations.Sqlite");
            sql.MigrationsHistoryTable("__IdentityMigrationsHistory");
        }));
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
        {
            if (migrate)
            {
                scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.Migrate();
                scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.Migrate();
            }
            else scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(vaultKey is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["VAULT_KEY"] = vaultKey })
            .Build();

        var svc = new BackupService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            maintenance,
            new NoopLifetime(),
            config,
            NullLogger<BackupService>.Instance,
            paths);
        return (svc, paths, root, sp);
    }

    private static void Cleanup(ServiceProvider sp, string root)
    {
        sp.Dispose();
        TestSqlite.ClearPools(root);
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task Encrypted_backup_lists_and_validates_round_trip()
    {
        var (svc, _, root, sp) = NewService("test-vault-key");
        try
        {
            var info = await svc.CreateBackupAsync("manual");
            Assert.True(info.Encrypted);
            Assert.Equal("sqlite", info.Provider);

            var path = svc.ResolveArchivePath(info.Id)!;
            Assert.NotNull(path);
            var head = new byte[BackupArchiveCipher.MagicLength];
            await using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
            Assert.True(BackupArchiveCipher.StartsWithMagic(head));   // encrypted at rest

            var list = await svc.ListBackupsAsync();
            Assert.Single(list);
            Assert.Equal(info.Id, list[0].Id);

            var validation = await svc.ValidateArchiveAsync(path);
            Assert.True(validation.Ok);
            Assert.Equal("sqlite", validation.Manifest!.Provider);
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task Plaintext_backup_excludes_backups_dir_tmp_and_live_db()
    {
        var (svc, paths, root, sp) = NewService(vaultKey: null);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "roles.json"), "[]");
            Directory.CreateDirectory(Path.Combine(root, "keys"));
            await File.WriteAllTextAsync(Path.Combine(root, "keys", "ring.xml"), "<k/>");
            Directory.CreateDirectory(paths.SelfBackupsDir);
            await File.WriteAllTextAsync(Path.Combine(paths.SelfBackupsDir, "old.tar.gz"), "OLD-BACKUP");
            await File.WriteAllTextAsync(Path.Combine(root, "scratch.tmp"), "TMP");

            var info = await svc.CreateBackupAsync("manual");
            Assert.False(info.Encrypted);

            var path = svc.ResolveArchivePath(info.Id)!;
            await using var fs = File.OpenRead(path);
            var inspection = await BackupArchiver.InspectAsync(fs, Path.Combine(root, "insp"));

            Assert.Contains("manifest.json", inspection.EntryNames);
            Assert.Contains("metrics.db", inspection.EntryNames);            // the VACUUM copy
            Assert.Contains("roles.json", inspection.EntryNames);
            Assert.Contains("keys/ring.xml", inspection.EntryNames);
            Assert.DoesNotContain(inspection.EntryNames, n => n.StartsWith("backups/", StringComparison.Ordinal));
            Assert.DoesNotContain(inspection.EntryNames, n => n.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task The_audit_checkpoint_file_is_archived_once_as_taken_before_the_database()
    {
        // R1-23: staged before VACUUM INTO, so a restored database always reaches the restored checkpoint.
        var (svc, paths, root, sp) = NewService(vaultKey: null);
        try
        {
            await File.WriteAllTextAsync(paths.AuditCheckpointJson, "{\"Kind\":\"head\",\"Seq\":7}");

            var info = await svc.CreateBackupAsync("manual");

            await using var fs = File.OpenRead(svc.ResolveArchivePath(info.Id)!);
            var inspection = await BackupArchiver.InspectAsync(fs, Path.Combine(root, "insp"));
            Assert.Single(inspection.EntryNames, n => n == "audit-checkpoint.json");
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task Validate_rejects_a_newer_schema()
    {
        var (svc, _, root, sp) = NewService(vaultKey: null);
        try
        {
            var archive = await CraftManifestArchive(root,
                new SelfBackupManifest { Provider = "sqlite", MetricsMigrations = new[] { "9999_from_the_future" } });
            var result = await svc.ValidateArchiveAsync(archive);
            Assert.False(result.Ok);
            Assert.Contains("newer", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task Validate_rejects_a_provider_mismatch()
    {
        var (svc, _, root, sp) = NewService(vaultKey: null);
        try
        {
            var archive = await CraftManifestArchive(root, new SelfBackupManifest { Provider = "postgres" });
            var result = await svc.ValidateArchiveAsync(archive);
            Assert.False(result.Ok);
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task Validate_accepts_an_equal_or_older_schema()
    {
        var (svc, _, root, sp) = NewService(vaultKey: null);
        try
        {
            var archive = await CraftManifestArchive(root, new SelfBackupManifest { Provider = "sqlite" });
            var result = await svc.ValidateArchiveAsync(archive);
            Assert.True(result.Ok);
        }
        finally { Cleanup(sp, root); }
    }

    // ---------------------------------------------------------------- R1-32: authentic, checked restore

    [Fact]
    public async Task With_a_vault_key_an_unencrypted_archive_is_refused()
    {
        var (svc, _, root, sp) = NewService("test-vault-key");
        try
        {
            var archive = await CraftManifestArchive(root, new SelfBackupManifest { Provider = "sqlite" });
            var result = await svc.ValidateArchiveAsync(archive);
            Assert.False(result.Ok);
            Assert.Contains("not encrypted", result.Error!);
            await Assert.ThrowsAsync<BackupValidationException>(() => svc.BeginRestoreAsync(archive, "tester"));
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task An_encrypted_archive_is_published_whole_under_its_final_name()
    {
        var (svc, paths, root, sp) = NewService("test-vault-key");
        try
        {
            var info = await svc.CreateBackupAsync("manual");
            Assert.Equal([$"{info.Id}.meta.json", $"{info.Id}.tar.gz.enc"],
                Directory.GetFiles(paths.SelfBackupsDir).Select(Path.GetFileName).Order().ToArray());
            Assert.False(Directory.Exists(Path.Combine(paths.SelfBackupsDir, ".build", info.Id)));
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task A_restore_stages_checks_and_commits_and_the_next_start_applies_it()
    {
        var (svc, paths, root, sp) = NewService("test-vault-key", new MaintenanceStateService(), migrate: true);
        try
        {
            await File.WriteAllTextAsync(paths.RolesJson, "{\"version\":\"archived\"}");
            var info = await svc.CreateBackupAsync("manual");
            await File.WriteAllTextAsync(paths.RolesJson, "{\"version\":\"live\"}");
            await File.WriteAllTextAsync(paths.GuardrailsJson, "{}");   // not in the archive

            await svc.BeginRestoreAsync(svc.ResolveArchivePath(info.Id)!, "tester");

            Assert.True(File.Exists(paths.RestorePendingMarker));
            Assert.True(File.Exists(Path.Combine(paths.RestoreStagingDir, "metrics.db")));
            Assert.Equal(2, (await svc.ListBackupsAsync()).Count);    // the archive and the pre-restore safety copy

            TestSqlite.ClearPools(root);
            RestoreBootHandler.ApplyPendingRestore(paths);

            Assert.Equal("{\"version\":\"archived\"}", await File.ReadAllTextAsync(paths.RolesJson));
            Assert.False(File.Exists(paths.GuardrailsJson));
            Assert.Equal(2, (await svc.ListBackupsAsync()).Count);    // both survive the swap
        }
        finally { Cleanup(sp, root); }
    }

    public static TheoryData<string, string> DamagedArchives => new()
    {
        { "database", "database" },
        { "migrations", "migrations" },
        { "json", "roles.json" },
        { "roles-shape", "roles.json" },        // valid JSON, but not what the role service reads at start
        { "settings-shape", "app-settings.json" },
        { "key", "keys/" },
        { "identity-unmigrated", "migration history" },   // the start would create the sign-in tables again and stop
        { "metrics-unmigrated", "migration history" },
        { "trigger", "triggers" },                  // would run only on the live database
    };

    [Theory]
    [MemberData(nameof(DamagedArchives))]
    public async Task A_damaged_archive_is_refused_before_anything_is_committed(string damage, string expected)
    {
        var maintenance = new MaintenanceStateService();
        var (svc, paths, root, sp) = NewService(vaultKey: null, maintenance);
        try
        {

            var dir = Path.Combine(root, "crafted-db");
            Directory.CreateDirectory(dir);
            var db = Path.Combine(dir, "metrics.db");
            if (damage == "database")
                await File.WriteAllBytesAsync(db, System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(new byte[4096]).ToArray());
            else
                CreateDatabase(db, damage == "migrations" ? ["20260101000000_NotInTheManifest"] : [],
                    damage switch { "identity-unmigrated" => "AspNetUsers", "metrics-unmigrated" => "Servers", _ => null },
                    trigger: damage == "trigger");
            var extra = new List<PackEntry>();
            if (damage is "json" or "roles-shape" or "settings-shape")
            {
                var store = damage == "settings-shape" ? "app-settings.json" : "roles.json";
                await File.WriteAllTextAsync(Path.Combine(dir, store), damage == "json" ? "{ not json" : "[]");
                extra.Add(new PackEntry(store, Path.Combine(dir, store)));
            }
            if (damage == "key")
            {
                await File.WriteAllTextAsync(Path.Combine(dir, "key.xml"), "<key><unclosed>");
                extra.Add(new PackEntry("keys/key-1.xml", Path.Combine(dir, "key.xml")));
            }
            // The manifest names no migrations; for "migrations" the database holds one.
            var archive = await CraftManifestArchive(root, new SelfBackupManifest { Provider = "sqlite" }, db, extra);
            await File.WriteAllTextAsync(paths.RolesJson, "LIVE");

            var ex = await Assert.ThrowsAsync<BackupValidationException>(() => svc.BeginRestoreAsync(archive, "tester"));
            Assert.Contains(expected, ex.Message);

            Assert.False(File.Exists(paths.RestorePendingMarker));
            Assert.False(Directory.Exists(paths.RestoreStagingDir));
            Assert.False(maintenance.IsMaintenance);
            Assert.Empty(await svc.ListBackupsAsync());                  // no safety copy for a refused archive
            Assert.Equal("LIVE", await File.ReadAllTextAsync(paths.RolesJson));
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task A_second_restore_is_refused_while_one_is_committed()
    {
        var (svc, paths, root, sp) = NewService(vaultKey: null);
        try
        {
            await File.WriteAllTextAsync(paths.RestorePendingMarker, "{}");
            Directory.CreateDirectory(paths.RestoreStagingDir);
            await File.WriteAllTextAsync(Path.Combine(paths.RestoreStagingDir, "roles.json"), "{}");
            var archive = await CraftManifestArchive(root, new SelfBackupManifest { Provider = "sqlite" });

            await Assert.ThrowsAsync<BackupValidationException>(() => svc.BeginRestoreAsync(archive, "tester"));
            Assert.True(File.Exists(Path.Combine(paths.RestoreStagingDir, "roles.json")));   // the committed one is intact
        }
        finally { Cleanup(sp, root); }
    }

    [Fact]
    public async Task A_backup_of_a_migrated_database_restores()
    {
        // The real schema: both history tables present and named in the manifest, as on every installed instance.
        var (svc, paths, root, sp) = NewService("test-vault-key", new MaintenanceStateService(), migrate: true);
        try
        {
            using (var scope = sp.CreateScope())
                Assert.NotEmpty(await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.GetAppliedMigrationsAsync());
            var info = await svc.CreateBackupAsync("manual");
            Assert.True((await svc.ValidateArchiveAsync(svc.ResolveArchivePath(info.Id)!)).Ok);

            await svc.BeginRestoreAsync(svc.ResolveArchivePath(info.Id)!, "tester");
            Assert.True(File.Exists(paths.RestorePendingMarker));
        }
        finally { Cleanup(sp, root); }
    }

    [Theory]
    [InlineData("history-behind")]   // the start would run the next migration against tables it already holds
    [InlineData("table-dropped")]    // the history is complete, but the start reads a table that is gone
    [InlineData("column-dropped")]
    public async Task A_database_the_start_cannot_use_is_refused_before_anything_is_committed(string damage)
    {
        var maintenance = new MaintenanceStateService();
        var (svc, paths, root, sp) = NewService(vaultKey: null, maintenance, migrate: true);
        try
        {
            string[] metrics, identity;
            using (var scope = sp.CreateScope())
            {
                metrics = (await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.GetAppliedMigrationsAsync()).ToArray();
                identity = (await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.GetAppliedMigrationsAsync()).ToArray();
            }
            Assert.True(metrics.Length > 1);
            TestSqlite.ClearPools(root);

            var dir = Path.Combine(root, "crafted-behind");
            Directory.CreateDirectory(dir);
            var db = Path.Combine(dir, "metrics.db");
            File.Copy(paths.DbPath, db);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = damage switch
                {
                    "history-behind" => "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" <> $first",
                    "table-dropped" => "DROP TABLE \"AuditLog\"",
                    _ => "ALTER TABLE \"AuditLog\" DROP COLUMN \"Details\"",
                };
                command.Parameters.AddWithValue("$first", metrics[0]);
                command.ExecuteNonQuery();
            }
            var archive = await CraftManifestArchive(root, new SelfBackupManifest
            {
                Provider = "sqlite",
                MetricsMigrations = damage == "history-behind" ? [metrics[0]] : metrics,
                IdentityMigrations = identity,
            }, db);

            Assert.True((await svc.ValidateArchiveAsync(archive)).Ok);   // the manifest alone looks fine
            var ex = await Assert.ThrowsAsync<BackupValidationException>(() => svc.BeginRestoreAsync(archive, "tester"));
            Assert.Contains("schema", ex.Message);
            Assert.False(File.Exists(paths.RestorePendingMarker));
            Assert.False(Directory.Exists(paths.RestoreStagingDir));
            Assert.False(maintenance.IsMaintenance);
            Assert.Empty(Directory.GetDirectories(Path.Combine(paths.SelfBackupsDir, ".build")));   // the probe copy is gone
        }
        finally { Cleanup(sp, root); }
    }

    private static void CreateDatabase(string path, string[] migrations, string? table = null, bool trigger = false)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT PRIMARY KEY, \"ProductVersion\" TEXT);";
        command.ExecuteNonQuery();
        if (table is not null)
        {
            command.CommandText = $"CREATE TABLE \"{table}\" (\"Id\" INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }
        if (trigger)
        {
            command.CommandText = "CREATE TRIGGER t AFTER INSERT ON \"__EFMigrationsHistory\" BEGIN SELECT 1; END;";
            command.ExecuteNonQuery();
        }
        foreach (var migration in migrations)
        {
            command.CommandText = "INSERT INTO \"__EFMigrationsHistory\" VALUES ($id, '10.0')";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", migration);
            command.ExecuteNonQuery();
        }
    }

    private static async Task<string> CraftManifestArchive(string root, SelfBackupManifest manifest,
        string? database = null, IReadOnlyList<PackEntry>? extra = null)
    {
        var dir = Path.Combine(root, $"crafted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
        var entries = new List<PackEntry> { new("manifest.json", manifestPath) };
        if (manifest.Provider == SelfBackupFormat.ProviderSqlite)
        {
            var db = database ?? Path.Combine(dir, "metrics.db");
            if (database is null) await File.WriteAllTextAsync(db, "");   // a SQLite archive holds its database
            entries.Add(new PackEntry("metrics.db", db));
        }
        if (extra is not null) entries.AddRange(extra);
        var archivePath = Path.Combine(dir, "crafted.tar.gz");
        await using (var fs = File.Create(archivePath))
            await BackupArchiver.PackAsync(fs, entries);
        return archivePath;
    }
}
