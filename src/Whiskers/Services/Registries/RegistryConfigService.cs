using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Persistence;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Registries;

/// <summary>Implements F8. Configs live in <c>registries.json</c>; credentials in the vault
/// (<c>registry-cred:{id}</c>). Credential resolution is synchronous and cache-backed because it
/// sits on the image-pull hot path. Its vault writes are part of <c>registry.save</c>/<c>registry.delete</c>,
/// which the gateway decides before this class runs (R1-11).</summary>
[Whiskers.Services.Mutations.MutationHandler]
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

        if (credential is { Length: > 0 } && !_vault.IsEnabled)
            throw new InvalidOperationException("Registry-Credentials benötigen den Vault — VAULT_KEY setzen und neu starten.");

        // R1-11: a failed write leaves the live configuration as it was. The registry list is changed on a copy and
        // published only after registries.json is written; a credential changed before that write is put back when
        // the write fails. The caller's object is never stored (the page keeps editing it).
        var stored = Copy(registry);
        await _lock.WaitAsync();
        try
        {
            var key = CredVaultKey(stored.Id);
            var credentialChanged = credential is not null && _vault.IsEnabled;
            var previousSecret = credentialChanged ? _vault.GetSecret(key) : null;
            if (credential is not null)
            {
                if (credential.Length == 0)
                {
                    if (_vault.IsEnabled) await _vault.DeleteSecretAsync(key);
                    stored.HasCredential = false;
                }
                else
                {
                    await _vault.SetSecretAsync(key, credential.Trim());
                    stored.HasCredential = true;
                }
            }

            var next = new RegistryConfigData { Registries = _cached.Registries.Select(Copy).ToList() };
            var idx = next.Registries.FindIndex(r => r.Id == stored.Id);
            if (idx >= 0) next.Registries[idx] = stored; else next.Registries.Add(stored);
            try
            {
                await _store.SaveAsync(next);
            }
            catch
            {
                if (credentialChanged)
                {
                    if (previousSecret is null) await _vault.DeleteSecretAsync(key);
                    else await _vault.SetSecretAsync(key, previousSecret);
                }
                throw;
            }
            _cached = next;
        }
        finally { _lock.Release(); }
        registry.HasCredential = stored.HasCredential;
        registry.Host = stored.Host;
        return Copy(stored);
    }

    public async Task DeleteRegistryAsync(string registryId)
    {
        // The registry goes first and is published only once written (R1-11): a failed write leaves it and its
        // credential in place. The credential is removed afterwards; if that fails, an orphaned secret is all
        // that remains — no registry uses it any more.
        await _lock.WaitAsync();
        try
        {
            var next = new RegistryConfigData { Registries = _cached.Registries.Where(r => r.Id != registryId).Select(Copy).ToList() };
            await _store.SaveAsync(next);
            _cached = next;
        }
        finally { _lock.Release(); }
        if (_vault.IsEnabled)
            try { await _vault.DeleteSecretAsync(CredVaultKey(registryId)); } catch { /* best effort */ }
    }

    private static RegistryConfig Copy(RegistryConfig r) => new()
    {
        Id = r.Id, Name = r.Name, Host = r.Host, Username = r.Username, HasCredential = r.HasCredential, CreatedAt = r.CreatedAt,
    };

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
