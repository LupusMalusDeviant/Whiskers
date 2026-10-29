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
    private volatile RegistryConfigData _cached = new();   // replaced, never changed in place: read lock-free
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
        if (credential is { Length: > 0 } && !_vault.IsEnabled)
            throw new InvalidOperationException("Registry-Credentials benötigen den Vault — VAULT_KEY setzen und neu starten.");

        // R1-11: a failed save leaves the live configuration as it was. A new credential is written under a NEW vault
        // key that no registry names yet, so it takes effect only together with the registry entry that names it —
        // that entry is published only after registries.json is written. The previous key is removed afterwards.
        // Everything runs under one lock, so a concurrent save or delete of the same registry cannot interleave.
        // The caller's object is never stored (the page keeps editing it).
        var stored = Copy(registry);
        stored.Host = stored.Host.Trim().TrimEnd('/')
            // Accept a pasted URL, keep only the host[:port] part.
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase);

        await _lock.WaitAsync();
        try
        {
            var existing = _cached.Registries.FirstOrDefault(r => r.Id == stored.Id);
            var oldRef = existing is { HasCredential: true } ? CredentialKey(existing) : null;
            string? newRef = null;
            if (credential is null)
            {
                stored.HasCredential = existing?.HasCredential ?? false;
                stored.CredentialRef = existing?.CredentialRef;
            }
            else if (credential.Length == 0)
            {
                stored.HasCredential = false;
                stored.CredentialRef = null;
            }
            else
            {
                newRef = $"{CredVaultKey(stored.Id)}:{Guid.NewGuid():N}";
                await _vault.SetSecretAsync(newRef, credential.Trim());
                stored.HasCredential = true;
                stored.CredentialRef = newRef;
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
                // The new key was never live; removing it only tidies up.
                if (newRef is not null)
                    try { await _vault.DeleteSecretAsync(newRef); } catch { /* an unused orphan at worst */ }
                throw;
            }
            _cached = next;
            // The versioned, redacted diff is part of the gateway's event (R1-22); CredentialRef names a vault key and
            // is redacted like a secret.
            Whiskers.Services.Mutations.ConfigChange.Record($"registry:{stored.Id}", existing, stored);

            if (credential is not null && oldRef is not null && oldRef != stored.CredentialRef && _vault.IsEnabled)
                try { await _vault.DeleteSecretAsync(oldRef); } catch { /* no registry names it any more */ }
        }
        finally { _lock.Release(); }
        registry.HasCredential = stored.HasCredential;
        registry.Host = stored.Host;
        return Copy(stored);
    }

    public async Task DeleteRegistryAsync(string registryId)
    {
        // The registry goes first and is published only once written (R1-11): a failed write leaves it and its
        // credential in place. The credential is removed afterwards, under the same lock, so a save of the same
        // registry cannot slip in between; a save afterwards writes a new key that this delete never touches.
        await _lock.WaitAsync();
        try
        {
            var existing = _cached.Registries.FirstOrDefault(r => r.Id == registryId);
            var next = new RegistryConfigData { Registries = _cached.Registries.Where(r => r.Id != registryId).Select(Copy).ToList() };
            await _store.SaveAsync(next);
            _cached = next;
            if (existing is not null && _vault.IsEnabled)
                try { await _vault.DeleteSecretAsync(CredentialKey(existing)); } catch { /* no registry names it any more */ }
        }
        finally { _lock.Release(); }
    }

    private static RegistryConfig Copy(RegistryConfig r) => new()
    {
        Id = r.Id, Name = r.Name, Host = r.Host, Username = r.Username, HasCredential = r.HasCredential,
        CredentialRef = r.CredentialRef, CreatedAt = r.CreatedAt,
    };

    /// <summary>The vault key holding a registry's credential: its own reference, or the pre-R1-11 key.</summary>
    private static string CredentialKey(RegistryConfig r) => r.CredentialRef ?? CredVaultKey(r.Id);

    public (string Username, string Password, string ServerAddress)? GetCredentialForImage(string imageRef)
    {
        var host = RegistryHostOf(imageRef);
        // Lock-free for the pull hot path. A rotation may publish a new snapshot and delete the key this read
        // captured in between (R1-11); a vanished secret on a snapshot that is no longer current is read again from
        // the current one.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = _cached;
            var match = snapshot.Registries.FirstOrDefault(r =>
                r.HasCredential && string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase));
            if (match is null || !_vault.IsEnabled) return null;

            var password = _vault.GetSecret(CredentialKey(match));
            if (!string.IsNullOrEmpty(password)) return (match.Username, password, match.Host);
            if (ReferenceEquals(snapshot, _cached)) return null;
        }
        return null;
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
