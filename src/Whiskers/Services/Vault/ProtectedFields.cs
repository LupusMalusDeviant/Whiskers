namespace Whiskers.Services.Vault;

/// <summary>
/// R1-31 (INV-52): values kept outside vault.json but encrypted with a key derived from the vault's — the inbound
/// webhooks' signing secrets and the rollback snapshots (a container's full configuration, its environment included)
/// in the database. A stored value starts with <see cref="Prefix"/>; one without it is plaintext from before R1-31,
/// still read until the start-up migration encrypts it (<see cref="ProtectedFieldMigration"/>). Each value is bound to
/// its purpose (field and row), so it cannot be copied to another row and opened there.
/// </summary>
public static class ProtectedFields
{
    /// <summary>The prefix of an encrypted value (AES-256-GCM, version 1).</summary>
    public const string Prefix = "f1:";

    /// <summary>Whether <paramref name="stored"/> is an encrypted value (not plaintext from before R1-31).</summary>
    public static bool IsProtected(string? stored) => stored?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>The purpose of a webhook's signing secret: its webhook id.</summary>
    public static string WebhookSecret(string webhookId) => "webhook-secret:" + webhookId;

    /// <summary>The purpose of a rollback snapshot: the container name and server it is kept for (one per pair).</summary>
    public static string RollbackSnapshot(string? serverId, string containerName)
        => $"rollback-snapshot:{serverId ?? ""}:{containerName}";

    /// <summary>The purpose of a data-protection key in the key ring.</summary>
    public const string DataProtectionKey = "dataprotection-key";

    /// <summary>Reads a stored value: an encrypted one through the vault, plaintext from before R1-31 as it is. Null
    /// when an encrypted value cannot be opened (vault not usable, another key, or the value was changed or moved).</summary>
    public static string? Read(IVaultService? vault, string purpose, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!IsProtected(stored)) return stored;
        return vault is not null && vault.TryUnprotectField(purpose, stored, out var plain) ? plain : null;
    }
}
