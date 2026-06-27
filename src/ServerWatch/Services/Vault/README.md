# Services/Vault

An **encrypted-at-rest secret vault**. Stores secrets (per-container secrets, API tokens) AES-256 encrypted in `/app/data/vault.json`, protected by a master key from the `VAULT_KEY` environment variable.

## Files

| File | Purpose |
|---|---|
| `IVaultService.cs` / `VaultService.cs` | AES-256 encrypted secret vault; master key from `VAULT_KEY`, secrets stored encrypted at rest in `/app/data/vault.json`. |

## Related

- Non-secret config export (excludes the vault): [`../ConfigExport/`](../ConfigExport/)
- UI: [`../../Components/Pages/Settings.razor`](../../Components/Pages/Settings.razor)
