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
        // The replaced plaintext must not linger in the database files: SQLite zeroes the freed space and folds the
        // write-ahead log back; PostgreSQL rewrites the two (small) tables without their dead row versions.
        var sqlite = db.Database.IsSqlite();
        if (sqlite)
        {
            await db.Database.OpenConnectionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA secure_delete = ON;", ct);
        }
        try
        {
            await db.SaveChangesAsync(ct);
            if (sqlite)
                await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
            else
                await db.Database.ExecuteSqlRawAsync("VACUUM FULL \"Webhooks\", \"UpdateRollbacks\";", ct);
        }
        finally
        {
            if (sqlite) await db.Database.CloseConnectionAsync();
        }
        logger.LogInformation("Encrypted {Secrets} webhook secret(s) and {Snapshots} rollback snapshot(s) stored in plaintext before",
            webhooks.Count, snapshotRows.Count);
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
