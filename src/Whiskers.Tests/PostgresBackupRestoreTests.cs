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
public class PostgresBackupRestoreTests() : PostgresBackupRestoreTestsBase("postgres:17-alpine");

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

    public async Task InitializeAsync()
    {
        try { await _pg.StartAsync(); _up = true; }
        catch { _up = false; } // no Docker daemon → the tests below Skip instead of failing
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
        Skip.IfNot(_up, "Docker daemon not available");
        try { await new PostgresDumpTool(BinDir).CheckReadableAsync(Path.Combine(Path.GetTempPath(), "none")); }
        catch (PostgresToolException ex) when (ex.Message.Contains("is not available", StringComparison.Ordinal))
        {
            // CI installs the tools: there a missing tool is a failure, not a skip.
            Skip.If(Environment.GetEnvironmentVariable("CI") is null, "PostgreSQL client tools not installed");
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
        var crafted = Path.Combine(live.Paths.RootDir, "files-only.tar.gz");
        await WithoutEntryAsync(plain, crafted, SelfBackupFormat.PgDumpEntryName);

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
        await ExecuteAsync(source, "CREATE EXTENSION citext; CREATE TABLE a_first (id int); CREATE TABLE names (n citext);");
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
        Assert.False(File.Exists(live.Paths.RestorePendingMarker));
        await using var db = new MetricsDbContext(new DbContextOptionsBuilder<MetricsDbContext>().UseNpgsql(live.Connection).Options);
        Assert.Equal(["deploy"], await db.Webhooks.Select(w => w.Name).ToListAsync());   // the dry run changed nothing
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
