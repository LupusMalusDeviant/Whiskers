using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Persistence;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Registries;

/// <summary>Implements F8. Configs live in <c>registries.json</c>; credentials in the vault
/// (<c>registry-cred:{id}</c>). Credential resolution is synchronous and cache-backed because it
/// sits on the image-pull hot path.</summary>
public class RegistryConfigService : IRegistryConfigService
{
    private readonly JsonFileStore<RegistryConfigData> _store;
    private readonly IVaultService _vault;
    private RegistryConfigData _cached = new();
    private bool _loaded;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RegistryConfigService(IVaultService vault, string? storePath = null, DataPathOptions? dataPaths = null)
    {
        _vault = vault;
        var paths = dataPaths ?? DataPathOptions.Default;
        _store = new JsonFileStore<RegistryConfigData>(storePath ?? $"{paths.RootDir}/registries.json");
        // Eager, synchronous warm-up so GetCredentialForImage never blocks on I/O mid-pull.
        if (_store.Exists())
        {
            _cached = _store.LoadAsync().GetAwaiter().GetResult();
            _loaded = true;
        }
        else
        {
            _loaded = true;
        }
    }

    private static string CredVaultKey(string id) => $"registry-cred:{id}";

    public Task<List<RegistryConfig>> GetRegistriesAsync() => Task.FromResult(_cached.Registries.ToList());

    public async Task<RegistryConfig> SaveRegistryAsync(RegistryConfig registry, string? credential)
    {
        if (string.IsNullOrWhiteSpace(registry.Name)) throw new ArgumentException("Name is required.");
        if (string.IsNullOrWhiteSpace(registry.Host)) throw new ArgumentException("Host is required.");
        registry.Host = registry.Host.Trim().TrimEnd('/');
        // Accept a pasted URL, keep only the host[:port] part.
        registry.Host = registry.Host
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase);

        if (credential is not null)
        {
            if (credential.Length == 0)
            {
                if (_vault.IsEnabled) await _vault.DeleteSecretAsync(CredVaultKey(registry.Id));
                registry.HasCredential = false;
            }
            else
            {
                if (!_vault.IsEnabled)
                    throw new InvalidOperationException("Registry-Credentials benötigen den Vault — VAULT_KEY setzen und neu starten.");
                await _vault.SetSecretAsync(CredVaultKey(registry.Id), credential.Trim());
                registry.HasCredential = true;
            }
        }

        await _lock.WaitAsync();
        try
        {
            var idx = _cached.Registries.FindIndex(r => r.Id == registry.Id);
            if (idx >= 0) _cached.Registries[idx] = registry; else _cached.Registries.Add(registry);
            await _store.SaveAsync(_cached);
        }
        finally { _lock.Release(); }
        return registry;
    }

    public async Task DeleteRegistryAsync(string registryId)
    {
        if (_vault.IsEnabled)
            try { await _vault.DeleteSecretAsync(CredVaultKey(registryId)); } catch { /* best effort */ }
        await _lock.WaitAsync();
        try
        {
            _cached.Registries.RemoveAll(r => r.Id == registryId);
            await _store.SaveAsync(_cached);
        }
        finally { _lock.Release(); }
    }

    public (string Username, string Password, string ServerAddress)? GetCredentialForImage(string imageRef)
    {
        var host = RegistryHostOf(imageRef);
        var match = _cached.Registries.FirstOrDefault(r =>
            r.HasCredential && string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase));
        if (match is null || !_vault.IsEnabled) return null;

        var password = _vault.GetSecret(CredVaultKey(match.Id));
        if (string.IsNullOrEmpty(password)) return null;
        return (match.Username, password, match.Host);
    }

    /// <summary>Registry host of an image reference, Docker-convention style: the first path
    /// segment counts as a registry only if it contains a '.', a ':' or is "localhost"; everything
    /// else (nginx, library/nginx) is Docker Hub → "docker.io". Public static for unit tests.</summary>
    public static string RegistryHostOf(string imageRef)
    {
        if (string.IsNullOrWhiteSpace(imageRef)) return "docker.io";
        var slash = imageRef.IndexOf('/');
        if (slash <= 0) return "docker.io";
        var first = imageRef[..slash];
        return first.Contains('.') || first.Contains(':') || first == "localhost"
            ? first
            : "docker.io";
    }
}
