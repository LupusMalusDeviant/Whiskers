namespace Whiskers.Services.Vault;

/// <summary>
/// Vault keys of the infrastructure credentials that R1-29 moved out of the JSON stores (INV-52): a cloud API key per
/// server and the DNS provider token. A save writes the secret under a NEW key and the record then names it, so a
/// failed save leaves the previous credential in use (the R1-11 pattern of the kubeconfig). The start-up migration
/// uses one fixed key per credential, so a migration interrupted before the record was rewritten simply runs again.
/// </summary>
public static class CredentialRefs
{
    /// <summary>A new vault key for a cloud API key of <paramref name="serverId"/>.</summary>
    public static string NewCloudKey(string serverId) => $"cloudkey:{serverId}:{Guid.NewGuid():N}";

    /// <summary>The vault key the migration writes a server's legacy plaintext cloud API key to.</summary>
    public static string MigratedCloudKey(string serverId) => $"cloudkey:{serverId}:migrated";

    /// <summary>A new vault key for the DNS provider token.</summary>
    public static string NewDnsToken() => $"dns:apitoken:{Guid.NewGuid():N}";

    /// <summary>The vault key the migration writes the legacy plaintext DNS token to.</summary>
    public const string MigratedDnsToken = "dns:apitoken:migrated";

    /// <summary>Whether <paramref name="key"/> is one of the fixed keys the start-up migration writes to.</summary>
    public static bool IsMigrationKey(string key)
        => key == MigratedDnsToken
           || VaultBackedSettings.IsMigrationKey(key)
           || (key.StartsWith("cloudkey:", StringComparison.Ordinal) && key.EndsWith(":migrated", StringComparison.Ordinal)
               && key.Length > "cloudkey:".Length + ":migrated".Length);
}
