using Whiskers.Models;

namespace Whiskers.Services.Vault;

/// <summary>Encrypted secret vault (master-key protected) for storing per-container secrets.</summary>
public interface IVaultService
{
    bool IsEnabled { get; }
    Task InitializeAsync(CancellationToken ct = default);
    List<VaultEntry> ListSecrets();
    Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null, int? rotateAfterDays = null);
    string? GetSecret(string key);
    Task DeleteSecretAsync(string key);
    List<VaultEntry> GetExpiringSecrets();
}
