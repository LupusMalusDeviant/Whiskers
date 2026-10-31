using Whiskers.Models;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Cloud;

/// <summary>Where a server's cloud API key lives (R1-29): in the vault under <see cref="Whiskers.Models.ServerConfig.CloudKeyRef"/>,
/// or — on a server not migrated yet because the vault was unavailable — still as the legacy plaintext field.</summary>
public static class CloudCredentials
{
    /// <summary>Whether the server has a cloud API key at all (in the vault or not yet migrated).</summary>
    public static bool HasCloudKey(this Whiskers.Models.ServerConfig server)
        => !string.IsNullOrWhiteSpace(server.CloudKeyRef) || !string.IsNullOrWhiteSpace(server.CloudApiKey);

    /// <summary>Whether the server still carries its key in plaintext in servers.json.</summary>
    public static bool HasPlaintextCloudKey(this Whiskers.Models.ServerConfig server)
        => string.IsNullOrWhiteSpace(server.CloudKeyRef) && !string.IsNullOrWhiteSpace(server.CloudApiKey);

    /// <summary>The server's cloud API key. A key in the vault needs a usable vault: a disabled or locked vault is
    /// an error, never a silent "no key".</summary>
    public static string? KeyOf(Whiskers.Models.ServerConfig server, IVaultService vault)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(vault);
        if (!string.IsNullOrWhiteSpace(server.CloudKeyRef))
        {
            if (!vault.IsEnabled)
                throw new InvalidOperationException(vault.KeyState == VaultKeyState.Wrong
                    ? $"The cloud API key of '{server.Name}' is in the vault, and the vault is locked (VAULT_KEY does not match it)."
                    : $"The cloud API key of '{server.Name}' is in the vault, and the vault is disabled (VAULT_KEY is not set).");
            return vault.GetSecret(server.CloudKeyRef)
                   ?? throw new InvalidOperationException($"The cloud API key of '{server.Name}' is missing from the vault.");
        }
        return string.IsNullOrWhiteSpace(server.CloudApiKey) ? null : server.CloudApiKey;
    }
}
