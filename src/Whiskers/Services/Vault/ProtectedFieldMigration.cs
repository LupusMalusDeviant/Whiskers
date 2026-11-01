using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Vault;

/// <summary>
/// R1-31 (INV-52): at start, once the database schema is current, encrypts the inbound webhooks' signing secrets and the
/// rollback snapshots still stored in plaintext from before (<see cref="ProtectedFields"/>). Each value is encrypted
/// and read back before its row is written, so a webhook keeps signing and a snapshot keeps rolling back. Without a
/// usable vault nothing changes: the plaintext values keep working, and the readiness check names them.
/// </summary>
public static class ProtectedFieldMigration
{
    public static async Task RunAsync(MetricsDbContext db, IVaultService vault, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(vault);
        if (!vault.IsEnabled)
        {
            var (secrets, snapshots) = await CountPlaintextAsync(db, ct);
            if (secrets + snapshots > 0)
                logger.LogWarning("{Secrets} webhook secret(s) and {Snapshots} rollback snapshot(s) stay in plaintext until the vault is usable",
                    secrets, snapshots);
            return;
        }

        var webhooks = await db.Webhooks.Where(w => w.Secret != "" && !w.Secret.StartsWith(ProtectedFields.Prefix)).ToListAsync(ct);
        foreach (var webhook in webhooks)
            webhook.Secret = Protect(vault, ProtectedFields.WebhookSecret(webhook.WebhookId), webhook.Secret);

        var snapshotRows = await db.UpdateRollbacks.Where(r => r.ConfigJson != "" && !r.ConfigJson.StartsWith(ProtectedFields.Prefix)).ToListAsync(ct);
        foreach (var snapshot in snapshotRows)
            snapshot.ConfigJson = Protect(vault, ProtectedFields.RollbackSnapshot(snapshot.ServerId, snapshot.ContainerName), snapshot.ConfigJson);

        if (webhooks.Count + snapshotRows.Count == 0) return;
        await WithoutRemnantsAsync(db, () => db.SaveChangesAsync(ct), logger, ct);
        logger.LogInformation("Encrypted {Secrets} webhook secret(s) and {Snapshots} rollback snapshot(s) stored in plaintext before",
            webhooks.Count, snapshotRows.Count);
    }

    /// <summary>
    /// Runs <paramref name="write"/> — one that replaces or deletes plaintext webhook secrets or snapshots — so the
    /// plaintext does not linger in the database files: SQLite zeroes the freed space and folds the write-ahead log
    /// back; PostgreSQL rewrites the two (small) tables without their dead row versions. The write is committed either
    /// way; a clean-up that could not finish is logged, not thrown. PostgreSQL's own WAL segments and replicas are
    /// outside the app's reach.
    /// </summary>
    internal static async Task WithoutRemnantsAsync(MetricsDbContext db, Func<Task> write, ILogger logger, CancellationToken ct = default)
    {
        var sqlite = db.Database.IsSqlite();
        if (!sqlite)
        {
            await write();
            try { await db.Database.ExecuteSqlRawAsync("VACUUM FULL \"Webhooks\", \"UpdateRollbacks\";", ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Webhook secrets and rollback snapshots are encrypted or removed, but the old row versions are not removed yet (VACUUM FULL failed)");
            }
            return;
        }

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA secure_delete = ON;", ct);
            await write();
            await using var checkpoint = db.Database.GetDbConnection().CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var result = await checkpoint.ExecuteReaderAsync(ct);
            // (busy, log frames, checkpointed frames): busy means a reader held the log, and the old pages stay in it.
            if (await result.ReadAsync(ct) && result.GetInt64(0) != 0)
                logger.LogWarning("The database log could not be folded back yet (busy); plaintext replaced just now stays in it until the next checkpoint");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>How many webhook secrets and rollback snapshots are stored in plaintext (for the readiness check).</summary>
    public static async Task<(int Secrets, int Snapshots)> CountPlaintextAsync(MetricsDbContext db, CancellationToken ct = default)
        => (await db.Webhooks.CountAsync(w => w.Secret != "" && !w.Secret.StartsWith(ProtectedFields.Prefix), ct),
            await db.UpdateRollbacks.CountAsync(r => r.ConfigJson != "" && !r.ConfigJson.StartsWith(ProtectedFields.Prefix), ct));

    // Read back before it replaces the plaintext: a value that would not open again is never written.
    private static string Protect(IVaultService vault, string purpose, string plaintext)
    {
        var stored = vault.ProtectField(purpose, plaintext)
            ?? throw new InvalidOperationException("The vault is not usable.");
        if (!vault.TryUnprotectField(purpose, stored, out var back) || back != plaintext)
            throw new InvalidOperationException("An encrypted value did not read back; nothing was changed.");
        return stored;
    }
}
