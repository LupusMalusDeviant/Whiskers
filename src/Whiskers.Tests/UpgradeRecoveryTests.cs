using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>R1-34: a schema upgrade starts from a recovery point (a pre-upgrade backup of the database as it was), a
/// failed backup stops the start before any migration, and the way back needs no running UI (the CLI restore).</summary>
public sealed class UpgradeRecoveryTests : IDisposable
{
    private readonly List<(ServiceProvider Sp, string Root)> _envs = [];

    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class ListLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed record Env(BackupService Backups, DataPathOptions Paths, ServiceProvider Sp, IConfiguration Config);

    private enum Start { Fresh, Legacy, OneBehind, Current }

    /// <summary>An app data directory whose database is fresh, a legacy EnsureCreated file, one metrics migration behind
    /// this release, or current — with a row written before the upgrade.</summary>
    private Env NewEnv(Start start, Dictionary<string, string?>? settings = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sw-upg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new DataPathOptions(root);
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={paths.DbPath}",
            sql => sql.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly)));
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={paths.DbPath}", sql =>
        {
            sql.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly);
            sql.MigrationsHistoryTable(DatabaseRegistration.IdentityMigrationsHistoryTable);
        }));
        var sp = services.BuildServiceProvider();
        _envs.Add((sp, root));

        using (var scope = sp.CreateScope())
        {
            var metrics = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            switch (start)
            {
                case Start.Legacy:
                    metrics.Database.EnsureCreated();
                    break;
                case Start.OneBehind:
                    metrics.Database.Migrate(metrics.Database.GetMigrations().ToList()[^2]);
                    scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.Migrate();
                    break;
                case Start.Current:
                    metrics.Database.Migrate();
                    scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.Migrate();
                    break;
            }
            if (start != Start.Fresh)
            {
                metrics.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "before", ContainerName = "web", ServerId = "s1", Timestamp = DateTime.UtcNow });
                metrics.SaveChanges();
            }
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var backups = new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), new MaintenanceStateService(),
            new NoopLifetime(), config, NullLogger<BackupService>.Instance, paths);
        return new Env(backups, paths, sp, config);
    }

    private static Task<UpgradePlan> PrepareAsync(Env env, ILogger? logger = null) =>
        UpgradeGuard.PrepareAsync(env.Sp.GetRequiredService<IServiceScopeFactory>(), env.Backups, env.Config,
            logger ?? NullLogger.Instance);

    private static async Task MigrateAsync(Env env)
    {
        using var scope = env.Sp.CreateScope();
        await DatabaseInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), NullLogger.Instance);
        await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.MigrateAsync();
    }

    private static async Task<List<string>> AppliedAsync(Env env)
    {
        using var scope = env.Sp.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.GetAppliedMigrationsAsync()).ToList();
    }

    [Fact]
    public async Task An_upgrade_first_backs_up_the_database_as_it_was_before_any_migration()
    {
        var env = NewEnv(Start.OneBehind);
        var before = await AppliedAsync(env);
        using (var scope = env.Sp.CreateScope())
            Assert.Single(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.GetPendingMigrationsAsync());

        var plan = await PrepareAsync(env);

        Assert.NotNull(plan.BackupId);
        Assert.Single(plan.PendingMetrics);
        Assert.Equal(before, await AppliedAsync(env));   // the guard itself migrates nothing
        var backup = Assert.Single(await env.Backups.ListBackupsAsync());
        Assert.Equal(plan.BackupId, backup.Id);
        Assert.StartsWith(BackupService.PreUpgradeLabelPrefix, backup.Label);
        // The archive holds the schema from before the upgrade, not after it.
        var manifest = (await env.Backups.ValidateArchiveAsync(env.Backups.ResolveArchivePath(backup.Id)!)).Manifest!;
        Assert.Equal(before, manifest.MetricsMigrations);
    }

    [Fact]
    public async Task A_legacy_database_without_a_migration_history_is_backed_up_too()
    {
        var env = NewEnv(Start.Legacy);
        var plan = await PrepareAsync(env);
        Assert.False(plan.FreshDatabase);
        Assert.NotNull(plan.BackupId);
    }

    [Fact]
    public async Task A_new_database_and_a_current_one_take_no_backup()
    {
        var fresh = await PrepareAsync(NewEnv(Start.Fresh));
        Assert.True(fresh.FreshDatabase);
        Assert.True(fresh.HasPending);
        Assert.Null(fresh.BackupId);

        var currentEnv = NewEnv(Start.Current);
        var current = await PrepareAsync(currentEnv);
        Assert.False(current.HasPending);
        Assert.Null(current.BackupId);
        Assert.Empty(await currentEnv.Backups.ListBackupsAsync());
    }

    private sealed class FailingBackups(IBackupService inner) : IBackupService
    {
        public Task<SelfBackupInfo> CreateBackupAsync(string label, CancellationToken ct = default) =>
            throw new IOException("No space left on device");
        public Task<IReadOnlyList<SelfBackupInfo>> ListBackupsAsync(CancellationToken ct = default) => inner.ListBackupsAsync(ct);
        public string? ResolveArchivePath(string id) => inner.ResolveArchivePath(id);
        public Task DeleteBackupAsync(string id, CancellationToken ct = default) => inner.DeleteBackupAsync(id, ct);
        public Task PruneAsync(int keepNewest, CancellationToken ct = default) => inner.PruneAsync(keepNewest, ct);
        public Task<string> StageUploadAsync(Stream source, CancellationToken ct = default) => inner.StageUploadAsync(source, ct);
        public Task DiscardUploadAsync(string uploadPath, CancellationToken ct = default) => inner.DiscardUploadAsync(uploadPath, ct);
        public Task<BackupValidationResult> ValidateArchiveAsync(string uploadedPath, CancellationToken ct = default) =>
            inner.ValidateArchiveAsync(uploadedPath, ct);
        public Task BeginRestoreAsync(string uploadedPath, string actor, CancellationToken ct = default) =>
            inner.BeginRestoreAsync(uploadedPath, actor, ct);
    }

    [Fact]
    public async Task A_failed_pre_upgrade_backup_stops_the_upgrade_and_says_how_to_go_on()
    {
        var env = NewEnv(Start.OneBehind);
        var before = await AppliedAsync(env);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => UpgradeGuard.PrepareAsync(
            env.Sp.GetRequiredService<IServiceScopeFactory>(), new FailingBackups(env.Backups), env.Config, NullLogger.Instance));

        Assert.Contains("before any migration", ex.Message);
        Assert.Contains("No space left on device", ex.Message);
        Assert.Contains(UpgradeGuard.SkipBackupEnv, ex.Message);
        Assert.Equal(before, await AppliedAsync(env));
    }

    [Fact]
    public async Task The_backup_is_skipped_only_when_the_operator_says_so_and_that_is_logged()
    {
        var env = NewEnv(Start.OneBehind, new() { [UpgradeGuard.SkipBackupEnv] = "true" });
        var logger = new ListLogger();

        var plan = await UpgradeGuard.PrepareAsync(env.Sp.GetRequiredService<IServiceScopeFactory>(),
            new FailingBackups(env.Backups), env.Config, logger);

        Assert.Null(plan.BackupId);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("WITHOUT a pre-upgrade backup"));
    }

    [Fact]
    public async Task A_failed_migration_names_the_pre_upgrade_backup_to_restore_and_fails_the_start()
    {
        var plan = new UpgradePlan(["20990101000000_Next"], [], FreshDatabase: false, BackupId: "abc123def456");
        var logger = new ListLogger();

        await Assert.ThrowsAsync<InvalidOperationException>(() => UpgradeGuard.MigrateAsync("metrics", plan, logger,
            () => throw new InvalidOperationException("column exists")));

        var critical = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("--restore-backup abc123def456", critical.Message);
        Assert.Contains("partly migrated", critical.Message);
    }

    [Fact]
    public async Task The_scheduled_retention_keeps_the_pre_upgrade_backups_which_are_pruned_on_their_own()
    {
        var env = NewEnv(Start.Current, new() { [UpgradeGuard.KeepEnv] = "2" });
        var upgrades = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            upgrades.Add((await env.Backups.CreateBackupAsync($"{BackupService.PreUpgradeLabelPrefix}{i}")).Id);
            await Task.Delay(20);
        }
        await env.Backups.CreateBackupAsync("scheduled");
        await Task.Delay(20);
        await env.Backups.CreateBackupAsync("scheduled");

        await env.Backups.PruneAsync(keepNewest: 1);
        var left = await env.Backups.ListBackupsAsync();
        Assert.Equal(4, left.Count);                                     // one scheduled, all three pre-upgrade
        Assert.Single(left, b => b.Label == "scheduled");

        await UpgradeGuard.PruneAsync(env.Backups, env.Config, NullLogger.Instance, default);
        var kept = (await env.Backups.ListBackupsAsync())
            .Where(b => b.Label.StartsWith(BackupService.PreUpgradeLabelPrefix, StringComparison.Ordinal)).Select(b => b.Id).ToList();
        Assert.Equal(upgrades.Skip(1).Reverse(), kept);                  // the newest two
    }

    [Fact]
    public async Task An_upgrade_is_undone_from_the_command_line_without_the_ui()
    {
        var env = NewEnv(Start.OneBehind);
        var before = await AppliedAsync(env);
        var plan = await PrepareAsync(env);
        await MigrateAsync(env);
        using (var scope = env.Sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "after", ContainerName = "web", ServerId = "s1", Timestamp = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.NotEqual(before, await AppliedAsync(env));

        var log = new StringWriter();
        Assert.Equal(0, await RestoreCli.RunAsync(env.Backups, vault: null, RestoreCli.LatestPreUpgrade, log));
        Assert.Contains($"Backup {plan.BackupId} is staged", log.ToString());
        Assert.True(File.Exists(env.Paths.RestorePendingMarker));

        // The next start — of either release — applies it before it opens the database.
        TestSqlite.ClearPools(env.Paths.RootDir);
        RestoreBootHandler.ApplyPendingRestore(env.Paths);

        Assert.Equal(before, await AppliedAsync(env));
        using (var scope = env.Sp.CreateScope())
        {
            var ids = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().ContainerMetrics
                .Select(m => m.ContainerId).ToListAsync();
            Assert.Equal(["before"], ids);
        }
    }

    [Theory]
    [InlineData(null)]                    // no id: the usage text, not a configuration error
    [InlineData(RestoreCli.LatestPreUpgrade)]
    public void The_restore_options_pass_the_host_command_line_configuration(string? id)
    {
        // Program hands the same args to WebApplication.CreateBuilder before it reaches the restore branch.
        string[] args = id is null ? [RestoreCli.Flag] : [RestoreCli.Flag, id];
        var config = new ConfigurationBuilder().AddCommandLine(args).Build();
        Assert.NotNull(config);
    }

    [Fact]
    public async Task The_command_line_restore_names_what_it_cannot_find()
    {
        var env = NewEnv(Start.Current);
        var manual = await env.Backups.CreateBackupAsync("manual");

        var usage = new StringWriter();
        Assert.Equal(2, await RestoreCli.RunAsync(env.Backups, null, "", usage));
        Assert.Contains(manual.Id, usage.ToString());                    // the listing helps pick one

        var missing = new StringWriter();
        Assert.Equal(3, await RestoreCli.RunAsync(env.Backups, null, "000000000000", missing));
        Assert.Contains("No backup with the id", missing.ToString());

        var noUpgrade = new StringWriter();
        Assert.Equal(3, await RestoreCli.RunAsync(env.Backups, null, RestoreCli.LatestPreUpgrade, noUpgrade));
        Assert.Contains("No pre-upgrade backup", noUpgrade.ToString());

        // A path is no backup id: only ids the service resolves inside its own directory are restored.
        Assert.Equal(3, await RestoreCli.RunAsync(env.Backups, null, "../../etc/passwd", new StringWriter()));
        Assert.False(File.Exists(env.Paths.RestorePendingMarker));
    }

    public void Dispose()
    {
        foreach (var (sp, root) in _envs)
        {
            sp.Dispose();
            TestSqlite.ClearPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
