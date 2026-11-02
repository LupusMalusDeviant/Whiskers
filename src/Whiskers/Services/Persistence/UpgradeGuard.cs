using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Services.Backup;

namespace Whiskers.Services.Persistence;

/// <summary>What <see cref="UpgradeGuard"/> found and did before the start migrated anything.</summary>
public sealed record UpgradePlan(
    IReadOnlyList<string> PendingMetrics,
    IReadOnlyList<string> PendingIdentity,
    bool FreshDatabase,
    string? BackupId,
    bool Resumed = false)
{
    public bool HasPending => PendingMetrics.Count > 0 || PendingIdentity.Count > 0;

    public static UpgradePlan None { get; } = new([], [], FreshDatabase: false, BackupId: null);
}

/// <summary>An upgrade that took its pre-upgrade backup and has not finished (<see cref="DataPathOptions.UpgradePendingMarker"/>).</summary>
public sealed record UpgradePendingMarker(string BackupId, string FromVersion, string ToVersion, DateTime CreatedAtUtc);

/// <summary>R1-34: an upgrade starts from a recovery point. When another release starts on an existing database — pending
/// schema migrations of either context, or a release other than the one that last finished its start — a self-backup
/// labelled <c>pre-upgrade-…</c> is taken first; when it cannot be taken, the start stops with nothing migrated (fail
/// closed). It runs as a warm-up right after the vault has checked its key (<see cref="Order"/> 45) and before anything
/// rewrites a data file or the database in a newer format (the server config, the credential move into the vault, the
/// key ring, the schema migrations).
///
/// An upgrade that has not finished (its migrations failed, the process was killed) leaves a marker: the next start keeps
/// the backup the marker names instead of taking another one of the half-migrated state, and the pruning never deletes
/// it. The marker is cleared when the migrations finished (<see cref="CompleteAsync"/>) or a restore was staged from the
/// command line (<see cref="RestoreCli"/>).</summary>
public sealed class UpgradeGuard : IInitializable
{
    /// <summary>Set to <c>true</c> to migrate without the pre-upgrade backup, when it cannot be taken and the operator
    /// has a recovery point of their own. Logged as a warning every time it is used.</summary>
    public const string SkipBackupEnv = "WHISKERS_UPGRADE_SKIP_BACKUP";

    /// <summary>How many pre-upgrade backups are kept (newest first, at least 1).</summary>
    public const string KeepEnv = "WHISKERS_UPGRADE_KEEP_BACKUPS";

    public const int DefaultKeep = 3;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IServiceScopeFactory _scopes;
    private readonly IBackupService _backups;
    private readonly IConfiguration _configuration;
    private readonly DataPathOptions _paths;
    private readonly ILogger _logger;
    private readonly string _version;

    public UpgradeGuard(IServiceScopeFactory scopes, IBackupService backups, IConfiguration configuration,
        DataPathOptions paths, ILogger<UpgradeGuard> logger)
        : this(scopes, backups, configuration, paths, logger, BackupService.AppVersion()) { }

    internal UpgradeGuard(IServiceScopeFactory scopes, IBackupService backups, IConfiguration configuration,
        DataPathOptions paths, ILogger logger, string version)
    {
        _scopes = scopes;
        _backups = backups;
        _configuration = configuration;
        _paths = paths;
        _logger = logger;
        _version = version;
    }

    /// <summary>After the vault (40) checked its key, so the backup is sealed with the verified one; before the server
    /// config (50) and the credential move into the vault (55), which may rewrite their files.</summary>
    public int Order => 45;

    /// <summary>The outcome of <see cref="InitializeAsync"/>; <see cref="UpgradePlan.None"/> before it ran.</summary>
    public UpgradePlan Plan { get; private set; } = UpgradePlan.None;

    public async Task InitializeAsync(CancellationToken ct) => Plan = await PrepareAsync(ct);

    /// <summary>Checks for an upgrade and, on an existing database, takes (or keeps) the pre-upgrade backup. Throws when
    /// the backup fails (unless <see cref="SkipBackupEnv"/> is set) or a setting is invalid: nothing has changed then.</summary>
    public async Task<UpgradePlan> PrepareAsync(CancellationToken ct = default)
    {
        // Settings first: a typo must stop the start before a backup is taken, not after it.
        var skip = ReadBool(SkipBackupEnv, false);
        var keep = ReadInt(KeepEnv, DefaultKeep);

        IReadOnlyList<string> pendingMetrics, pendingIdentity;
        bool fresh;
        using (var scope = _scopes.CreateScope())
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
        var lastVersion = ReadLastVersion();
        var marker = ReadMarker();
        if (fresh)
        {
            _logger.LogInformation("New database: creating the schema ({Metrics} + {Identity} migrations).",
                pendingMetrics.Count, pendingIdentity.Count);
            return plan;
        }
        if (!plan.HasPending && lastVersion == _version && marker is null)
        {
            _logger.LogInformation("Database schema is current; no upgrade.");
            return plan;
        }

        // An upgrade that did not finish keeps the backup it took: a new one would hold the half-migrated state, and
        // pruning for it would delete the way back.
        if (marker is not null && _backups.ResolveArchivePath(marker.BackupId) is not null)
        {
            _logger.LogWarning("Resuming the upgrade from {From} begun at {At:o}: its pre-upgrade backup {Id} stays the way " +
                "back ({Metrics} metrics and {Identity} identity migration(s) pending).",
                marker.FromVersion, marker.CreatedAtUtc, marker.BackupId, pendingMetrics.Count, pendingIdentity.Count);
            return plan with { BackupId = marker.BackupId, Resumed = true };
        }
        if (marker is not null)
            _logger.LogWarning("The pre-upgrade backup {Id} of an unfinished upgrade is gone; taking a new one of the " +
                "current state, which may be partly migrated.", marker.BackupId);

        _logger.LogWarning(
            "Upgrade from {From} to {To}: {Metrics} metrics and {Identity} identity migration(s) pending ({List}). " +
            "Taking a pre-upgrade backup first.",
            lastVersion ?? "an earlier release", _version, pendingMetrics.Count, pendingIdentity.Count,
            string.Join(", ", pendingMetrics.Concat(pendingIdentity)));

        if (skip)
        {
            _logger.LogWarning("{Setting}=true: upgrading WITHOUT a pre-upgrade backup. There is no automatic recovery " +
                "point for this upgrade.", SkipBackupEnv);
            return plan;
        }

        var watch = Stopwatch.StartNew();
        SelfBackupInfo backup;
        try
        {
            backup = await _backups.CreateBackupAsync($"{BackupService.PreUpgradeLabelPrefix}{DateTime.UtcNow:yyyyMMdd_HHmmss}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The pre-upgrade backup failed; nothing was migrated and the database is unchanged.");
            throw new InvalidOperationException(
                $"Upgrade stopped before any migration: the pre-upgrade backup failed ({ex.Message}). The database is " +
                $"unchanged. Fix the cause (free space, VAULT_KEY, the PostgreSQL client tools) and start again, or set " +
                $"{SkipBackupEnv}=true to upgrade without a backup.", ex);
        }
        // The marker before anything migrates: a start killed from here on resumes with this backup.
        await DurableFile.WriteAllTextAsync(_paths.UpgradePendingMarker, JsonSerializer.Serialize(
            new UpgradePendingMarker(backup.Id, lastVersion ?? "unknown", _version, DateTime.UtcNow), Json), ct: ct);
        _logger.LogWarning("Pre-upgrade backup {Id} taken in {Seconds:F1} s. To undo this upgrade: stage it with " +
            "--restore-backup {Id} --for-previous-release, then start the previous release.",
            backup.Id, watch.Elapsed.TotalSeconds, backup.Id);

        await PruneAsync(_backups, keep, backup.Id, _logger, ct);
        return plan with { BackupId = backup.Id };
    }

    /// <summary>The start's migrations finished: this release is the one that last started, and the upgrade is over.</summary>
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        await DurableFile.WriteAllTextAsync(_paths.LastStartedVersionFile, _version, ct: ct);
        File.Delete(_paths.UpgradePendingMarker);
    }

    /// <summary>The marker of an unfinished upgrade, or null. An unreadable marker is treated as none (and logged).</summary>
    public static UpgradePendingMarker? ReadMarker(DataPathOptions paths, ILogger? logger = null)
    {
        if (!File.Exists(paths.UpgradePendingMarker)) return null;
        try
        {
            return JsonSerializer.Deserialize<UpgradePendingMarker>(File.ReadAllText(paths.UpgradePendingMarker));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger?.LogWarning(ex, "The marker of an unfinished upgrade could not be read; it is ignored.");
            return null;
        }
    }

    private UpgradePendingMarker? ReadMarker() => ReadMarker(_paths, _logger);

    private string? ReadLastVersion() =>
        File.Exists(_paths.LastStartedVersionFile) ? File.ReadAllText(_paths.LastStartedVersionFile).Trim() : null;

    private bool ReadBool(string key, bool fallback)
    {
        var raw = _configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return bool.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException($"{key} must be true or false, not '{raw}'. Nothing was changed.");
    }

    private int ReadInt(string key, int fallback)
    {
        var raw = _configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var value) && value >= 1
            ? value
            : throw new InvalidOperationException($"{key} must be a whole number of at least 1, not '{raw}'. Nothing was changed.");
    }

    /// <summary>Keeps the newest <paramref name="keep"/> pre-upgrade backups — never <paramref name="current"/>, the one
    /// the running upgrade depends on — and deletes older ones. A failure is logged, never fatal.</summary>
    internal static async Task PruneAsync(IBackupService backups, int keep, string current, ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var old = (await backups.ListBackupsAsync(ct))
                .Where(b => b.Label.StartsWith(BackupService.PreUpgradeLabelPrefix, StringComparison.Ordinal))
                .OrderByDescending(b => b.CreatedAtUtc)
                .Skip(Math.Max(1, keep))
                .Where(b => b.Id != current);
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
                "Recover: stop Whiskers; {Restore}; then start the previous release. Or fix the cause and start this " +
                "release again: it keeps the same pre-upgrade backup. See docs/operations/backup-and-recovery.md (Upgrades).",
                what, watch.Elapsed.TotalSeconds,
                plan.BackupId is { } id
                    ? $"run this release with --restore-backup {id} --for-previous-release (the pre-upgrade backup)"
                    : "restore your own backup of the database");
            throw;
        }
        if (plan.HasPending)
            logger.LogInformation("The {What} schema migration finished in {Seconds:F1} s.", what, watch.Elapsed.TotalSeconds);
    }
}
