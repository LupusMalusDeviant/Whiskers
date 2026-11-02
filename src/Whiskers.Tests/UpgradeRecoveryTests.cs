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

/// <summary>R1-34: an upgrade starts from a recovery point (a pre-upgrade backup of the state as it was), a failed backup
/// stops the start before any migration, a start after a failed migration keeps that backup, and the way back needs no
/// running UI (the CLI restore), also when this release's migration is what fails.</summary>
public sealed class UpgradeRecoveryTests : IDisposable
{
    private const string ThisRelease = "2.0.0+test";
    private const string PreviousRelease = "1.9.0+test";

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

    private sealed record Env(BackupService Backups, DataPathOptions Paths, ServiceProvider Sp, IConfiguration Config)
    {
        public UpgradeGuard Guard(IBackupService? backups = null, ILogger? logger = null, string version = ThisRelease) =>
            new(Sp.GetRequiredService<IServiceScopeFactory>(), backups ?? Backups, Config, Paths,
                logger ?? NullLogger.Instance, version);
    }

    /// <summary>Fresh; a legacy EnsureCreated file; one metrics migration behind; one behind with a table in the way of
    /// that migration (it fails on this data); identity never migrated; current.</summary>
    private enum Start { Fresh, Legacy, OneBehind, OneBehindBroken, IdentityPending, Current }

    /// <summary>An app data directory in the given state, last started by <paramref name="lastRelease"/> (none: a release
    /// from before the version file), with a row written before the upgrade.</summary>
    private Env NewEnv(Start start, string? lastRelease = PreviousRelease, Dictionary<string, string?>? settings = null)
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
            var identity = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
            switch (start)
            {
                case Start.Legacy:
                    metrics.Database.EnsureCreated();
                    break;
                case Start.OneBehind:
                case Start.OneBehindBroken:
                    metrics.Database.Migrate(metrics.Database.GetMigrations().ToList()[^2]);
                    identity.Database.Migrate();
                    // The last migration creates AuditChain: a table already there makes it fail, every time.
                    if (start == Start.OneBehindBroken)
                        metrics.Database.ExecuteSqlRaw("CREATE TABLE \"AuditChain\" (\"x\" INTEGER)");
                    break;
                case Start.IdentityPending:
                    metrics.Database.Migrate();
                    break;
                case Start.Current:
                    metrics.Database.Migrate();
                    identity.Database.Migrate();
                    break;
            }
            if (start != Start.Fresh)
            {
                metrics.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "before", ContainerName = "web", ServerId = "s1", Timestamp = DateTime.UtcNow });
                metrics.SaveChanges();
            }
        }
        if (lastRelease is not null) File.WriteAllText(paths.LastStartedVersionFile, lastRelease);

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var backups = new BackupService(sp.GetRequiredService<IServiceScopeFactory>(), new MaintenanceStateService(),
            new NoopLifetime(), config, NullLogger<BackupService>.Instance, paths);
        return new Env(backups, paths, sp, config);
    }

    /// <summary>The start's migrations, as the pipeline runs them after the guard; <see cref="UpgradeGuard.CompleteAsync"/>
    /// only when both finished.</summary>
    private static async Task StartAsync(Env env, UpgradeGuard guard)
    {
        var plan = await guard.PrepareAsync();
        using (var scope = env.Sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            await UpgradeGuard.MigrateAsync("metrics", plan.PendingMetrics, plan, NullLogger.Instance,
                () => DatabaseInitializer.InitializeAsync(db, NullLogger.Instance));
        }
        using (var scope = env.Sp.CreateScope())
        {
            var id = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
            await UpgradeGuard.MigrateAsync("identity", plan.PendingIdentity, plan, NullLogger.Instance, () => id.Database.MigrateAsync());
        }
        await guard.CompleteAsync();
    }

    private static async Task<List<string>> AppliedAsync(Env env)
    {
        using var scope = env.Sp.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.GetAppliedMigrationsAsync()).ToList();
    }

    private static async Task<List<SelfBackupInfo>> PreUpgradeAsync(Env env) =>
        (await env.Backups.ListBackupsAsync())
        .Where(b => b.Label.StartsWith(BackupService.PreUpgradeLabelPrefix, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task An_upgrade_first_backs_up_the_database_as_it_was_before_any_migration()
    {
        var env = NewEnv(Start.OneBehind);
        var before = await AppliedAsync(env);

        var plan = await env.Guard().PrepareAsync();

        Assert.NotNull(plan.BackupId);
        Assert.Single(plan.PendingMetrics);
        Assert.Equal(before, await AppliedAsync(env));   // the guard itself migrates nothing
        var backup = Assert.Single(await env.Backups.ListBackupsAsync());
        Assert.Equal(plan.BackupId, backup.Id);
        Assert.StartsWith(BackupService.PreUpgradeLabelPrefix, backup.Label);
        // The archive holds the schema from before the upgrade, not after it.
        var manifest = (await env.Backups.ValidateArchiveAsync(env.Backups.ResolveArchivePath(backup.Id)!)).Manifest!;
        Assert.Equal(before, manifest.MetricsMigrations);
        Assert.Equal(backup.Id, UpgradeGuard.ReadMarker(env.Paths)!.BackupId);
    }

    [Fact]
    public async Task A_pending_identity_migration_alone_is_an_upgrade_too()
    {
        var env = NewEnv(Start.IdentityPending);
        var plan = await env.Guard().PrepareAsync();
        Assert.Empty(plan.PendingMetrics);
        Assert.NotEmpty(plan.PendingIdentity);
        Assert.NotNull(plan.BackupId);
    }

    [Fact]
    public async Task A_new_release_without_migrations_is_an_upgrade_too_its_files_may_change()
    {
        var env = NewEnv(Start.Current, lastRelease: PreviousRelease);
        var plan = await env.Guard().PrepareAsync();
        Assert.False(plan.HasPending);
        Assert.NotNull(plan.BackupId);

        // A database from before the version file is treated the same way.
        Assert.NotNull((await NewEnv(Start.Current, lastRelease: null).Guard().PrepareAsync()).BackupId);
    }

    [Fact]
    public async Task A_legacy_database_without_a_migration_history_is_backed_up_too()
    {
        var plan = await NewEnv(Start.Legacy).Guard().PrepareAsync();
        Assert.False(plan.FreshDatabase);
        Assert.NotNull(plan.BackupId);
    }

    [Fact]
    public async Task A_new_database_and_a_restart_of_the_same_release_take_no_backup()
    {
        var freshEnv = NewEnv(Start.Fresh, lastRelease: null);
        var fresh = await freshEnv.Guard().PrepareAsync();
        Assert.True(fresh.FreshDatabase);
        Assert.Null(fresh.BackupId);
        Assert.Empty(await freshEnv.Backups.ListBackupsAsync());

        var env = NewEnv(Start.Current, lastRelease: ThisRelease);
        var current = await env.Guard().PrepareAsync();
        Assert.Null(current.BackupId);
        // A rebuild of the same release (another commit after the "+") is no upgrade either.
        Assert.Null((await env.Guard(version: "2.0.0+rebuilt").PrepareAsync()).BackupId);
        Assert.Empty(await env.Backups.ListBackupsAsync());
    }

    [Fact]
    public async Task A_finished_upgrade_records_the_release_and_the_next_start_takes_no_backup()
    {
        var env = NewEnv(Start.OneBehind);
        await StartAsync(env, env.Guard());

        Assert.Null(UpgradeGuard.ReadMarker(env.Paths));
        Assert.Equal(ThisRelease, File.ReadAllText(env.Paths.LastStartedVersionFile));
        Assert.Null((await env.Guard().PrepareAsync()).BackupId);
        Assert.Single(await PreUpgradeAsync(env));
    }

    [Fact]
    public async Task Restarts_after_a_failed_migration_keep_the_backup_of_the_state_before_it()
    {
        var env = NewEnv(Start.OneBehindBroken, settings: new() { [UpgradeGuard.KeepEnv] = "1" });
        var before = await AppliedAsync(env);
        string? first = null;

        // A crash loop: every start fails in the same migration.
        for (var start = 0; start < 4; start++)
        {
            var logger = new ListLogger();
            await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(env, env.Guard(logger: logger)));
            var marker = UpgradeGuard.ReadMarker(env.Paths)!;
            first ??= marker.BackupId;
            Assert.Equal(first, marker.BackupId);
            if (start > 0) Assert.Contains(logger.Entries, e => e.Message.Contains("Resuming the upgrade"));
        }

        var backup = Assert.Single(await PreUpgradeAsync(env));        // no new backup, nothing pruned
        Assert.Equal(first, backup.Id);
        var manifest = (await env.Backups.ValidateArchiveAsync(env.Backups.ResolveArchivePath(first!)!)).Manifest!;
        Assert.Equal(before, manifest.MetricsMigrations);
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
        public Task BeginRestoreAsync(string uploadedPath, string actor, RestoreTarget target, CancellationToken ct = default) =>
            inner.BeginRestoreAsync(uploadedPath, actor, target, ct);
    }

    [Fact]
    public async Task A_failed_pre_upgrade_backup_stops_the_upgrade_and_says_how_to_go_on()
    {
        var env = NewEnv(Start.OneBehind);
        var before = await AppliedAsync(env);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Guard(new FailingBackups(env.Backups)).PrepareAsync());

        Assert.Contains("before any migration", ex.Message);
        Assert.Contains("No space left on device", ex.Message);
        Assert.Contains(UpgradeGuard.SkipBackupEnv, ex.Message);
        Assert.Equal(before, await AppliedAsync(env));
        Assert.Null(UpgradeGuard.ReadMarker(env.Paths));
    }

    [Fact]
    public async Task An_unreadable_marker_still_counts_as_an_unfinished_upgrade_and_nothing_is_pruned()
    {
        var env = NewEnv(Start.OneBehind, settings: new() { [UpgradeGuard.KeepEnv] = "1" });
        var older = await env.Backups.CreateBackupAsync($"{BackupService.PreUpgradeLabelPrefix}older");
        await Task.Delay(20);
        await File.WriteAllTextAsync(env.Paths.UpgradePendingMarker, "{ cut off");

        var plan = await env.Guard().PrepareAsync();

        Assert.NotNull(plan.BackupId);
        Assert.Contains(older.Id, (await PreUpgradeAsync(env)).Select(b => b.Id));   // may be the way back: kept
        Assert.Equal(plan.BackupId, UpgradeGuard.ReadMarker(env.Paths)!.BackupId);
    }

    [Theory]
    [InlineData("2.0.0+abc", "2.0.0+def", true)]
    [InlineData("2.0.0", "2.0.0+def", true)]
    [InlineData("1.9.0+abc", "2.0.0+abc", false)]
    [InlineData(null, "2.0.0", false)]
    public void A_release_is_compared_without_its_build_suffix(string? last, string current, bool same) =>
        Assert.Equal(same, UpgradeGuard.SameRelease(last, current));

    [Fact]
    public async Task The_backup_is_skipped_only_when_the_operator_says_so_and_that_is_logged()
    {
        var env = NewEnv(Start.OneBehind, settings: new() { [UpgradeGuard.SkipBackupEnv] = "true" });
        var logger = new ListLogger();

        var plan = await env.Guard(new FailingBackups(env.Backups), logger).PrepareAsync();

        Assert.Null(plan.BackupId);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("WITHOUT a pre-upgrade backup"));
    }

    [Theory]
    [InlineData(UpgradeGuard.SkipBackupEnv, "yes")]
    [InlineData(UpgradeGuard.KeepEnv, "three")]
    [InlineData(UpgradeGuard.KeepEnv, "0")]
    public async Task An_invalid_setting_stops_the_start_before_a_backup_is_taken(string key, string value)
    {
        var env = NewEnv(Start.OneBehind, settings: new() { [key] = value });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => env.Guard().PrepareAsync());

        Assert.Contains(key, ex.Message);
        Assert.Empty(await env.Backups.ListBackupsAsync());
    }

    [Fact]
    public async Task A_failed_migration_names_the_pre_upgrade_backup_to_restore_and_fails_the_start()
    {
        var plan = new UpgradePlan(["20990101000000_Next"], [], FreshDatabase: false, BackupId: "abc123def456");
        var logger = new ListLogger();

        await Assert.ThrowsAsync<InvalidOperationException>(() => UpgradeGuard.MigrateAsync("metrics", plan.PendingMetrics, plan, logger,
            () => throw new InvalidOperationException("column exists")));

        var critical = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains($"--restore-backup abc123def456 {RestoreCli.ForPreviousReleaseFlag}", critical.Message);
        Assert.Contains("partly migrated", critical.Message);
    }

    [Fact]
    public async Task The_scheduled_retention_keeps_the_pre_upgrade_backups_which_are_pruned_on_their_own()
    {
        var env = NewEnv(Start.Current);
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

        await UpgradeGuard.PruneAsync(env.Backups, keep: 2, current: upgrades[2], NullLogger.Instance, default);
        Assert.Equal(upgrades.Skip(1).Reverse(), (await PreUpgradeAsync(env)).Select(b => b.Id));   // the newest two

        // The backup the running upgrade depends on is never pruned, however many are kept.
        await UpgradeGuard.PruneAsync(env.Backups, keep: 1, current: upgrades[1], NullLogger.Instance, default);
        Assert.Contains(upgrades[1], (await PreUpgradeAsync(env)).Select(b => b.Id));
    }

    [Fact]
    public async Task An_upgrade_is_undone_from_the_command_line_without_the_ui()
    {
        var env = NewEnv(Start.OneBehind);
        var before = await AppliedAsync(env);
        await StartAsync(env, env.Guard());
        var backupId = Assert.Single(await PreUpgradeAsync(env)).Id;
        using (var scope = env.Sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "after", ContainerName = "web", ServerId = "s1", Timestamp = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.NotEqual(before, await AppliedAsync(env));

        var log = new StringWriter();
        var rc = await RestoreCli.RunAsync(env.Backups, vault: null, env.Paths,
            [RestoreCli.LatestPreUpgrade, RestoreCli.ForPreviousReleaseFlag], log);
        Assert.True(rc == 0, log.ToString());
        Assert.Contains($"Backup {backupId} is staged", log.ToString());
        Assert.True(File.Exists(env.Paths.RestorePendingMarker));

        // The next start applies it before it opens the database.
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

    [Fact]
    public async Task When_the_migration_fails_on_the_data_the_way_back_is_the_restore_for_the_previous_release()
    {
        var env = NewEnv(Start.OneBehindBroken);
        var before = await AppliedAsync(env);
        await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(env, env.Guard()));
        var backupId = UpgradeGuard.ReadMarker(env.Paths)!.BackupId;

        // For this release the archive is refused: its migration fails on the same data.
        var refused = new StringWriter();
        Assert.Equal(4, await RestoreCli.RunAsync(env.Backups, null, env.Paths, [RestoreCli.LatestPreUpgrade], refused));
        Assert.Contains(RestoreCli.ForPreviousReleaseFlag, refused.ToString());
        Assert.False(File.Exists(env.Paths.RestorePendingMarker));

        // For the previous release it is staged (still validated, the safety backup taken), and the upgrade is over.
        var log = new StringWriter();
        Assert.Equal(0, await RestoreCli.RunAsync(env.Backups, null, env.Paths,
            [RestoreCli.LatestPreUpgrade, RestoreCli.ForPreviousReleaseFlag], log));
        Assert.Contains($"Backup {backupId} is staged", log.ToString());
        Assert.Null(UpgradeGuard.ReadMarker(env.Paths));
        // The restored data is not what the last release left: the next start of a newer release backs it up again.
        Assert.False(File.Exists(env.Paths.LastStartedVersionFile));
        Assert.Contains(await env.Backups.ListBackupsAsync(), b => b.Label.StartsWith("pre-restore-", StringComparison.Ordinal));

        TestSqlite.ClearPools(env.Paths.RootDir);
        RestoreBootHandler.ApplyPendingRestore(env.Paths);
        Assert.Equal(before, await AppliedAsync(env));
    }

    [Fact]
    public async Task The_command_line_restore_names_what_it_cannot_find()
    {
        var env = NewEnv(Start.Current);
        var manual = await env.Backups.CreateBackupAsync("manual");

        var usage = new StringWriter();
        Assert.Equal(2, await RestoreCli.RunAsync(env.Backups, null, env.Paths, [], usage));
        Assert.Contains(manual.Id, usage.ToString());                    // the listing helps pick one
        Assert.Equal(2, await RestoreCli.RunAsync(env.Backups, null, env.Paths, [manual.Id, "--force"], new StringWriter()));
        Assert.Equal(2, await RestoreCli.RunAsync(env.Backups, null, env.Paths, [manual.Id, "second-id"], new StringWriter()));

        var missing = new StringWriter();
        Assert.Equal(3, await RestoreCli.RunAsync(env.Backups, null, env.Paths, ["000000000000"], missing));
        Assert.Contains("No backup with the id", missing.ToString());

        var noUpgrade = new StringWriter();
        Assert.Equal(3, await RestoreCli.RunAsync(env.Backups, null, env.Paths, [RestoreCli.LatestPreUpgrade], noUpgrade));
        Assert.Contains("No pre-upgrade backup", noUpgrade.ToString());

        // A path is no backup id: only ids the service resolves inside its own directory are restored.
        Assert.Equal(3, await RestoreCli.RunAsync(env.Backups, null, env.Paths, ["../../etc/passwd"], new StringWriter()));
        Assert.False(File.Exists(env.Paths.RestorePendingMarker));
    }

    [Theory]
    [InlineData(null, null)]                                   // no id: the usage text, not a configuration error
    [InlineData(RestoreCli.LatestPreUpgrade, null)]
    [InlineData(RestoreCli.LatestPreUpgrade, RestoreCli.ForPreviousReleaseFlag)]
    public void The_restore_options_pass_the_host_builder(string? id, string? option)
    {
        // Program hands the same args to WebApplication.CreateBuilder before it reaches the restore branch.
        string[] args = [RestoreCli.Flag, .. new[] { id, option }.OfType<string>()];
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);
        Assert.NotNull(builder.Configuration);
        Assert.True(RestoreCli.Requested(args));
    }

    [Theory]
    [InlineData("--restore-backup=abc123def456", "abc123def456")]
    [InlineData("--restore-backup=latest-pre-upgrade --for-previous-release", "latest-pre-upgrade --for-previous-release")]
    [InlineData("--for-previous-release --restore-backup abc123def456", "--for-previous-release abc123def456")]
    [InlineData("--restore-backup", "")]
    public void Every_spelling_of_the_restore_option_reaches_the_restore_not_a_normal_start(string line, string rest)
    {
        // A recovery typed in the `=` form or after its switch must not boot Whiskers on the data it is meant to replace.
        var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(RestoreCli.Requested(args));
        Assert.Equal(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries), RestoreCli.Arguments(args));
        Assert.NotNull(Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args).Configuration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--restore-backups")]
    [InlineData("--urls http://+:8080")]
    public void Other_command_lines_start_normally(string line) =>
        Assert.False(RestoreCli.Requested(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public async Task A_restore_from_the_settings_panel_resets_the_upgrade_state_like_the_command_line()
    {
        var env = NewEnv(Start.Current, lastRelease: ThisRelease);
        var manual = await env.Backups.CreateBackupAsync("manual");
        Assert.Null((await env.Guard().PrepareAsync()).BackupId);       // the fast path: same release, nothing pending

        await env.Backups.BeginRestoreAsync(env.Backups.ResolveArchivePath(manual.Id)!, "admin@example.com");

        Assert.False(File.Exists(env.Paths.LastStartedVersionFile));
        TestSqlite.ClearPools(env.Paths.RootDir);
        RestoreBootHandler.ApplyPendingRestore(env.Paths);
        // The first start on the restored data backs it up, although it is the same release with nothing to migrate.
        var plan = await env.Guard().PrepareAsync();
        Assert.False(plan.HasPending);
        Assert.NotNull(plan.BackupId);
    }

    [Fact]
    public async Task When_an_unfinished_upgrades_backup_is_gone_the_log_names_the_older_ones()
    {
        var env = NewEnv(Start.OneBehind);
        var older = await env.Backups.CreateBackupAsync($"{BackupService.PreUpgradeLabelPrefix}older");
        await File.WriteAllTextAsync(env.Paths.UpgradePendingMarker, System.Text.Json.JsonSerializer.Serialize(
            new UpgradePendingMarker("000000000000", PreviousRelease, ThisRelease, DateTime.UtcNow)));
        var logger = new ListLogger();

        await env.Guard(logger: logger).PrepareAsync();

        Assert.Contains(logger.Entries, e => e.Message.Contains("Older pre-upgrade backups are kept") && e.Message.Contains(older.Id));
    }

    [Fact]
    public async Task Only_a_schema_with_pending_migrations_logs_its_migration_time()
    {
        var plan = new UpgradePlan([], ["20990101000000_Identity"], FreshDatabase: false, BackupId: "abc123def456");
        var logger = new ListLogger();

        await UpgradeGuard.MigrateAsync("metrics", plan.PendingMetrics, plan, logger, () => Task.CompletedTask);
        await UpgradeGuard.MigrateAsync("identity", plan.PendingIdentity, plan, logger, () => Task.CompletedTask);

        var timed = Assert.Single(logger.Entries, e => e.Message.Contains("schema migration finished"));
        Assert.Contains("identity", timed.Message);
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
