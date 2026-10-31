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
                    ? $"Der Cloud-API-Key von '{server.Name}' liegt im Vault, und der Vault ist gesperrt (VAULT_KEY passt nicht)."
                    : $"Der Cloud-API-Key von '{server.Name}' liegt im Vault, und der Vault ist deaktiviert (VAULT_KEY fehlt).");
            return vault.GetSecret(server.CloudKeyRef)
                   ?? throw new InvalidOperationException($"Der Cloud-API-Key von '{server.Name}' fehlt im Vault. Key unter Server neu hinterlegen.");
        }
        return string.IsNullOrWhiteSpace(server.CloudApiKey) ? null : server.CloudApiKey;
    }
}
