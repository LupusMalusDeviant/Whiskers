# Services/Vault

An **encrypted-at-rest secret vault**. Stores secrets (per-container secrets, API tokens) with **authenticated encryption (AES-256-GCM)** in `/app/data/vault.json`. The master key is derived from `VAULT_KEY_FILE` (a secret file, wins) or the `VAULT_KEY` environment variable via **PBKDF2-HMAC-SHA256 (600k iterations)** over a random, persisted per-vault salt (`VaultData.KdfSalt`).

Each secret is stored as `g1:` + base64(`nonce ‖ tag ‖ ciphertext`). The GCM authentication tag makes tampering detectable — a modified `vault.json` fails to decrypt instead of yielding attacker-influenced plaintext.

**Key check (R1-28, INV-54):** `VaultData.KeyCheck` holds a fixed text sealed with the derived key. At start the key must open it — or, on a vault written before R1-28, one of its GCM entries (the check is then added). Otherwise `KeyState` is `Wrong`: `IsEnabled` is false, nothing is read, written, deleted or migrated, and `vault.json` is left byte for byte as it was. `KeyState` is `NotConfigured` without a key and `Verified` with the vault's key. A configured `VAULT_KEY_FILE` that cannot be read stops the start (`SecretSource`).

**Migration:** legacy entries written by the previous unauthenticated AES-256-CBC scheme (key = `SHA256(VAULT_KEY)`, no salt) are re-encrypted to GCM on the first `InitializeAsync` after upgrade, all or none, after an untouched copy `vault.json.legacy-cbc.bak` is written (never overwritten; no conversion when it cannot be written). A vault of legacy entries only carries no proof of the key, so every entry must decrypt to valid UTF-8 — a padding that fits is not enough — or the vault is locked as `Wrong`. See [ADR 0001](../../../docs/adr/0001-vault-aead-gcm-pbkdf2.md).

**Concurrency:** the reads (`ListSecrets`/`GetSecret`/`GetExpiringSecrets`) run under the same lock as the writers (`SetSecretAsync`/`DeleteSecretAsync`), so a lookup or listing never races a concurrent set/delete. Writes are copy-on-write: a change is published only after `vault.json` is written, so a failed write leaves the live vault unchanged. Set/delete are `vault.secret_set` / `vault.secret_delete` (Admin, R1-11).

## Files

| File | Purpose |
|---|---|
| `IVaultService.cs` / `VaultService.cs` | AES-256-GCM secret vault; PBKDF2-derived master key from `VAULT_KEY_FILE`/`VAULT_KEY` (+ persisted salt), key check at start (`KeyState`), secrets stored encrypted at rest in `/app/data/vault.json`; migrates legacy CBC entries behind a recovery copy. |

## Related

- Non-secret config export (excludes the vault): [`../ConfigExport/`](../ConfigExport/)
- UI: [`../../Components/Pages/Settings.razor`](../../Components/Pages/Settings.razor)
