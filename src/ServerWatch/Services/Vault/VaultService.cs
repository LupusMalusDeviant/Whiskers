using System.Security.Cryptography;
using System.Text;
using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Vault;

/// <summary>
/// AES-256 encrypted secret vault. Master key from VAULT_KEY env var.
/// Secrets are stored encrypted at rest in /app/data/vault.json.
/// </summary>
public class VaultService : IVaultService
{
    private readonly JsonFileStore<VaultData> _store;
    private readonly ILogger<VaultService> _logger;
    private readonly byte[]? _masterKey;
    private VaultData _data = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public VaultService(IConfiguration configuration, ILogger<VaultService> logger)
    {
        _store = new JsonFileStore<VaultData>("/app/data/vault.json");
        _logger = logger;

        var keyStr = configuration["VAULT_KEY"] ?? Environment.GetEnvironmentVariable("VAULT_KEY");
        if (!string.IsNullOrEmpty(keyStr))
        {
            // Derive a 256-bit key from the passphrase
            _masterKey = SHA256.HashData(Encoding.UTF8.GetBytes(keyStr));
            _logger.LogInformation("Vault initialized with master key");
        }
        else
        {
            _logger.LogWarning("VAULT_KEY not set — vault is disabled. Set VAULT_KEY env var to enable.");
        }
    }

    public bool IsEnabled => _masterKey != null;

    public async Task InitializeAsync()
    {
        if (_store.Exists())
        {
            _data = await _store.LoadAsync();
            _logger.LogInformation("Vault loaded: {Count} secrets", _data.Entries.Count);
        }
    }

    public List<VaultEntry> ListSecrets()
    {
        // Return metadata only — values are never exposed in the list
        return _data.Entries.Select(e => new VaultEntry
        {
            Key = e.Key,
            ContainerId = e.ContainerId,
            ContainerName = e.ContainerName,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
            RotateAfterDays = e.RotateAfterDays,
            EncryptedValue = "" // Never expose
        }).ToList();
    }

    public async Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null, int? rotateAfterDays = null)
    {
        if (_masterKey == null) throw new InvalidOperationException("Vault ist nicht aktiviert. VAULT_KEY Umgebungsvariable setzen.");

        await _lock.WaitAsync();
        try
        {
            var encrypted = Encrypt(plainValue);
            var existing = _data.Entries.FirstOrDefault(e => e.Key == key);

            if (existing != null)
            {
                existing.EncryptedValue = encrypted;
                existing.UpdatedAt = DateTime.UtcNow;
                existing.ContainerId = containerId ?? existing.ContainerId;
                existing.ContainerName = containerName ?? existing.ContainerName;
                existing.RotateAfterDays = rotateAfterDays ?? existing.RotateAfterDays;
            }
            else
            {
                _data.Entries.Add(new VaultEntry
                {
                    Key = key,
                    EncryptedValue = encrypted,
                    ContainerId = containerId,
                    ContainerName = containerName,
                    CreatedAt = DateTime.UtcNow,
                    RotateAfterDays = rotateAfterDays
                });
            }

            await _store.SaveAsync(_data);
            _logger.LogInformation("Vault secret set: {Key}", key);
        }
        finally { _lock.Release(); }
    }

    public string? GetSecret(string key)
    {
        if (_masterKey == null) return null;
        var entry = _data.Entries.FirstOrDefault(e => e.Key == key);
        if (entry == null) return null;

        try { return Decrypt(entry.EncryptedValue); }
        catch { return null; }
    }

    public async Task DeleteSecretAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            _data.Entries.RemoveAll(e => e.Key == key);
            await _store.SaveAsync(_data);
        }
        finally { _lock.Release(); }
    }

    public List<VaultEntry> GetExpiringSecrets()
    {
        var now = DateTime.UtcNow;
        return _data.Entries.Where(e =>
            e.RotateAfterDays.HasValue &&
            (e.UpdatedAt ?? e.CreatedAt).AddDays(e.RotateAfterDays.Value) <= now
        ).ToList();
    }

    private string Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = _masterKey!;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + encrypted.Length];
        aes.IV.CopyTo(result, 0);
        encrypted.CopyTo(result, aes.IV.Length);

        return Convert.ToBase64String(result);
    }

    private string Decrypt(string cipherBase64)
    {
        var cipherBytes = Convert.FromBase64String(cipherBase64);

        using var aes = Aes.Create();
        aes.Key = _masterKey!;

        // Extract IV from first 16 bytes
        var iv = cipherBytes[..16];
        var encrypted = cipherBytes[16..];

        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
