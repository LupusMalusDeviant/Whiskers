using Whiskers.Configuration;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Backup;

/// <summary>R1-34: recovery without a running UI.
/// <c>dotnet Whiskers.dll --restore-backup &lt;id|latest-pre-upgrade&gt; [--for-previous-release]</c> (in the container:
/// <c>docker compose run --rm serverwatch --restore-backup …</c>) stages an on-box self-backup through the same checked
/// path as the Settings panel — validation, the pre-restore safety backup, the PostgreSQL dry run — and exits without
/// starting the web host. The next start applies it (<see cref="RestoreBootHandler"/>) before anything opens the
/// database. <c>--for-previous-release</c> is the downgrade: this release's migrations are not run on the archive (they
/// may be what failed), so it is meant for the previous release to start on.</summary>
public static class RestoreCli
{
    public const string Flag = "--restore-backup";

    /// <summary>The downgrade: the archive is checked against its own manifest, not migrated to this release.</summary>
    public const string ForPreviousReleaseFlag = "--for-previous-release";

    /// <summary>Picks the backup of the unfinished upgrade, else the newest backup taken before an upgrade.</summary>
    public const string LatestPreUpgrade = "latest-pre-upgrade";

    internal const string Actor = "cli (--restore-backup)";

    /// <summary>True when the command line asks for this CLI: <c>--restore-backup</c> anywhere, also in the
    /// <c>--restore-backup=&lt;id&gt;</c> form — a recovery typed that way must never start Whiskers normally.</summary>
    public static bool Requested(IReadOnlyList<string> args) =>
        args.Any(a => a == Flag || a.StartsWith(Flag + "=", StringComparison.Ordinal));

    /// <summary>The arguments for <see cref="RunAsync"/>: everything but the flag, with the value of the <c>=</c> form
    /// as its own argument.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> args)
    {
        var rest = new List<string>();
        foreach (var a in args)
        {
            if (a == Flag) continue;
            if (a.StartsWith(Flag + "=", StringComparison.Ordinal)) rest.Add(a[(Flag.Length + 1)..]);
            else rest.Add(a);
        }
        return rest;
    }

    /// <summary><paramref name="args"/> are the ones after <see cref="Flag"/>. Returns a process exit code: 0 staged,
    /// 2 usage, 3 no such backup, 4 refused by validation, 1 failed.</summary>
    public static async Task<int> RunAsync(IBackupService backups, Whiskers.Services.Vault.IVaultService? vault,
        DataPathOptions paths, IReadOnlyList<string> args, TextWriter log, CancellationToken ct = default)
    {
        var target = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a != ForPreviousReleaseFlag).ToList();
        var forPrevious = args.Contains(ForPreviousReleaseFlag);
        if (string.IsNullOrWhiteSpace(target) || unknown.Count > 0 || args.Count(a => !a.StartsWith("--", StringComparison.Ordinal)) > 1)
        {
            if (unknown.Count > 0) await log.WriteLineAsync($"Unknown option: {string.Join(" ", unknown)}");
            await log.WriteLineAsync($"Usage: dotnet Whiskers.dll {Flag} <backup id | {LatestPreUpgrade}> [{ForPreviousReleaseFlag}]");
            await log.WriteLineAsync($"  {ForPreviousReleaseFlag}: for a downgrade — the previous release starts on the restored data.");
            await ListAsync(backups, log, ct);
            return 2;
        }

        try
        {
            // The vault starts first: a wrong VAULT_KEY must refuse the safety backup, as it does in the app.
            if (vault is not null) await vault.InitializeAsync(ct);

            var id = target == LatestPreUpgrade ? await LatestPreUpgradeAsync(backups, paths, ct) : target;
            var archive = id is null ? null : backups.ResolveArchivePath(id);
            if (archive is null)
            {
                await log.WriteLineAsync(target == LatestPreUpgrade
                    ? "No pre-upgrade backup found."
                    : $"No backup with the id '{target}'.");
                await ListAsync(backups, log, ct);
                return 3;
            }

            await log.WriteLineAsync($"Staging backup {id} for restore{(forPrevious ? " by the previous release" : "")} " +
                "(a safety backup of the current state is taken first)…");
            // Staging also resets the upgrade state (BackupService), as for a restore from the Settings panel.
            await backups.BeginRestoreAsync(archive, Actor,
                forPrevious ? RestoreTarget.PreviousRelease : RestoreTarget.ThisRelease, ct);
            await log.WriteLineAsync($"Backup {id} is staged. " + (forPrevious
                ? "Start the previous release now — it applies the restore before it opens the database."
                : "Start Whiskers now — it applies the restore before it opens the database."));
            return 0;
        }
        catch (BackupValidationException ex)
        {
            await log.WriteLineAsync($"REFUSED: {ex.Message}");
            if (!forPrevious)
                await log.WriteLineAsync($"To go back to the previous release, add {ForPreviousReleaseFlag}: this release's " +
                    "migrations are then not run on the archive.");
            return 4;
        }
        catch (Exception ex)
        {
            await log.WriteLineAsync($"RESTORE FAILED: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The backup an unfinished upgrade depends on — the state before it — else the newest pre-upgrade backup.</summary>
    private static async Task<string?> LatestPreUpgradeAsync(IBackupService backups, DataPathOptions paths, CancellationToken ct)
    {
        if (UpgradeGuard.ReadMarker(paths) is { } marker && backups.ResolveArchivePath(marker.BackupId) is not null)
            return marker.BackupId;
        return (await backups.ListBackupsAsync(ct))
            .Where(b => b.Label.StartsWith(BackupService.PreUpgradeLabelPrefix, StringComparison.Ordinal))
            .OrderByDescending(b => b.CreatedAtUtc)
            .FirstOrDefault()?.Id;
    }

    private static async Task ListAsync(IBackupService backups, TextWriter log, CancellationToken ct)
    {
        try
        {
            var all = await backups.ListBackupsAsync(ct);
            if (all.Count == 0)
            {
                await log.WriteLineAsync("There are no self-backups in this data directory.");
                return;
            }
            await log.WriteLineAsync("Self-backups, newest first:");
            foreach (var b in all)
                await log.WriteLineAsync($"  {b.Id}  {b.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}Z  {b.Label}");
        }
        catch (Exception ex)
        {
            await log.WriteLineAsync($"The backups could not be listed: {ex.Message}");
        }
    }
}
