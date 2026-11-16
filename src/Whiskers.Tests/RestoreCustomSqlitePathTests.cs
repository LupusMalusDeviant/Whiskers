using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>A restore on SQLite with its own connection string (WHISKERS_DB_CONNECTION) replaces the database the app
/// opens, not the default <c>metrics.db</c> (review 1.0.0, Data).</summary>
public sealed class RestoreCustomSqlitePathTests
{
    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Fact]
    public async Task A_restore_replaces_the_sqlite_database_the_connection_string_names()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sw-review-restore-{Guid.NewGuid():N}");
        var elsewhere = Path.Combine(Path.GetTempPath(), $"sw-review-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(elsewhere);
        var paths = new DataPathOptions(root);
        var custom = Path.Combine(elsewhere, "whiskers.db").Replace('\\', '/');
        // As the app registers it for WHISKERS_DB_PROVIDER=sqlite with WHISKERS_DB_CONNECTION set.
        var sp = new ServiceCollection()
            .AddWhiskersDbContexts("sqlite", $"Data Source={custom}", paths)
            .BuildServiceProvider();
        try
        {
            using (var scope = sp.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.Migrate();
                scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.Migrate();
            }
            async Task AddTaskAsync(string id)
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                db.ScheduledTasks.Add(new ScheduledTaskEntity { TaskId = id, Name = id, CronExpression = "0 3 * * *", Enabled = false });
                await db.SaveChangesAsync();
            }
            async Task<List<string>> TasksAsync()
            {
                using var scope = sp.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().ScheduledTasks.AsNoTracking()
                    .Select(t => t.TaskId).OrderBy(t => t).ToListAsync();
            }

            var svc = new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), new MaintenanceStateService(),
                new NoopLifetime(), new ConfigurationBuilder().Build(), NullLogger<BackupService>.Instance, paths);

            await AddTaskAsync("archived");
            var info = await svc.CreateBackupAsync("manual");
            await AddTaskAsync("added-after-the-backup");

            await svc.BeginRestoreAsync(svc.ResolveArchivePath(info.Id)!, "tester");
            Assert.True(File.Exists(paths.RestorePendingMarker));
            TestSqlite.ClearPools(root);
            TestSqlite.ClearPool(custom);
            RestoreBootHandler.ApplyPendingRestore(paths);
            Assert.False(File.Exists(paths.RestorePendingMarker));   // the restore says it was applied

            var after = await TasksAsync();
            Assert.True(after.SequenceEqual(["archived"]),
                $"The restore was applied, but the database the app uses ({custom}) still holds [{string.Join(", ", after)}]; " +
                $"the archive's database was written to {paths.DbPath} (exists: {File.Exists(paths.DbPath)}), which this " +
                "configuration never opens.");
        }
        finally
        {
            sp.Dispose();
            TestSqlite.ClearPools(root);
            TestSqlite.ClearPool(custom);
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
            try { Directory.Delete(elsewhere, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public async Task A_restore_refused_for_a_file_uri_connection_string_takes_no_safety_backup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sw-review-restore-{Guid.NewGuid():N}");
        var elsewhere = Path.Combine(Path.GetTempPath(), $"sw-review-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(elsewhere);
        var paths = new DataPathOptions(root);
        var custom = Path.Combine(elsewhere, "whiskers.db").Replace('\\', '/');
        var sp = new ServiceCollection()
            .AddWhiskersDbContexts("sqlite", $"Data Source=file:{custom}", paths)
            .BuildServiceProvider();
        try
        {
            using (var scope = sp.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.Migrate();
                scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.Migrate();
            }
            var maintenance = new MaintenanceStateService();
            var svc = new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), maintenance,
                new NoopLifetime(), new ConfigurationBuilder().Build(), NullLogger<BackupService>.Instance, paths);
            var info = await svc.CreateBackupAsync("manual");
            var before = (await svc.ListBackupsAsync()).Select(b => b.Id).ToList();

            var refused = await Assert.ThrowsAsync<BackupValidationException>(
                () => svc.BeginRestoreAsync(svc.ResolveArchivePath(info.Id)!, "tester"));

            Assert.Contains("Nothing was changed", refused.Message);
            Assert.Equal(before, (await svc.ListBackupsAsync()).Select(b => b.Id).ToList());   // no pre-restore safety backup
            Assert.False(File.Exists(paths.RestorePendingMarker));
            Assert.False(maintenance.IsMaintenance);
        }
        finally
        {
            sp.Dispose();
            TestSqlite.ClearPools(root);
            TestSqlite.ClearPool(custom);
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
            try { Directory.Delete(elsewhere, recursive: true); } catch { /* temp dir */ }
        }
    }

    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void The_start_s_configuration_names_the_database_the_swap_replaces()
    {
        var paths = new DataPathOptions(Path.Combine(Path.GetTempPath(), $"sw-restore-target-{Guid.NewGuid():N}"));
        var staging = paths.RestoreStagingDir;   // holds no settings files: the archive names no database of its own
        var custom = Path.Combine(Path.GetTempPath(), "elsewhere", "whiskers.db");
        var marker = new RestoreMarker { Provider = "sqlite", DatabasePath = Path.Combine(Path.GetTempPath(), "marker.db") };

        // The configuration wins: it names what the app opens after this start.
        Assert.Equal(Path.GetFullPath(custom), RestoreBootHandler.SqliteTarget(paths, marker, Config(new()
        {
            ["WHISKERS_DB_PROVIDER"] = "sqlite", ["WHISKERS_DB_CONNECTION"] = $"Data Source={custom}",
        }), staging));
        // Configured without a connection string: the default file, as the app registers it.
        Assert.Equal(Path.GetFullPath(paths.DbPath), RestoreBootHandler.SqliteTarget(paths, marker, Config(new()
        {
            ["WHISKERS_DB_PROVIDER"] = "sqlite",
        }), staging));
        // No configuration: the file the app had open when the restore was committed; a marker of an earlier release
        // names none, so the default.
        Assert.Equal(Path.GetFullPath(marker.DatabasePath), RestoreBootHandler.SqliteTarget(paths, marker, null, staging));
        Assert.Equal(Path.GetFullPath(paths.DbPath),
            RestoreBootHandler.SqliteTarget(paths, new RestoreMarker { Provider = "sqlite" }, null, staging));
        // Old markers deserialize without the new field.
        var old = JsonSerializer.Deserialize<RestoreMarker>("""{"StagingDir":"/x","Provider":"sqlite"}""")!;
        Assert.Equal("", old.DatabasePath);
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=shared;Mode=Memory")]
    [InlineData("")]
    public void A_connection_string_without_a_file_names_no_database_file(string connection)
        => Assert.Null(DatabaseRegistration.SqliteFilePath(connection));

    [Theory]
    [InlineData("Data Source=file:whiskers.db?cache=shared")]
    [InlineData("Data Source=|DataDirectory|whiskers.db")]
    public void A_connection_string_whose_file_is_not_a_plain_path_is_refused(string connection)
        => Assert.Throws<InvalidOperationException>(() => DatabaseRegistration.SqliteFilePath(connection));

    // A staged SQLite restore, committed while the app had `databaseFile` open.
    private static DataPathOptions StageRestore(out string root, string? databaseFile = null)
    {
        root = Path.Combine(Path.GetTempPath(), $"sw-restore-custom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new DataPathOptions(root);
        Directory.CreateDirectory(paths.RestoreStagingDir);
        File.WriteAllText(Path.Combine(paths.RestoreStagingDir, SelfBackupFormat.DbEntryName), "ARCHIVE-DB");
        File.WriteAllText(paths.RestorePendingMarker, JsonSerializer.Serialize(new RestoreMarker
        {
            StagingDir = paths.RestoreStagingDir, Provider = "sqlite", DatabasePath = databaseFile ?? "",
        }));
        return paths;
    }

    private static void Delete(params string[] dirs)
    {
        foreach (var d in dirs) try { Directory.Delete(d, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void The_operator_s_bak_beside_a_custom_database_survives_the_restore()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), $"sw-restore-elsewhere-{Guid.NewGuid():N}");
        Directory.CreateDirectory(elsewhere);
        var custom = Path.Combine(elsewhere, "whiskers.db");
        File.WriteAllText(custom, "LIVE-DB");
        File.WriteAllText(custom + ".bak", "OPERATOR-COPY");
        File.WriteAllText(custom + "-wal", "STALE-WAL");
        var paths = StageRestore(out var root);
        try
        {
            RestoreBootHandler.ApplyPendingRestore(paths, Config(new()
            {
                ["WHISKERS_DB_PROVIDER"] = "sqlite", ["WHISKERS_DB_CONNECTION"] = $"Data Source={custom}",
            }));

            Assert.False(File.Exists(paths.RestorePendingMarker));
            Assert.Equal("ARCHIVE-DB", File.ReadAllText(custom));
            Assert.False(File.Exists(custom + "-wal"));                       // the old journal never meets the new file
            Assert.Equal("OPERATOR-COPY", File.ReadAllText(custom + ".bak")); // never written by Whiskers, never removed
            Assert.False(File.Exists(paths.DbPath));                          // the default file was not written
        }
        finally { Delete(root, elsewhere); }
    }

    [Fact]
    public void A_restore_whose_settings_name_another_database_is_refused_before_anything_is_replaced()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), $"sw-restore-elsewhere-{Guid.NewGuid():N}");
        Directory.CreateDirectory(elsewhere);
        var paths = StageRestore(out var root);
        File.WriteAllText(paths.DbPath, "LIVE-DB");
        File.WriteAllText(Path.Combine(root, "roles.json"), "LIVE-ROLES");
        File.WriteAllText(Path.Combine(paths.RestoreStagingDir, "roles.json"), "ARCHIVE-ROLES");
        // The archive's app-settings.json names another file: after the swap the app would open that one.
        var other = Path.Combine(elsewhere, "other.db").Replace('\\', '/');
        File.WriteAllText(Path.Combine(paths.RestoreStagingDir, Path.GetFileName(paths.AppSettingsJson)),
            JsonSerializer.Serialize(new { Database = new { ConnectionString = $"Data Source={other}" } }));
        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                RestoreBootHandler.ApplyPendingRestore(paths, Config(new() { ["WHISKERS_DB_PROVIDER"] = "sqlite" })));

            Assert.Contains("another database", thrown.Message);
            Assert.True(File.Exists(paths.RestorePendingMarker));               // pending: the next start says it again
            Assert.Equal("LIVE-DB", File.ReadAllText(paths.DbPath));
            Assert.Equal("LIVE-ROLES", File.ReadAllText(Path.Combine(root, "roles.json")));
            Assert.False(File.Exists(other));
        }
        finally { Delete(root, elsewhere); }
    }

    [Fact]
    public void A_sqlite_restore_on_a_start_configured_for_postgres_is_refused()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), $"sw-restore-elsewhere-{Guid.NewGuid():N}");
        Directory.CreateDirectory(elsewhere);
        var recorded = Path.Combine(elsewhere, "whiskers.db");
        File.WriteAllText(recorded, "LIVE-DB");
        var paths = StageRestore(out var root, recorded);
        try
        {
            Assert.Throws<InvalidOperationException>(() => RestoreBootHandler.ApplyPendingRestore(paths, Config(new()
            {
                ["WHISKERS_DB_PROVIDER"] = "postgres", ["WHISKERS_DB_CONNECTION"] = "Host=db;Database=whiskers;Username=w",
            })));

            Assert.True(File.Exists(paths.RestorePendingMarker));
            Assert.Equal("LIVE-DB", File.ReadAllText(recorded));                // the marker's path was not written
        }
        finally { Delete(root, elsewhere); }
    }

    [Fact]
    public void A_start_without_a_restore_sweeps_the_swap_copy_beside_a_custom_database()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), $"sw-restore-elsewhere-{Guid.NewGuid():N}");
        Directory.CreateDirectory(elsewhere);
        var root = Path.Combine(Path.GetTempPath(), $"sw-restore-custom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var custom = Path.Combine(elsewhere, "whiskers.db");
        File.WriteAllText(custom, "LIVE-DB");
        File.WriteAllText(custom + ".__restoring", "HALF-COPIED");          // a swap given up half-way
        try
        {
            RestoreBootHandler.ApplyPendingRestore(new DataPathOptions(root), Config(new()
            {
                ["WHISKERS_DB_PROVIDER"] = "sqlite", ["WHISKERS_DB_CONNECTION"] = $"Data Source={custom}",
            }));

            Assert.False(File.Exists(custom + ".__restoring"));
            Assert.Equal("LIVE-DB", File.ReadAllText(custom));
        }
        finally { Delete(root, elsewhere); }
    }
}
