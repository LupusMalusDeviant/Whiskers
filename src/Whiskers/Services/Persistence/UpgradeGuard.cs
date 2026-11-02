using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Backup;

namespace Whiskers.Services.Persistence;

/// <summary>What <see cref="UpgradeGuard.PrepareAsync"/> found and did before the schema migrations ran.</summary>
public sealed record UpgradePlan(
    IReadOnlyList<string> PendingMetrics,
    IReadOnlyList<string> PendingIdentity,
    bool FreshDatabase,
    string? BackupId)
{
    public bool HasPending => PendingMetrics.Count > 0 || PendingIdentity.Count > 0;
}

/// <summary>R1-34: a schema upgrade starts from a recovery point. Before the first migration of a new release runs
/// against an existing database, a self-backup labelled <c>pre-upgrade-…</c> is taken; when it cannot be taken, the
/// start stops with nothing migrated (fail closed). A failed migration logs how to get back: the CLI restore
/// (<c>--restore-backup</c>) needs no running UI. The pre-upgrade backups are kept apart from the scheduled retention
/// and pruned here, to <see cref="KeepEnv"/> (default <see cref="DefaultKeep"/>).</summary>
public static class UpgradeGuard
{
    /// <summary>Set to <c>true</c> to migrate without the pre-upgrade backup, when it cannot be taken and the operator
    /// has a recovery point of their own. Logged as a warning every time it is used.</summary>
    public const string SkipBackupEnv = "WHISKERS_UPGRADE_SKIP_BACKUP";

    /// <summary>How many pre-upgrade backups are kept (newest first).</summary>
    public const string KeepEnv = "WHISKERS_UPGRADE_KEEP_BACKUPS";

    public const int DefaultKeep = 3;

    /// <summary>Checks both contexts for pending migrations and, for an existing database, takes the pre-upgrade
    /// backup. Throws when the backup fails (unless <see cref="SkipBackupEnv"/> is set): nothing is migrated then.</summary>
    public static async Task<UpgradePlan> PrepareAsync(IServiceScopeFactory scopes, IBackupService backups,
        IConfiguration configuration, ILogger logger, CancellationToken ct = default)
    {
        IReadOnlyList<string> pendingMetrics, pendingIdentity;
        bool fresh;
        using (var scope = scopes.CreateScope())
        {
            var metrics = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var identity = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
            var appliedMetrics = (await metrics.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var appliedIdentity = (await identity.Database.GetAppliedMigrationsAsync(ct)).ToList();
            pendingMetrics = (await metrics.Database.GetPendingMigrationsAsync(ct)).ToList();
            pendingIdentity = (await identity.Database.GetPendingMigrationsAsync(ct)).ToList();
            // A database nothing was ever migrated into holds nothing to recover — unless it is a legacy SQLite file
            // from before migrations (its tables exist without a history; DatabaseInitializer baselines it).
            fresh = appliedMetrics.Count == 0 && appliedIdentity.Count == 0
                    && !(metrics.Database.IsSqlite()
                         && await DatabaseInitializer.TableExistsAsync(metrics, DatabaseInitializer.SentinelTable, ct));
        }

        var plan = new UpgradePlan(pendingMetrics, pendingIdentity, fresh, BackupId: null);
        if (!plan.HasPending)
        {
            logger.LogInformation("Database schema is current; no migration to run.");
            return plan;
        }
        if (fresh)
        {
            logger.LogInformation("New database: creating the schema ({Metrics} + {Identity} migrations).",
                pendingMetrics.Count, pendingIdentity.Count);
            return plan;
        }

        logger.LogWarning(
            "Database schema upgrade: {Metrics} metrics and {Identity} identity migration(s) pending ({List}). " +
            "Taking a pre-upgrade backup first.",
            pendingMetrics.Count, pendingIdentity.Count, string.Join(", ", pendingMetrics.Concat(pendingIdentity)));

        if (configuration.GetValue<bool>(SkipBackupEnv))
        {
            logger.LogWarning("{Setting}=true: migrating WITHOUT a pre-upgrade backup. There is no automatic recovery " +
                "point for this upgrade.", SkipBackupEnv);
            return plan;
        }

        var watch = Stopwatch.StartNew();
        SelfBackupInfo backup;
        try
        {
            backup = await backups.CreateBackupAsync($"{BackupService.PreUpgradeLabelPrefix}{DateTime.UtcNow:yyyyMMdd_HHmmss}", ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "The pre-upgrade backup failed; no migration ran and the database is unchanged.");
            throw new InvalidOperationException(
                $"Upgrade stopped before any migration: the pre-upgrade backup failed ({ex.Message}). The database is " +
                $"unchanged. Fix the cause (free space, VAULT_KEY, the PostgreSQL client tools) and start again, or set " +
                $"{SkipBackupEnv}=true to migrate without a backup.", ex);
        }
        logger.LogWarning("Pre-upgrade backup {Id} taken in {Seconds:F1} s. To undo this upgrade: restore it with " +
            "--restore-backup {Id}, then start the previous release.", backup.Id, watch.Elapsed.TotalSeconds, backup.Id);

        await PruneAsync(backups, configuration, logger, ct);
        return plan with { BackupId = backup.Id };
    }

    /// <summary>Keeps the newest pre-upgrade backups and deletes older ones. A failure is logged, never fatal.</summary>
    internal static async Task PruneAsync(IBackupService backups, IConfiguration configuration, ILogger logger,
        CancellationToken ct)
    {
        var keep = Math.Max(1, configuration.GetValue(KeepEnv, DefaultKeep));
        try
        {
            var old = (await backups.ListBackupsAsync(ct))
                .Where(b => b.Label.StartsWith(BackupService.PreUpgradeLabelPrefix, StringComparison.Ordinal))
                .OrderByDescending(b => b.CreatedAtUtc)
                .Skip(keep);
            foreach (var backup in old)
                await backups.DeleteBackupAsync(backup.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Older pre-upgrade backups could not be pruned; they stay.");
        }
    }

    /// <summary>Runs one migration step, timing it; on failure logs how to recover, then rethrows (the start fails).</summary>
    public static async Task MigrateAsync(string what, UpgradePlan plan, ILogger logger, Func<Task> migrate)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await migrate();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "The {What} schema migration failed after {Seconds:F1} s. The database may be partly migrated. " +
                "Recover: stop Whiskers; {Restore}; then start the previous release, or fix the cause and start this " +
                "one again. See docs/operations/backup-and-recovery.md (Upgrades).",
                what, watch.Elapsed.TotalSeconds,
                plan.BackupId is { } id
                    ? $"run this release with --restore-backup {id} (the pre-upgrade backup)"
                    : "restore your own backup of the database");
            throw;
        }
        if (plan.HasPending)
            logger.LogInformation("The {What} schema migration finished in {Seconds:F1} s.", what, watch.Elapsed.TotalSeconds);
    }
}
