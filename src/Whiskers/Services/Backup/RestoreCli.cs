namespace Whiskers.Services.Backup;

/// <summary>R1-34: recovery without a running UI. <c>dotnet Whiskers.dll --restore-backup &lt;id|latest-pre-upgrade&gt;</c>
/// (in the container: <c>docker compose run --rm whiskers --restore-backup …</c>) stages an on-box self-backup through
/// the same checked path as the Settings panel — validation, the pre-restore safety backup, the PostgreSQL dry run —
/// and exits without starting the web host. The next start applies it (<see cref="RestoreBootHandler"/>) before
/// anything opens the database: this release for a retry, the previous release to undo an upgrade.</summary>
public static class RestoreCli
{
    public const string Flag = "--restore-backup";

    /// <summary>Picks the newest backup taken before a schema upgrade.</summary>
    public const string LatestPreUpgrade = "latest-pre-upgrade";

    internal const string Actor = "cli (--restore-backup)";

    /// <summary>Returns a process exit code: 0 staged, 2 usage, 3 no such backup, 4 refused by validation, 1 failed.</summary>
    public static async Task<int> RunAsync(IBackupService backups, Whiskers.Services.Vault.IVaultService? vault,
        string? target, TextWriter log, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            await log.WriteLineAsync($"Usage: dotnet Whiskers.dll {Flag} <backup id | {LatestPreUpgrade}>");
            await ListAsync(backups, log, ct);
            return 2;
        }

        try
        {
            // The vault starts first: a wrong VAULT_KEY must refuse the safety backup, as it does in the app.
            if (vault is not null) await vault.InitializeAsync(ct);

            var id = target == LatestPreUpgrade
                ? (await backups.ListBackupsAsync(ct))
                    .Where(b => b.Label.StartsWith(BackupService.PreUpgradeLabelPrefix, StringComparison.Ordinal))
                    .OrderByDescending(b => b.CreatedAtUtc)
                    .FirstOrDefault()?.Id
                : target;
            var archive = id is null ? null : backups.ResolveArchivePath(id);
            if (archive is null)
            {
                await log.WriteLineAsync(target == LatestPreUpgrade
                    ? "No pre-upgrade backup found."
                    : $"No backup with the id '{target}'.");
                await ListAsync(backups, log, ct);
                return 3;
            }

            await log.WriteLineAsync($"Staging backup {id} for restore (a safety backup of the current state is taken first)…");
            await backups.BeginRestoreAsync(archive, Actor, ct);
            await log.WriteLineAsync($"Backup {id} is staged. Start Whiskers now — it applies the restore before it opens " +
                "the database. To undo an upgrade, start the previous release.");
            return 0;
        }
        catch (BackupValidationException ex)
        {
            await log.WriteLineAsync($"REFUSED: {ex.Message}");
            return 4;
        }
        catch (Exception ex)
        {
            await log.WriteLineAsync($"RESTORE FAILED: {ex.Message}");
            return 1;
        }
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
