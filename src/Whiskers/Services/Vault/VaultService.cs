using System.Security.Cryptography;
using System.Text;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Vault;

/// <summary>
/// Authenticated encrypted secret vault (AES-256-GCM). The master key is derived from the VAULT_KEY
/// passphrase with PBKDF2 (600k iterations) over a persisted per-vault salt. Secrets are stored encrypted
/// at rest in /app/data/vault.json as "g1:" + base64(nonce ‖ tag ‖ ciphertext). Legacy AES-CBC entries
/// (unauthenticated, key = SHA256(passphrase)) are transparently migrated on load.
/// </summary>
public class VaultService : IVaultService
{
    private const string GcmPrefix = "g1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 600_000;

    private readonly JsonFileStore<VaultData> _store;
    private readonly ILogger<VaultService> _logger;
    private readonly string? _passphrase;
    private readonly byte[]? _legacyKey;   // SHA256(passphrase) — only used to read/migrate old CBC entries
    private byte[]? _masterKey;             // PBKDF2-derived — set in InitializeAsync once the salt is known
    private VaultData _data = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public VaultService(IConfiguration configuration, ILogger<VaultService> logger, string? storePath = null)
    {
        _store = new JsonFileStore<VaultData>(storePath ?? "/app/data/vault.json");
        _logger = logger;

        _passphrase = configuration["VAULT_KEY"] ?? Environment.GetEnvironmentVariable("VAULT_KEY");
        if (!string.IsNullOrEmpty(_passphrase))
            _legacyKey = SHA256.HashData(Encoding.UTF8.GetBytes(_passphrase));
        else
            _logger.LogWarning("VAULT_KEY not set — vault is disabled. Set VAULT_KEY env var to enable.");
    }

    public bool IsEnabled => _masterKey != null;

    public async Task InitializeAsync()
    {
        if (_store.Exists())
        {
            _data = await _store.LoadAsync();
            _logger.LogInformation("Vault loaded: {Count} secrets", _data.Entries.Count);
        }

        if (string.IsNullOrEmpty(_passphrase)) return;

        // Establish the persisted PBKDF2 salt (generate on first use), then derive the master key.
        var dirty = false;
        if (string.IsNullOrEmpty(_data.KdfSalt))
        {
            _data.KdfSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            dirty = true;
        }
        var salt = Convert.FromBase64String(_data.KdfSalt);
        _masterKey = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(_passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);

        // Migrate any legacy (CBC) entries to authenticated GCM.
        foreach (var e in _data.Entries)
        {
            if (string.IsNullOrEmpty(e.EncryptedValue) || e.EncryptedValue.StartsWith(GcmPrefix, StringComparison.Ordinal))
                continue;
            try
            {
                var plain = DecryptLegacyCbc(e.EncryptedValue);
                e.EncryptedValue = Encrypt(plain);
                dirty = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vault migration failed for key {Key} — wrong VAULT_KEY or corrupted entry", e.Key);
            }
        }

        if (dirty)
        {
            await _store.SaveAsync(_data);
            _logger.LogInformation("Vault initialized (PBKDF2 + AES-GCM); migrated legacy entries where present");
        }
    }

    public List<VaultEntry> ListSecrets()
    {
        // Read under the same lock the writers use, so the enumeration never observes a concurrent
        // Add/Remove mid-flight. Return metadata only — values are never exposed in the list.
        _lock.Wait();
        try
        {
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
        finally { _lock.Release(); }
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

        // Same lock as the writers: the entry lookup can't race a concurrent Add/Remove. Decrypt runs
        // under the lock (CPU-only, no re-entrant vault call, so no deadlock).
        _lock.Wait();
        try
        {
            var entry = _data.Entries.FirstOrDefault(e => e.Key == key);
            if (entry == null) return null;

            try { return Decrypt(entry.EncryptedValue); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vault decrypt failed for key {Key} — wrong VAULT_KEY or corrupted/tampered entry", key);
                return null;
            }
        }
        finally { _lock.Release(); }
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
        _lock.Wait();
        try
        {
            var now = DateTime.UtcNow;
            return _data.Entries.Where(e =>
                e.RotateAfterDays.HasValue &&
                (e.UpdatedAt ?? e.CreatedAt).AddDays(e.RotateAfterDays.Value) <= now
            ).ToList();
        }
        finally { _lock.Release(); }
    }

    private string Encrypt(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(_masterKey!, TagSize))
            gcm.Encrypt(nonce, plainBytes, cipher, tag);

        // Layout: nonce ‖ tag ‖ ciphertext, base64, with a version prefix.
        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return GcmPrefix + Convert.ToBase64String(blob);
    }

    private string Decrypt(string stored)
    {
        if (!stored.StartsWith(GcmPrefix, StringComparison.Ordinal))
            return DecryptLegacyCbc(stored);   // not yet migrated (should not happen post-InitializeAsync)

        var blob = Convert.FromBase64String(stored[GcmPrefix.Length..]);
        var nonce = blob[..NonceSize];
        var tag = blob[NonceSize..(NonceSize + TagSize)];
        var cipher = blob[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];

        using (var gcm = new AesGcm(_masterKey!, TagSize))
            gcm.Decrypt(nonce, cipher, tag, plain);   // throws CryptographicException on tamper/wrong key

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Decrypts a legacy AES-256-CBC entry (IV prepended, key = SHA256(passphrase)). Only used to
    /// read pre-GCM data during migration.</summary>
    private string DecryptLegacyCbc(string cipherBase64)
    {
        var cipherBytes = Convert.FromBase64String(cipherBase64);

        using var aes = Aes.Create();
        aes.Key = _legacyKey!;
        aes.IV = cipherBytes[..16];

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 16, cipherBytes.Length - 16);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
