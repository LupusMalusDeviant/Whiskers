namespace Whiskers.Services.Vault;

/// <summary>One reader of <c>VAULT_KEY</c> (directly or through the vault's master or field key) and how the rotation
/// treats what it seals or signs.</summary>
/// <param name="Name">What is sealed or signed.</param>
/// <param name="Source">The source files (relative to <c>src/Whiskers</c>) that read the key or use the vault's keys for it.</param>
/// <param name="Rotation">What <see cref="VaultKeyRotation"/> does with it.</param>
public sealed record VaultKeyConsumer(string Name, IReadOnlyList<string> Source, string Rotation);

/// <summary>
/// Contract 0015 Part B: every reader of <see cref="Whiskers.Configuration.SecretSource.VaultKey"/> and every use of the
/// vault's master or field key. <c>VaultKeyConsumersTests</c> reads the source and fails when a file reads the key or
/// protects a value with it and is not listed here — a new consumer must say how the rotation handles it.
/// </summary>
public static class VaultKeyConsumers
{
    public static IReadOnlyList<VaultKeyConsumer> All { get; } =
    [
        new("vault.json entries and key check",
            ["Services/Vault/VaultService.cs", "Services/Vault/IVaultService.cs"],
            "re-written with a new salt and key check beside the original (vault.json.rotating), every entry read back"),
        new("protected fields in the database (webhook secrets, rollback snapshots)",
            ["Services/Webhooks/WebhookService.cs", "Services/AutoUpdate/AutoUpdateService.cs", "Services/Vault/ProtectedFields.cs",
             "Services/Vault/ProtectedFieldMigration.cs"],
            "re-sealed in the database transaction"),
        new("data-protection key ring (keys/*.xml)",
            ["Services/Vault/VaultKeyRing.cs"],
            "each vault-protected secret re-sealed into key-*.xml.rotating"),
        new("sealed SSH and mTLS client key files (contract 0015 Part A)",
            ["Services/Vault/SealedKeyFiles.cs"],
            "each sealed file re-sealed into *.rotating"),
        new("audit chain signatures (links, checkpoints, audit-checkpoint.json)",
            ["Services/AuditLog/AuditChain.cs"],
            "verified completely with the current key first (D8), then re-signed in the database transaction and the file"),
        new("self-backup archives",
            ["Services/Backup/BackupService.cs", "Components/Pages/SelfBackupPanel.razor"],
            "not re-sealed (D9): older archives stay restorable with their own key and are marked; a fresh backup follows"),
        new("the key's source (VAULT_KEY_FILE / VAULT_KEY)",
            ["Configuration/SecretSource.cs"],
            "after the rotation the process reads the new key (VAULT_KEY_NEW_FILE) until the operator replaces VAULT_KEY_FILE"),
        new("the rotation itself",
            ["Services/Vault/VaultKeyRotation.cs", "Services/Vault/VaultKeyRotationBootHandler.cs"],
            "reads the current and the new key"),
    ];
}
