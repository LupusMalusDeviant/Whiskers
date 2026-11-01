using Whiskers.Models;

namespace Whiskers.Services.Vault;

/// <summary>Encrypted secret vault (master-key protected) for storing per-container secrets.</summary>
public interface IVaultService
{
    bool IsEnabled { get; }

    /// <summary>R1-28: whether the configured key is this vault's key. Only <see cref="VaultKeyState.Verified"/>
    /// reads and writes; a wrong or unprovable key leaves the file exactly as it is.</summary>
    VaultKeyState KeyState => IsEnabled ? VaultKeyState.Verified : VaultKeyState.NotConfigured;

    Task InitializeAsync(CancellationToken ct = default);
    List<VaultEntry> ListSecrets();
    [Whiskers.Services.Mutations.MutatingOperation("vault.secret_set")]
    Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null, int? rotateAfterDays = null);
    string? GetSecret(string key);
    [Whiskers.Services.Mutations.MutatingOperation("vault.secret_delete")]
    Task DeleteSecretAsync(string key);
    List<VaultEntry> GetExpiringSecrets();

    /// <summary>
    /// R1-29 start-up migration: stores a credential that is already kept in plaintext elsewhere under its fixed
    /// migration key (<see cref="CredentialRefs.IsMigrationKey"/>) and returns whether vault.json on disk now holds
    /// it. Not an operator's write: it accepts no other key, and it only adds to the vault what a file already holds in
    /// plaintext — so it is not a <c>vault.secret_set</c> and needs no gateway decision. The default (test doubles)
    /// stores nothing.
    /// </summary>
    Task<bool> AdoptLegacyCredentialAsync(string migrationKey, string plaintext) => Task.FromResult(false);

    /// <summary>
    /// R1-31 (INV-52): encrypts a value that is kept outside the vault — a webhook secret or a rollback snapshot in the
    /// database, a data-protection key in its file — with a key derived from the vault's (AES-256-GCM, bound to
    /// <paramref name="purpose"/>, so a value cannot be moved to another field or row). Null when the vault is not
    /// usable: the caller then stores nothing (see <see cref="ProtectedFields"/>). The default (test doubles) is null.
    /// </summary>
    string? ProtectField(string purpose, string plaintext) => null;

    /// <summary>Opens a value <see cref="ProtectField"/> returned for the same <paramref name="purpose"/>; false when the
    /// vault is not usable, the key is another, or the value was changed or moved.</summary>
    bool TryUnprotectField(string purpose, string stored, out string plaintext)
    {
        plaintext = "";
        return false;
    }
}

/// <summary>The vault key as the vault judged it at start (R1-28).</summary>
public enum VaultKeyState
{
    /// <summary>Neither VAULT_KEY nor VAULT_KEY_FILE is set: the vault is off.</summary>
    NotConfigured,

    /// <summary>The key decrypts the vault's key check (or an existing entry, or the vault is new).</summary>
    Verified,

    /// <summary>The key decrypts neither the key check nor any entry: nothing is read, written or migrated.</summary>
    Wrong,
}
