# Services/Vault

An **encrypted-at-rest secret vault**. Stores secrets (per-container secrets, API tokens) with **authenticated encryption (AES-256-GCM)** in `/app/data/vault.json`. The master key is derived from the `VAULT_KEY` environment variable via **PBKDF2-HMAC-SHA256 (600k iterations)** over a random, persisted per-vault salt (`VaultData.KdfSalt`).

Each secret is stored as `g1:` + base64(`nonce ‖ tag ‖ ciphertext`). The GCM authentication tag makes tampering detectable — a modified `vault.json` fails to decrypt instead of yielding attacker-influenced plaintext.

**Migration:** legacy entries written by the previous unauthenticated AES-256-CBC scheme (key = `SHA256(VAULT_KEY)`, no salt) are transparently decrypted and re-encrypted to GCM on the first `InitializeAsync` after upgrade. The re-encrypted file is saved once; no manual step is required. See [ADR 0001](../../../docs/adr/0001-vault-aead-gcm-pbkdf2.md).

**Concurrency:** the reads (`ListSecrets`/`GetSecret`/`GetExpiringSecrets`) run under the same lock as the writers (`SetSecretAsync`/`DeleteSecretAsync`), so a lookup or listing never races a concurrent set/delete.

## Files

| File | Purpose |
|---|---|
| `IVaultService.cs` / `VaultService.cs` | AES-256-GCM secret vault; PBKDF2-derived master key from `VAULT_KEY` (+ persisted salt), secrets stored encrypted at rest in `/app/data/vault.json`; auto-migrates legacy CBC entries. |

## Related

- Non-secret config export (excludes the vault): [`../ConfigExport/`](../ConfigExport/)
- UI: [`../../Components/Pages/Settings.razor`](../../Components/Pages/Settings.razor)
