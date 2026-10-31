using System.Security.Cryptography;
using System.Text;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Vault;

/// <summary>
/// Authenticated encrypted secret vault (AES-256-GCM). The master key is derived from the VAULT_KEY
/// passphrase with PBKDF2 (600k iterations) over a persisted per-vault salt. Secrets are stored encrypted
/// at rest in /app/data/vault.json as "g1:" + base64(nonce ‖ tag ‖ ciphertext). Legacy AES-CBC entries
/// (unauthenticated, key = SHA256(passphrase)) are migrated on load, after an untouched copy of the file is kept.
/// R1-28 (INV-54): the key is checked at start against a key check stored in the file (or, on an older vault, an
/// existing authenticated entry). A key that does not match reads, writes and migrates nothing and leaves the file
/// as it is. The passphrase comes from <c>VAULT_KEY_FILE</c> or <c>VAULT_KEY</c> (<see cref="SecretSource"/>).
/// </summary>
public class VaultService : IVaultService, IInitializable
{
    private const string GcmPrefix = "g1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 600_000;
    private const string KeyCheckText = "whiskers-vault-key-check-v1";

    /// <summary>Suffix of the untouched copy of vault.json kept before legacy CBC entries are converted.</summary>
    internal const string LegacyCopySuffix = ".legacy-cbc.bak";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _path;
    private readonly JsonFileStore<VaultData> _store;
    private readonly ILogger<VaultService> _logger;
    private readonly string? _passphrase;
    private readonly byte[]? _legacyKey;   // SHA256(passphrase) — only used to read/migrate old CBC entries
    private byte[]? _masterKey;             // PBKDF2-derived — set in InitializeAsync once the salt is known
    private VaultData _data = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public VaultService(IConfiguration configuration, ILogger<VaultService> logger, string? storePath = null, DataPathOptions? dataPaths = null)
    {
        _path = storePath ?? (dataPaths ?? DataPathOptions.Default).VaultJson;
        _store = new JsonFileStore<VaultData>(_path);
        _logger = logger;

        // A VAULT_KEY_FILE that cannot be read stops the start here (SecretFileException): never a silent "no key".
        _passphrase = SecretSource.VaultKey(configuration);
        if (_passphrase is not null)
            _legacyKey = SHA256.HashData(Encoding.UTF8.GetBytes(_passphrase));
        else
            _logger.LogWarning("VAULT_KEY not set — vault is disabled. Set VAULT_KEY or VAULT_KEY_FILE to enable.");
    }

    public bool IsEnabled => KeyState == VaultKeyState.Verified;

    public VaultKeyState KeyState { get; private set; } = VaultKeyState.NotConfigured;

    public int Order => 40;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_store.Exists())
        {
            _data = await _store.LoadAsync();
            _logger.LogInformation("Vault loaded: {Count} secrets", _data.Entries.Count);
        }

        if (_passphrase is null) return;

        var data = _data;
        var gcm = data.Entries.Where(e => IsGcm(e.EncryptedValue)).ToList();
        var legacy = data.Entries.Where(e => !string.IsNullOrEmpty(e.EncryptedValue) && !IsGcm(e.EncryptedValue)).ToList();

        // The derived key: from the persisted salt, or from a new one for a vault that has never had one. A vault that
        // carries a key check or authenticated entries but no salt can match no key.
        byte[] salt;
        var newSalt = string.IsNullOrEmpty(data.KdfSalt);
        if (newSalt && (data.KeyCheck is not null || gcm.Count > 0))
        {
            Reject("vault.json has encrypted entries but no key salt");
            return;
        }
        try
        {
            salt = newSalt ? RandomNumberGenerator.GetBytes(16) : Convert.FromBase64String(data.KdfSalt!);
        }
        catch (FormatException)
        {
            Reject("the key salt in vault.json is not valid");
            return;
        }
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(_passphrase), salt, Pbkdf2Iterations,
            HashAlgorithmName.SHA256, 32);

        // Proof of the key: the key check, else any authenticated entry (GCM fails on a wrong key). A vault of legacy
        // CBC entries only has no proof — a padding that happens to fit is none — so every entry must decrypt to valid
        // text, and the conversion below keeps the original file untouched beside it.
        string? rejection = null;
        if (data.KeyCheck is not null)
        {
            if (!Opens(key, data.KeyCheck, out var text) || text != KeyCheckText)
                rejection = "VAULT_KEY does not open the vault's key check";
        }
        else if (gcm.Count > 0)
        {
            if (!gcm.Any(e => Opens(key, e.EncryptedValue, out _)))
                rejection = "VAULT_KEY opens none of the vault's entries";
        }
        else if (legacy.Count > 0 && !legacy.All(e => TryLegacy(e.EncryptedValue, out _)))
        {
            rejection = "VAULT_KEY does not decrypt the vault's legacy entries";
        }
        if (rejection is not null)
        {
            Reject(rejection);
            return;
        }

        _masterKey = key;
        KeyState = VaultKeyState.Verified;

        var next = Copy(data);
        var dirty = false;
        if (newSalt)
        {
            next.KdfSalt = Convert.ToBase64String(salt);
            dirty = true;
        }
        var provenStrongly = data.KeyCheck is not null || gcm.Count > 0;
        if (legacy.Count > 0)
            dirty |= MigrateLegacy(next, provenByKeyCheck: provenStrongly);
        // The key check is written once the key is the vault's: proven by an authenticated value, new, or converted
        // from legacy entries (whose original stays in the recovery copy). Only a legacy-only vault whose conversion
        // failed gets no check: its key is not proven, and a check would bind the vault to it.
        if (next.KeyCheck is null
            && (provenStrongly || next.Entries.All(e => string.IsNullOrEmpty(e.EncryptedValue) || IsGcm(e.EncryptedValue))))
        {
            next.KeyCheck = Encrypt(key, KeyCheckText);
            dirty = true;
        }

        if (dirty)
        {
            await _store.SaveAsync(next);
            _data = next;
            _logger.LogInformation("Vault initialized (PBKDF2 + AES-GCM, key checked)");
        }
    }

    /// <summary>Converts the legacy entries of <paramref name="next"/> to AES-GCM — all of them or none — once an
    /// untouched copy of vault.json is kept beside it.</summary>
    private bool MigrateLegacy(VaultData next, bool provenByKeyCheck)
    {
        // Decrypt every legacy entry first: a vault that cannot be converted gets no copy (one per start would pile up).
        var plains = new Dictionary<VaultEntry, string>();
        foreach (var e in next.Entries.Where(e => !string.IsNullOrEmpty(e.EncryptedValue) && !IsGcm(e.EncryptedValue)))
        {
            if (!TryLegacy(e.EncryptedValue, out var plain))
            {
                _logger.LogError("Vault: legacy entry {Key} does not decrypt — no legacy entry converted", e.Key);
                return false;
            }
            plains[e] = plain;
        }

        string copy;
        try
        {
            copy = KeepRecoveryCopy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Vault: legacy entries not converted, the recovery copy of {Path} could not be written", _path);
            return false;
        }

        var converted = plains.ToDictionary(p => p.Key, p => Encrypt(_masterKey!, p.Value));
        foreach (var (entry, value) in converted) entry.EncryptedValue = value;
        if (!provenByKeyCheck)
            _logger.LogWarning("Vault: {Count} legacy entries converted with a key that only their padding and text could "
                               + "confirm; the original file is kept as {Copy}", converted.Count, copy);
        return converted.Count > 0;
    }

    /// <summary>Keeps a byte-identical copy of vault.json as it is now and returns its path. A copy is never
    /// overwritten: one that holds exactly this file is used as it is; one that differs (cut short by a crash, or the
    /// copy of another vault that a restore replaced) stays, and the copy is written under a new name beside it. The
    /// copy is written to a temporary file, flushed and then moved into place, so it is complete or absent.</summary>
    private string KeepRecoveryCopy()
    {
        var original = File.ReadAllBytes(_path);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        for (var n = 0; ; n++)
        {
            var copy = n == 0 ? _path + LegacyCopySuffix : $"{_path}.legacy-cbc.{stamp}-{n}.bak";
            if (File.Exists(copy))
            {
                if (File.ReadAllBytes(copy).AsSpan().SequenceEqual(original)) return copy;
                continue;
            }
            var tmp = copy + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(original);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, copy, overwrite: false);
            return copy;
        }
    }

    private void Reject(string reason)
    {
        _masterKey = null;
        KeyState = VaultKeyState.Wrong;
        _logger.LogError("VAULT KEY REJECTED: {Reason}. Secrets are not read, written or migrated and vault.json is left "
                         + "unchanged until the right VAULT_KEY is set.", reason);
    }

    public List<VaultEntry> ListSecrets()
    {
        // A locked vault shows nothing: its entries belong to a key that is not the one configured.
        if (KeyState == VaultKeyState.Wrong) return [];
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
        EnsureWritable();
        await StoreAsync(key, plainValue, containerId, containerName, rotateAfterDays);
    }

    private async Task StoreAsync(string key, string plainValue, string? containerId, string? containerName, int? rotateAfterDays)
    {
        await _lock.WaitAsync();
        try
        {
            var encrypted = Encrypt(_masterKey!, plainValue);
            // Copy-on-write (R1-11): the new entry list is published only once it is written, so a failed write
            // leaves the live vault as it was.
            var next = Copy(_data);
            var existing = next.Entries.FirstOrDefault(e => e.Key == key);

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
                next.Entries.Add(new VaultEntry
                {
                    Key = key,
                    EncryptedValue = encrypted,
                    ContainerId = containerId,
                    ContainerName = containerName,
                    CreatedAt = DateTime.UtcNow,
                    RotateAfterDays = rotateAfterDays
                });
            }

            await _store.SaveAsync(next);
            _data = next;
            _logger.LogInformation("Vault secret set: {Key}", key);
        }
        finally { _lock.Release(); }
    }

    public string? GetSecret(string key)
    {
        if (!IsEnabled) return null;

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
        EnsureWritable();
        await _lock.WaitAsync();
        try
        {
            var next = Copy(_data);
            next.Entries.RemoveAll(e => e.Key == key);
            await _store.SaveAsync(next);
            _data = next;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Writes need the vault's own key: without a key, or with a wrong one, the file stays as it is.</summary>
    private void EnsureWritable()
    {
        if (KeyState == VaultKeyState.Wrong)
            throw new InvalidOperationException("Vault ist gesperrt: VAULT_KEY passt nicht zu diesem Vault. Den richtigen Schlüssel setzen und neu starten.");
        if (!IsEnabled)
            throw new InvalidOperationException("Vault ist nicht aktiviert. VAULT_KEY Umgebungsvariable setzen.");
    }

    /// <summary>A copy of the vault data whose entries can be changed without touching the live ones.</summary>
    private static VaultData Copy(VaultData data)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        return System.Text.Json.JsonSerializer.Deserialize<VaultData>(json) ?? new VaultData();
    }

    /// <inheritdoc />
    public async Task<bool> AdoptLegacyCredentialAsync(string migrationKey, string plaintext)
    {
        if (!CredentialRefs.IsMigrationKey(migrationKey))
            throw new ArgumentException("Only a credential migration key is adopted.", nameof(migrationKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        EnsureWritable();
        // Already there from an interrupted run: nothing to write.
        if (await IsPersistedAsync(migrationKey, plaintext)) return true;
        await StoreAsync(migrationKey, plaintext, null, null, null);
        return await IsPersistedAsync(migrationKey, plaintext);
    }

    /// <inheritdoc />
    public async Task<bool> IsPersistedAsync(string key, string plainValue)
    {
        if (!IsEnabled || !_store.Exists()) return false;
        var onDisk = await _store.LoadAsync();
        var entry = onDisk.Entries.FirstOrDefault(e => e.Key == key);
        if (entry is null || !IsGcm(entry.EncryptedValue)) return false;
        return Opens(_masterKey!, entry.EncryptedValue, out var plain) && plain == plainValue;
    }

    public List<VaultEntry> GetExpiringSecrets()
    {
        // A locked vault cannot rotate anything: no reminders for secrets nobody can write.
        if (KeyState == VaultKeyState.Wrong) return [];
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

    private static bool IsGcm(string? stored) => stored?.StartsWith(GcmPrefix, StringComparison.Ordinal) == true;

    private static string Encrypt(byte[] key, string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, plainBytes, cipher, tag);

        // Layout: nonce ‖ tag ‖ ciphertext, base64, with a version prefix.
        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return GcmPrefix + Convert.ToBase64String(blob);
    }

    /// <summary>Opens a "g1:" value with <paramref name="key"/>; false on a wrong key, a changed value or bad data.</summary>
    private static bool Opens(byte[] key, string stored, out string plain)
    {
        plain = "";
        try
        {
            plain = Decrypt(key, stored);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    // A legacy value is still read only while its conversion could not run (no recovery copy).
    private string Decrypt(string stored) => IsGcm(stored) ? Decrypt(_masterKey!, stored) : StrictLegacy(stored);

    private static string Decrypt(byte[] key, string stored)
    {
        if (!IsGcm(stored)) throw new FormatException("not an AES-GCM value");
        var blob = Convert.FromBase64String(stored[GcmPrefix.Length..]);
        if (blob.Length < NonceSize + TagSize) throw new FormatException("AES-GCM value too short");
        var nonce = blob[..NonceSize];
        var tag = blob[NonceSize..(NonceSize + TagSize)];
        var cipher = blob[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];

        using (var gcm = new AesGcm(key, TagSize))
            gcm.Decrypt(nonce, cipher, tag, plain);   // throws CryptographicException on tamper/wrong key

        return Encoding.UTF8.GetString(plain);
    }

    private bool TryLegacy(string stored, out string plain)
    {
        plain = "";
        try
        {
            plain = StrictLegacy(stored);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Decrypts a legacy AES-256-CBC entry (IV prepended, key = SHA256(passphrase)) to text that must be valid
    /// UTF-8. A padding that fits is no proof of the key; text that is no UTF-8 is a sure sign of the wrong one.</summary>
    private string StrictLegacy(string cipherBase64)
    {
        var cipherBytes = Convert.FromBase64String(cipherBase64);
        if (cipherBytes.Length < 32 || cipherBytes.Length % 16 != 0) throw new FormatException("not a legacy AES-CBC value");

        using var aes = Aes.Create();
        aes.Key = _legacyKey!;
        aes.IV = cipherBytes[..16];

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 16, cipherBytes.Length - 16);
        return StrictUtf8.GetString(plainBytes);
    }
}
