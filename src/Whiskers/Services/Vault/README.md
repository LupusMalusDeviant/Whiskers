# Services/Vault

An **encrypted-at-rest secret vault**. Stores secrets (per-container secrets, API tokens) with **authenticated encryption (AES-256-GCM)** in `/app/data/vault.json`. The master key is derived from `VAULT_KEY_FILE` (a secret file, wins) or the `VAULT_KEY` environment variable via **PBKDF2-HMAC-SHA256 (600k iterations)** over a random, persisted per-vault salt (`VaultData.KdfSalt`).

Each secret is stored as `g1:` + base64(`nonce ‖ tag ‖ ciphertext`). The GCM authentication tag makes tampering detectable — a modified `vault.json` fails to decrypt instead of yielding attacker-influenced plaintext.

**Key check (R1-28, INV-54):** `VaultData.KeyCheck` holds a fixed text sealed with the derived key. At start the key must open it — or, on a vault written before R1-28, one of its GCM entries (the check is then added). Otherwise `KeyState` is `Wrong`: `IsEnabled` is false, nothing is read, written, deleted or migrated, and `vault.json` is left byte for byte as it was. `KeyState` is `NotConfigured` without a key and `Verified` with the vault's key. A configured `VAULT_KEY_FILE` that cannot be read stops the start (`SecretSource`).

**Migration:** legacy entries written by the previous unauthenticated AES-256-CBC scheme (key = `SHA256(VAULT_KEY)`, no salt) are re-encrypted to GCM on the first `InitializeAsync` after upgrade, all or none, after an untouched copy `vault.json.legacy-cbc.bak` is written. A vault of legacy entries only carries no proof of the key, so every entry must decrypt to valid UTF-8 — a padding that fits is not enough — or the vault is locked as `Wrong`. A locked vault lists no entries and no expiring secrets.

**Recovery:** if a legacy vault was converted under the wrong key (only possible when every legacy entry happens to decrypt to valid text under it), every later start with the right key reports the vault as locked. Stop Whiskers, replace `vault.json` with the recovery copy `vault.json.legacy-cbc.bak` (or the newest `vault.json.legacy-cbc.<time>-<n>.bak`), set the right `VAULT_KEY` and start again; the conversion then runs with the right key. The copy is written to a temporary file, flushed and moved into place, so it is complete or absent; an existing copy is never overwritten — one that differs from the current file (cut short, or from another vault a restore replaced) stays, and the new copy gets a new name. It holds the old unauthenticated CBC ciphertext and travels in every self-backup: delete it once the converted vault has been checked (its secrets read correctly). When no copy can be written nothing is converted (the salt of a vault that had none is still saved).

An **empty** vault (no entries) carries nothing that could prove a key, so it accepts the key it is first started with and seals its key check under it; to re-key an empty vault (or one locked by a mistyped key before anything was stored), stop Whiskers, delete `vault.json` and start with the right key. A vault whose key check or every entry was altered locks even with the right key — the only remedy is to restore `vault.json` from a self-backup. See [ADR 0001](../../../docs/adr/0001-vault-aead-gcm-pbkdf2.md).

**Concurrency:** the reads (`ListSecrets`/`GetSecret`/`GetExpiringSecrets`) run under the same lock as the writers (`SetSecretAsync`/`DeleteSecretAsync`), so a lookup or listing never races a concurrent set/delete. Writes are copy-on-write: a change is published only after `vault.json` is written, so a failed write leaves the live vault unchanged. Set/delete are `vault.secret_set` / `vault.secret_delete` (Admin, R1-11).

## Files

| File | Purpose |
|---|---|
| `IVaultService.cs` / `VaultService.cs` | AES-256-GCM secret vault; PBKDF2-derived master key from `VAULT_KEY_FILE`/`VAULT_KEY` (+ persisted salt), key check at start (`KeyState`), secrets stored encrypted at rest in `/app/data/vault.json`; migrates legacy CBC entries behind a recovery copy. |

## Related

- Non-secret config export (excludes the vault): [`../ConfigExport/`](../ConfigExport/)
- UI: [`../../Components/Pages/Settings.razor`](../../Components/Pages/Settings.razor)
