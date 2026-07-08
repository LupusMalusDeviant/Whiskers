# Services/Hetzner

Client for the [Hetzner Cloud API](https://docs.hetzner.cloud/) (`https://api.hetzner.cloud/v1`). Used by the [`Cloud/`](../Cloud/) control plane and the Hetzner-specific MCP tools.

Credentials are **per Whiskers server** (each may live in a different Hetzner project), so every call takes an explicit project token and carries its own `Authorization` header, there is no shared/global token.

## Files

| File | Purpose |
|---|---|
| `IHetznerService.cs` / `HetznerApiService.cs` | Hetzner Cloud API client: power actions, snapshots, server metrics, rescue mode, backups, server-type changes, each call takes the per-server project token. |

## Behaviour notes

- **`hetzner_delete_snapshot` is snapshot-only:** it loads the image first (`GetImageAsync`) and refuses anything whose `type` isn't `"snapshot"` — backups and system/custom images are protected from an accidental id typo.
- **List endpoints paginate:** servers / snapshots / server-types are fetched page-by-page (`per_page=50`, the API max) until a short page, so an account with >50 entries is returned in full.

## Related

- Provider-agnostic dispatch: [`../Cloud/`](../Cloud/)
- Models: [`../../Models/Hetzner/`](../../Models/Hetzner/)
- MCP tools: `hetzner_enable_rescue`, `hetzner_disable_rescue`, `hetzner_enable_backups`, `hetzner_disable_backups`, `hetzner_list_snapshots`, `hetzner_delete_snapshot`, `hetzner_change_server_type`
