using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>R1-33: the self-backup of a PostgreSQL instance against a REAL PostgreSQL 17 (Testcontainers) with the real
/// client tools — backup, change, restore (staged, dry-run, applied at the start), an off-volume restore into a fresh
/// database and an empty data directory, the refusal of a files-only archive and the all-or-nothing restore. Skipped
/// without Docker; without the client tools skipped locally and failed on CI, which installs them.</summary>
[Trait("Category", "RequiresDocker")]
public class PostgresBackupRestoreTests() : PostgresBackupRestoreTestsBase("postgres:18-alpine");

/// <summary>The same against PostgreSQL 17: installs that have not moved to 18 yet (docs/operations/postgres-18-upgrade.md)
/// keep their backups, and the upgrade dump itself is restored into 18.</summary>
[Trait("Category", "RequiresDocker")]
public class PostgresBackupRestorePg17Tests() : PostgresBackupRestoreTestsBase("postgres:17-alpine");

/// <summary>The same against an older server line than the image's client tools: the script of the newer client must
/// not use settings this server lacks.</summary>
[Trait("Category", "RequiresDocker")]
public class PostgresBackupRestorePg14Tests() : PostgresBackupRestoreTestsBase("postgres:14-alpine");

public abstract class PostgresBackupRestoreTestsBase(string image) : IAsyncLifetime
{
    private const string VaultKey = "test-vault-key";
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder(image).Build();
    private readonly List<string> _roots = [];
    private bool _up;
    private Exception? _startFailure;

    public async Task InitializeAsync()
    {
        _startFailure = await DockerRequirement.TryStartAsync(_pg);
        _up = _startFailure is null;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        foreach (var root in _roots)
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        if (_up) await _pg.DisposeAsync();
    }

    private static string? BinDir => Environment.GetEnvironmentVariable(PostgresDumpTool.BinDirKey);

    private async Task SkipUnlessReadyAsync()
    {
        // CI has Docker and the tools: there a server that does not start, or a missing tool, is a failure, not a skip.
        DockerRequirement.SkipUnlessStarted(_startFailure);
        try { await new PostgresDumpTool(BinDir).CheckReadableAsync(Path.Combine(Path.GetTempPath(), "none")); }
        catch (PostgresToolException ex) when (ex.Message.Contains("is not available", StringComparison.Ordinal))
        {
            DockerRequirement.SkipUnlessAvailable(false, "PostgreSQL client tools not installed");
            throw;
        }
        catch (PostgresToolException) { /* the tool ran and refused the missing file: it is there */ }
    }

    private async Task<string> CreateDatabaseAsync(string name)
    {
        await using (var admin = new NpgsqlConnection(_pg.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Database = name }.ConnectionString;
    }

    private sealed record Instance(BackupService Service, DataPathOptions Paths, ServiceProvider Provider,
        IConfiguration Configuration, string Connection) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    // As DatabaseRegistration registers PostgreSQL, against a data directory of its own; the schema migrated as at a start.
    private async Task<Instance> NewInstanceAsync(string connection)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sw-pgbak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        var paths = new DataPathOptions(root);

        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseNpgsql(connection, npg =>
        {
            npg.EnableRetryOnFailure(3);
            npg.MigrationsAssembly("Whiskers.Migrations.Postgres");
        }), ServiceLifetime.Transient);
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseNpgsql(connection, npg =>
        {
            npg.EnableRetryOnFailure(3);
            npg.MigrationsAssembly("Whiskers.Migrations.Postgres");
            npg.MigrationsHistoryTable("__IdentityMigrationsHistory");
        }));
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
        {
            await DatabaseInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), NullLogger.Instance);
            await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.MigrateAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["VAULT_KEY"] = VaultKey,
            ["WHISKERS_DB_PROVIDER"] = "postgres",
            ["WHISKERS_DB_CONNECTION"] = connection,
        };
        if (BinDir is { } bin) settings[PostgresDumpTool.BinDirKey] = bin;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var svc = new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), new MaintenanceStateService(),
            new NoopLifetime(), config, NullLogger<BackupService>.Instance, paths);
        return new Instance(svc, paths, sp, config, connection);
    }

    private static async Task SeedAsync(Instance instance)
    {
        using var scope = instance.Provider.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
        identity.Users.Add(new AppUser { UserName = "alice", NormalizedUserName = "ALICE", Email = "alice@example.com" });
        await identity.SaveChangesAsync();
        await using var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.ScheduledTasks.Add(new ScheduledTaskEntity { Name = "nightly", CronExpression = "0 2 * * *", CreatedBy = "alice" });
        db.Webhooks.Add(new WebhookEntity { Name = "deploy", TargetId = "web" });
        db.AuditLog.Add(new AuditLogEntity { Actor = "alice", Action = "container.start", TargetType = "container", TargetId = "web" });
        await db.SaveChangesAsync();
        await File.WriteAllTextAsync(instance.Paths.RolesJson, "{\"version\":\"archived\"}");
    }

    private static async Task AssertSeededStateAsync(Instance instance)
    {
        await using var db = new MetricsDbContext(new DbContextOptionsBuilder<MetricsDbContext>()
            .UseNpgsql(instance.Connection).Options);
        Assert.Equal(["nightly"], await db.ScheduledTasks.Select(t => t.Name).ToListAsync());
        Assert.Equal(["deploy"], await db.Webhooks.Select(w => w.Name).ToListAsync());
        Assert.Contains(await db.AuditLog.Select(a => a.Action).ToListAsync(), a => a == "container.start");
        await using var identity = new WhiskersIdentityDbContext(new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
            .UseNpgsql(instance.Connection).Options);
        Assert.Equal(["alice"], await identity.Users.Select(u => u.UserName).ToListAsync());
        Assert.Equal("{\"version\":\"archived\"}", await File.ReadAllTextAsync(instance.Paths.RolesJson));

        // Both migration histories came back, and the identity sequences continue after the restored rows.
        await using var check = new NpgsqlConnection(instance.Connection);
        await check.OpenAsync();
        await using (var count = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM \"__EFMigrationsHistory\") > 0 AND (SELECT count(*) FROM \"__IdentityMigrationsHistory\") > 0", check))
            Assert.True((bool)(await count.ExecuteScalarAsync())!);
        db.ScheduledTasks.Add(new ScheduledTaskEntity { Name = "after-restore", CronExpression = "0 3 * * *" });
        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task A_backup_holds_the_database_and_a_restore_brings_back_database_and_files_together()
    {
        await SkipUnlessReadyAsync();
        using var live = await NewInstanceAsync(await CreateDatabaseAsync("live"));
        await SeedAsync(live);

        var info = await live.Service.CreateBackupAsync("manual");
        Assert.True(info.IncludesDatabase);
        Assert.True(info.Restorable);
        Assert.Equal(SelfBackupFormat.PostgresDumpVersion, info.FormatVersion);
        var listed = Assert.Single(await live.Service.ListBackupsAsync());
        Assert.True(listed.Restorable);   // read back from the sidecar

        // Everything changes after the backup: a new user, a task gone, a webhook renamed, the roles file.
        using (var scope = live.Provider.CreateScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
            identity.Users.Add(new AppUser { UserName = "mallory", NormalizedUserName = "MALLORY" });
            await identity.SaveChangesAsync();
            await using var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.ScheduledTasks.RemoveRange(db.ScheduledTasks);
            (await db.Webhooks.SingleAsync()).Name = "renamed";
            await db.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(live.Paths.RolesJson, "{\"version\":\"live\"}");

        await live.Service.BeginRestoreAsync(live.Service.ResolveArchivePath(info.Id)!, "tester");
        Assert.True(File.Exists(live.Paths.RestorePendingMarker));
        Assert.True(File.Exists(Path.Combine(live.Paths.RestoreStagingDir, SelfBackupFormat.PgDumpEntryName)));
        // The dry run was rolled back: until the start applies the restore, the database is as it was.
        await using (var identity = new WhiskersIdentityDbContext(new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
                         .UseNpgsql(live.Connection).Options))
            Assert.Contains(await identity.Users.Select(u => u.UserName).ToListAsync(), n => n == "mallory");

        NpgsqlConnection.ClearAllPools();
        // A start that restored the database and then stopped before the files were swapped: the next start repeats
        // the whole restore from the unchanged staging.
        await new PostgresDumpTool(BinDir).RestoreAsync(live.Connection,
            Path.Combine(live.Paths.RestoreStagingDir, SelfBackupFormat.PgDumpEntryName));
        RestoreBootHandler.ApplyPendingRestore(live.Paths, live.Configuration);

        Assert.False(File.Exists(live.Paths.RestorePendingMarker));
        Assert.False(File.Exists(Path.Combine(live.Paths.RootDir, SelfBackupFormat.PgDumpEntryName)));   // never a data file
        await AssertSeededStateAsync(live);
    }

    [SkippableFact]
    public async Task An_archive_restores_off_the_volume_into_a_fresh_database_and_an_empty_data_directory()
    {
        await SkipUnlessReadyAsync();
        string archive;
        using (var source = await NewInstanceAsync(await CreateDatabaseAsync("lost")))
        {
            await SeedAsync(source);
            var info = await source.Service.CreateBackupAsync("manual");
            archive = Path.Combine(Path.GetTempPath(), $"sw-pgbak-offvolume-{Guid.NewGuid():N}.tar.gz.enc");
            File.Copy(source.Service.ResolveArchivePath(info.Id)!, archive);
            _roots.Add(archive);
        }

        // A new installation with the same VAULT_KEY: started once (schema migrated), nothing else.
        using var fresh = await NewInstanceAsync(await CreateDatabaseAsync("fresh"));
        string upload;
        await using (var file = File.OpenRead(archive)) upload = await fresh.Service.StageUploadAsync(file);
        await fresh.Service.BeginRestoreAsync(upload, "tester");
        NpgsqlConnection.ClearAllPools();
        RestoreBootHandler.ApplyPendingRestore(fresh.Paths, fresh.Configuration);

        await AssertSeededStateAsync(fresh);
    }

    [SkippableFact]
    public async Task The_round_trip_works_as_the_recommended_owner_role_without_superuser_rights()
    {
        await SkipUnlessReadyAsync();
        // As the guide sets it up: a login role that owns the database (and, before PostgreSQL 15, the public schema,
        // which there belongs to the bootstrap superuser), not a superuser.
        var asAdmin = await CreateDatabaseAsync("owned");
        await ExecuteAsync(_pg.GetConnectionString(), "CREATE ROLE whiskers_app LOGIN PASSWORD 'app-pw' NOSUPERUSER");
        await ExecuteAsync(_pg.GetConnectionString(), "ALTER DATABASE owned OWNER TO whiskers_app");
        await using (var admin = new NpgsqlConnection(asAdmin))
        {
            await admin.OpenAsync();
            if (admin.PostgreSqlVersion.Major < 15)
            {
                await using var schema = new NpgsqlCommand("ALTER SCHEMA public OWNER TO whiskers_app", admin);
                await schema.ExecuteNonQueryAsync();
            }
        }
        var asApp = new NpgsqlConnectionStringBuilder(asAdmin) { Username = "whiskers_app", Password = "app-pw" }.ConnectionString;
        using var live = await NewInstanceAsync(asApp);
        await SeedAsync(live);
        var info = await live.Service.CreateBackupAsync("manual");
        await File.WriteAllTextAsync(live.Paths.RolesJson, "{\"version\":\"live\"}");

        await live.Service.BeginRestoreAsync(live.Service.ResolveArchivePath(info.Id)!, "tester");
        NpgsqlConnection.ClearAllPools();
        RestoreBootHandler.ApplyPendingRestore(live.Paths, live.Configuration);

        await AssertSeededStateAsync(live);
    }

    [SkippableFact]
    public async Task A_script_that_stops_psql_from_reading_is_ended_at_the_time_limit()
    {
        await SkipUnlessReadyAsync();
        // A crafted dump: its own statement timeout, a long sleep, then more script than a pipe holds. Without the time
        // limit ending both ends, the write would wait for the sleep and the schema stay locked.
        var target = await CreateDatabaseAsync("sleeper");
        var filler = string.Concat(Enumerable.Repeat("SELECT 1;\n", 50_000));
        var script = "\\restrict k6\nSET statement_timeout TO 0;\nSELECT pg_sleep(600);\n" + filler + "\\unrestrict k6\n";
        var tools = new PostgresDumpTool(BinDir, TimeSpan.FromSeconds(5));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<PostgresToolException>(() => tools.ApplyScriptAsync(target, script, commit: true));

        Assert.Contains("did not finish", ex.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [SkippableFact]
    public async Task A_postgres_archive_without_the_database_is_refused()
    {
        await SkipUnlessReadyAsync();
        using var live = await NewInstanceAsync(await CreateDatabaseAsync("filesonly"));
        await File.WriteAllTextAsync(live.Paths.RolesJson, "{}");
        var info = await live.Service.CreateBackupAsync("manual");

        // An archive as the versions before R1-33 wrote it: the files and the manifest, no dump.
        var plain = Path.Combine(live.Paths.RootDir, "plain.tar.gz");
        await using (var sealedFs = File.OpenRead(live.Service.ResolveArchivePath(info.Id)!))
        await using (var plainFs = File.Create(plain))
            await BackupArchiveCipher.DecryptAsync(sealedFs, plainFs, VaultKey);
        var filesOnly = Path.Combine(live.Paths.RootDir, "files-only.tar.gz");
        await WithoutEntryAsync(plain, filesOnly, SelfBackupFormat.PgDumpEntryName);
        // Sealed with the instance key like every archive it accepts: what is refused is the missing database.
        var crafted = Path.Combine(live.Paths.RootDir, "files-only.tar.gz.enc");
        await using (var source = File.OpenRead(filesOnly))
        await using (var sealedOut = File.Create(crafted))
            await BackupArchiveCipher.EncryptAsync(source, sealedOut, VaultKey);

        var validation = await live.Service.ValidateArchiveAsync(crafted);
        Assert.False(validation.Ok);
        Assert.Contains("files only", validation.Error!, StringComparison.Ordinal);
        await Assert.ThrowsAsync<BackupValidationException>(() => live.Service.BeginRestoreAsync(crafted, "tester"));
        Assert.False(File.Exists(live.Paths.RestorePendingMarker));
    }

    [SkippableFact]
    public async Task A_restore_that_fails_part_way_leaves_the_database_untouched()
    {
        await SkipUnlessReadyAsync();
        // The dump holds a table of a type the target lacks (an extension is not part of a schema dump): the restore
        // fails after the schema was dropped and some tables were created — and all of it is rolled back.
        var source = await CreateDatabaseAsync("withext");
        await ExecuteAsync(source, WhiskersHistories +
            "CREATE EXTENSION citext; CREATE TABLE a_first (id int); CREATE TABLE names (n citext);");
        var target = await CreateDatabaseAsync("target");
        await ExecuteAsync(target, "CREATE TABLE keep (v text); INSERT INTO keep VALUES ('still here');");

        var tools = new PostgresDumpTool(BinDir);
        var work = Path.Combine(Path.GetTempPath(), $"sw-pgbak-{Guid.NewGuid():N}");
        _roots.Add(work);
        Directory.CreateDirectory(work);
        var dump = Path.Combine(work, "db.pgdump");
        await tools.DumpAsync(source, dump);

        await Assert.ThrowsAsync<PostgresToolException>(() => tools.DryRunAsync(target, dump));
        await Assert.ThrowsAsync<PostgresToolException>(() => tools.RestoreAsync(target, dump));

        await using var check = new NpgsqlConnection(target);
        await check.OpenAsync();
        await using var read = new NpgsqlCommand("SELECT v FROM keep", check);
        Assert.Equal("still here", await read.ExecuteScalarAsync());
        await using var tables = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'public'", check);
        Assert.Equal(1L, await tables.ExecuteScalarAsync());
        Assert.Equal(["db.pgdump"], Directory.GetFiles(work).Select(Path.GetFileName));   // the script never lands on disk
    }

    [SkippableFact]
    public async Task A_dump_whose_history_lags_its_tables_is_refused_before_the_commit()
    {
        await SkipUnlessReadyAsync();
        using var live = await NewInstanceAsync(await CreateDatabaseAsync("lagging"));
        await SeedAsync(live);
        // A history behind its tables (the manifest is read from the same history, so it agrees): the start would
        // run the missing migration against the tables that exist and stop, at every start.
        await ExecuteAsync(live.Connection,
            "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = (SELECT max(\"MigrationId\") FROM \"__EFMigrationsHistory\")");
        var info = await live.Service.CreateBackupAsync("manual");

        var ex = await Assert.ThrowsAsync<BackupValidationException>(() =>
            live.Service.BeginRestoreAsync(live.Service.ResolveArchivePath(info.Id)!, "tester"));
        Assert.Contains("cannot be restored", ex.Message, StringComparison.Ordinal);
        // Any refused dry run names the safety backup that holds the database from before it.
        var safety = Assert.Single(await live.Service.ListBackupsAsync(), b => b.Label.StartsWith("pre-restore-", StringComparison.Ordinal));
        Assert.Contains(safety.Id, ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(live.Paths.RestorePendingMarker));
        await using var db = new MetricsDbContext(new DbContextOptionsBuilder<MetricsDbContext>().UseNpgsql(live.Connection).Options);
        Assert.Equal(["deploy"], await db.Webhooks.Select(w => w.Name).ToListAsync());   // the dry run changed nothing
    }

    [SkippableFact]
    public async Task An_upgrade_on_postgres_first_takes_a_backup_with_the_dump_of_the_schema_before_it()
    {
        await SkipUnlessReadyAsync();
        using var live = await NewInstanceAsync(await CreateDatabaseAsync("upgrade"));
        await SeedAsync(live);
        List<string> before;
        // The database as the release before the newest migration left it.
        using (var scope = live.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db).MigrateAsync(applied[^2]);
            before = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        }
        await File.WriteAllTextAsync(live.Paths.LastStartedVersionFile, "1.9.0");
        var guard = new UpgradeGuard(live.Provider.GetRequiredService<IServiceScopeFactory>(), live.Service,
            live.Configuration, live.Paths, NullLogger.Instance, "2.0.0");

        var plan = await guard.PrepareAsync();

        // Pending migrations are found across both history tables on PostgreSQL, and nothing was migrated yet.
        Assert.Single(plan.PendingMetrics);
        Assert.Empty(plan.PendingIdentity);
        Assert.NotNull(plan.BackupId);
        var backup = Assert.Single(await live.Service.ListBackupsAsync());
        Assert.StartsWith(BackupService.PreUpgradeLabelPrefix, backup.Label);
        Assert.True(backup.IncludesDatabase);
        var manifest = (await live.Service.ValidateArchiveAsync(live.Service.ResolveArchivePath(backup.Id)!)).Manifest!;
        Assert.Equal(before, manifest.MetricsMigrations);
        Assert.NotEmpty(manifest.IdentityMigrations);
        Assert.Equal(backup.Id, UpgradeGuard.ReadMarker(live.Paths)!.BackupId);
        using (var scope = live.Provider.CreateScope())
            Assert.Equal(before, (await scope.ServiceProvider.GetRequiredService<MetricsDbContext>()
                .Database.GetAppliedMigrationsAsync()).ToList());
    }

    [SkippableFact]
    public async Task An_archive_from_an_older_schema_is_restored_and_brought_up_to_date_by_the_start()
    {
        await SkipUnlessReadyAsync();
        using var live = await NewInstanceAsync(await CreateDatabaseAsync("older"));
        // The database as the build before the newest migration left it.
        using (var scope = live.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.True(applied.Count >= 2);
            await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db).MigrateAsync(applied[^2]);
        }
        var info = await live.Service.CreateBackupAsync("manual");

        await live.Service.BeginRestoreAsync(live.Service.ResolveArchivePath(info.Id)!, "tester");
        NpgsqlConnection.ClearAllPools();
        RestoreBootHandler.ApplyPendingRestore(live.Paths, live.Configuration);

        using (var scope = live.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            Assert.Single(await db.Database.GetPendingMigrationsAsync());   // the archive's schema, one behind
            await DatabaseInitializer.InitializeAsync(db, NullLogger.Instance);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Empty(await db.Webhooks.ToListAsync());
        }
    }

    [SkippableFact]
    public async Task A_dump_missing_a_column_this_version_uses_is_refused_before_the_commit()
    {
        await SkipUnlessReadyAsync();
        using var live = await NewInstanceAsync(await CreateDatabaseAsync("nocolumn"));
        await SeedAsync(live);
        // The histories are complete, but a mapped column is gone: the app would fail on its first read of webhooks.
        await ExecuteAsync(live.Connection, "ALTER TABLE \"Webhooks\" DROP COLUMN \"Owner\"");
        var info = await live.Service.CreateBackupAsync("manual");

        var ex = await Assert.ThrowsAsync<BackupValidationException>(() =>
            live.Service.BeginRestoreAsync(live.Service.ResolveArchivePath(info.Id)!, "tester"));
        Assert.Contains("cannot be restored", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(live.Paths.RestorePendingMarker));
    }

    // The two tables a dump of Whiskers' database must hold (PostgresDumpTool.RequiredTables).
    private const string WhiskersHistories =
        "CREATE TABLE \"__EFMigrationsHistory\" (id int); CREATE TABLE \"__IdentityMigrationsHistory\" (id int); ";

    [SkippableFact]
    public async Task A_restore_that_fails_at_its_first_statement_returns_at_once_with_the_reason()
    {
        await SkipUnlessReadyAsync();
        // A dump far larger than a pipe's buffer, restored by a role that does not own the schema: psql stops at
        // DROP SCHEMA, and pg_restore, still writing, must not keep the restore waiting for the time limit.
        var source = await CreateDatabaseAsync("large");
        await ExecuteAsync(source, WhiskersHistories +
            "CREATE TABLE bulk AS SELECT g AS id, repeat(md5(g::text), 8) AS filler FROM generate_series(1, 200000) g;");
        var target = await CreateDatabaseAsync("notmine");
        await ExecuteAsync(_pg.GetConnectionString(), "CREATE ROLE stranger LOGIN PASSWORD 'stranger-pw'");
        var asStranger = new NpgsqlConnectionStringBuilder(target) { Username = "stranger", Password = "stranger-pw" }.ConnectionString;

        var work = Path.Combine(Path.GetTempPath(), $"sw-pgbak-{Guid.NewGuid():N}");
        _roots.Add(work);
        Directory.CreateDirectory(work);
        var dump = Path.Combine(work, "db.pgdump");
        await new PostgresDumpTool(BinDir).DumpAsync(source, dump);

        var tools = new PostgresDumpTool(BinDir, TimeSpan.FromMinutes(5));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<PostgresToolException>(() => tools.DryRunAsync(asStranger, dump));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}");
        Assert.Contains("psql failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("owner", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_dump_cut_off_on_the_volume_is_not_committed_half_way()
    {
        await SkipUnlessReadyAsync();
        var source = await CreateDatabaseAsync("cutoff");
        await ExecuteAsync(source, WhiskersHistories +
            "CREATE TABLE bulk AS SELECT g AS id, repeat(md5(g::text), 8) AS filler FROM generate_series(1, 100000) g;");
        var target = await CreateDatabaseAsync("cutoff_target");
        await ExecuteAsync(target, "CREATE TABLE keep (v text); INSERT INTO keep VALUES ('still here');");
        var work = Path.Combine(Path.GetTempPath(), $"sw-pgbak-{Guid.NewGuid():N}");
        _roots.Add(work);
        Directory.CreateDirectory(work);
        var dump = Path.Combine(work, "db.pgdump");
        await new PostgresDumpTool(BinDir).DumpAsync(source, dump);
        // The table of contents stays readable, the data ends half way.
        await using (var file = new FileStream(dump, FileMode.Open)) file.SetLength(file.Length / 2);

        await Assert.ThrowsAsync<PostgresToolException>(() => new PostgresDumpTool(BinDir).RestoreAsync(target, dump));

        await using var check = new NpgsqlConnection(target);
        await check.OpenAsync();
        await using var read = new NpgsqlCommand("SELECT v FROM keep", check);
        Assert.Equal("still here", await read.ExecuteScalarAsync());
    }

    [SkippableTheory]
    // A marker the script can reach (a setting) would survive its own COMMIT; the transaction id kept in a psql
    // variable does not.
    [InlineData("SELECT pg_catalog.set_config('whiskers.restore_tx', 'x', false);\nCOMMIT;\nBEGIN;\n")]
    // An operator of its own that makes every bigint comparison true, found first through the search path it sets.
    [InlineData("COMMIT;\nBEGIN;\n" +
        "CREATE FUNCTION public.t(bigint, bigint) RETURNS boolean LANGUAGE sql AS 'SELECT true';\n" +
        "CREATE OPERATOR public.= (LEFTARG = bigint, RIGHTARG = bigint, FUNCTION = public.t);\n" +
        "SET search_path = public, pg_catalog;\n")]
    public async Task A_script_that_ends_its_transaction_on_its_own_is_refused(string trick)
    {
        await SkipUnlessReadyAsync();
        // What a crafted dump could print as an object's definition: its own COMMIT, then a new transaction.
        var target = await CreateDatabaseAsync("hostile");
        var tools = new PostgresDumpTool(BinDir);
        const string honest = "\\restrict k1\nCREATE TABLE t (id int);\n\\unrestrict k1\n";
        var hostile = "\\restrict k2\nCREATE TABLE t (id int);\n" + trick + "\\unrestrict k2\n";

        await tools.ApplyScriptAsync(target, honest, commit: false);   // the same transaction: accepted, rolled back
        var ex = await Assert.ThrowsAsync<PostgresToolException>(() => tools.ApplyScriptAsync(target, hostile, commit: false));
        Assert.Contains("ended its transaction early", ex.Message, StringComparison.Ordinal);
        Assert.False(await tools.ApplyScriptAsync(target, hostile, commit: true).ContinueWith(t => t.IsCompletedSuccessfully));
    }

    [SkippableTheory]
    [InlineData("\\! touch {0}\n")]                         // a shell command
    [InlineData("\\set whiskers_xid 0\n")]                   // the guard's variable
    [InlineData("SELECT 1 AS whiskers_same \\gset\n")]       // the guard's verdict
    public async Task A_meta_command_inside_the_protected_script_is_refused_by_psql(string metaCommand)
    {
        await SkipUnlessReadyAsync();
        // The defence against \! and the guard both rest on psql's restricted mode (\restrict, CVE-2025-8714): proven here
        // against the client tools in use, not assumed.
        var target = await CreateDatabaseAsync("restricted");
        var marker = Path.Combine(Path.GetTempPath(), $"sw-pgbak-owned-{Guid.NewGuid():N}");
        _roots.Add(marker);
        var script = "\\restrict k3\nCREATE TABLE t (id int);\n" + string.Format(metaCommand, marker) + "\\unrestrict k3\n";

        await Assert.ThrowsAsync<PostgresToolException>(() => new PostgresDumpTool(BinDir).ApplyScriptAsync(target, script, commit: true));

        Assert.False(File.Exists(marker));
        await using var check = new NpgsqlConnection(target);
        await check.OpenAsync();
        await using var tables = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'public'", check);
        Assert.Equal(0L, await tables.ExecuteScalarAsync());   // nothing committed
    }

    [SkippableTheory]
    // A changed default search path, pointing the app at a schema the attacker controls.
    [InlineData("ALTER ROLE CURRENT_USER SET search_path = other, public;\n")]
    [InlineData("DO $d$ BEGIN EXECUTE pg_catalog.format('ALTER DATABASE %I SET search_path = other, public', pg_catalog.current_database()); END $d$;\n")]
    // A schema named like the role: first in the default search path "$user", public, its tables shadow Whiskers' own.
    [InlineData("DO $d$ BEGIN EXECUTE pg_catalog.format('CREATE SCHEMA %I', current_user); " +
        "EXECUTE pg_catalog.format('CREATE TABLE %I.\"Webhooks\" (id int)', current_user); END $d$;\n")]
    // A table or trigger code added to a schema that is already there.
    [InlineData("CREATE TABLE other.\"AspNetUsers\" (id int);\n")]
    [InlineData("CREATE FUNCTION other.f() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END';\n" +
        "CREATE TRIGGER t_ins BEFORE INSERT ON other.keep FOR EACH ROW EXECUTE FUNCTION other.f();\n")]
    public async Task A_dump_that_changes_settings_or_objects_outside_public_is_refused(string change)
    {
        await SkipUnlessReadyAsync();
        var target = await CreateDatabaseAsync("outside");
        await ExecuteAsync(target, "CREATE SCHEMA other; CREATE TABLE other.keep (id int);");
        var tools = new PostgresDumpTool(BinDir);
        const string honest = "\\restrict k5\nCREATE TABLE public.t (id int);\n\\unrestrict k5\n";
        await tools.ApplyScriptAsync(target, honest, commit: false);   // the other schema as it was: accepted

        var script = "\\restrict k5\nCREATE TABLE public.t (id int);\n" + change + "\\unrestrict k5\n";
        var ex = await Assert.ThrowsAsync<PostgresToolException>(() => tools.ApplyScriptAsync(target, script, commit: true));

        Assert.Contains("outside the public schema", ex.Message, StringComparison.Ordinal);
        await using var check = new NpgsqlConnection(target);
        await check.OpenAsync();
        await using var tables = new NpgsqlCommand(
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog', 'information_schema')", check);
        Assert.Equal(1L, await tables.ExecuteScalarAsync());   // only other.keep: nothing committed
        await using var settings = new NpgsqlCommand("SELECT count(*) FROM pg_db_role_setting WHERE setconfig::text LIKE '%other%'", check);
        Assert.Equal(0L, await settings.ExecuteScalarAsync());
    }

    [SkippableTheory]
    [InlineData("")]           // the tail taken as COPY data, psql ends with 0 at the end of the input
    [InlineData("COMMIT;\n")]  // and what the script did before is committed by its own COMMIT, past every check
    public async Task A_script_that_leaves_psql_reading_copy_data_is_not_taken_as_restored(string before)
    {
        await SkipUnlessReadyAsync();
        var target = await CreateDatabaseAsync("sink");
        // What a crafted entry could end with; the whole tail (checks, COMMIT) would follow as rows of public.sink.
        var script = "\\restrict k7\n" + before + "CREATE TABLE public.sink (t text);\nCOPY public.sink FROM stdin;\n\\unrestrict k7\n";

        await Assert.ThrowsAsync<PostgresToolException>(() => new PostgresDumpTool(BinDir).ApplyScriptAsync(target, script, commit: true));

        await using var check = new NpgsqlConnection(target);
        await check.OpenAsync();
        await using var tables = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE tablename = 'sink'", check);
        Assert.Equal(0L, await tables.ExecuteScalarAsync());
    }

    [SkippableTheory]
    [InlineData("CREATE FUNCTION public.f() RETURNS int LANGUAGE sql AS 'SELECT 1';\n")]
    [InlineData("CREATE FUNCTION evil() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END';\n" +
        "CREATE TRIGGER t_ins BEFORE INSERT ON public.t FOR EACH ROW EXECUTE FUNCTION evil();\n")]
    [InlineData("CREATE VIEW public.v AS SELECT id FROM public.t;\n")]
    [InlineData("CREATE RULE r AS ON INSERT TO public.t DO INSTEAD NOTHING;\n")]
    [InlineData("ALTER TABLE public.t ENABLE ROW LEVEL SECURITY;\nCREATE POLICY p ON public.t USING (true);\n")]
    // What a dump without owners and grants never holds.
    [InlineData("GRANT SELECT, INSERT ON public.t TO PUBLIC;\n")]
    [InlineData("GRANT USAGE ON SCHEMA public TO PUBLIC;\n")]
    [InlineData("CREATE UNLOGGED TABLE public.u (id int);\n")]
    public async Task A_dump_that_brings_code_into_public_is_refused(string code)
    {
        await SkipUnlessReadyAsync();
        var target = await CreateDatabaseAsync("code");
        var script = "\\restrict k4\nSET search_path = public;\nCREATE TABLE public.t (id int);\n" + code + "\\unrestrict k4\n";

        var ex = await Assert.ThrowsAsync<PostgresToolException>(() =>
            new PostgresDumpTool(BinDir).ApplyScriptAsync(target, script, commit: true));

        Assert.Contains("Whiskers does not create", ex.Message, StringComparison.Ordinal);
        await using var check = new NpgsqlConnection(target);
        await check.OpenAsync();
        await using var tables = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'public'", check);
        Assert.Equal(0L, await tables.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task A_dump_without_whiskers_tables_in_public_fails_the_backup()
    {
        await SkipUnlessReadyAsync();
        // Tables in another schema (a search path in the connection string): the dump of public would be empty.
        var elsewhere = await CreateDatabaseAsync("elsewhere");
        await ExecuteAsync(elsewhere, "CREATE SCHEMA app; CREATE TABLE app.\"__EFMigrationsHistory\" (id int);");
        var work = Path.Combine(Path.GetTempPath(), $"sw-pgbak-{Guid.NewGuid():N}");
        _roots.Add(work);
        Directory.CreateDirectory(work);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PostgresDumpTool(BinDir).DumpAsync(elsewhere, Path.Combine(work, "db.pgdump")));
        Assert.Contains("__EFMigrationsHistory", ex.Message, StringComparison.Ordinal);
    }

    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task WithoutEntryAsync(string sourceTarGz, string targetTarGz, string entryName)
    {
        await using var input = File.OpenRead(sourceTarGz);
        await using var gunzip = new GZipStream(input, CompressionMode.Decompress);
        await using var reader = new TarReader(gunzip);
        await using var output = File.Create(targetTarGz);
        await using var gzip = new GZipStream(output, CompressionLevel.Fastest);
        await using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        while (await reader.GetNextEntryAsync(copyData: true) is { } entry)
            if (entry.Name != entryName) await writer.WriteEntryAsync(entry);
    }

    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
