# Services/Hetzner

Client for the [Hetzner Cloud API](https://docs.hetzner.cloud/) (`https://api.hetzner.cloud/v1`). Used by the [`Cloud/`](../Cloud/) control plane and the Hetzner-specific MCP tools.

Credentials are **per ServerWatch server** (each may live in a different Hetzner project), so every call takes an explicit project token and carries its own `Authorization` header, there is no shared/global token.

## Files

| File | Purpose |
|---|---|
| `IHetznerService.cs` / `HetznerApiService.cs` | Hetzner Cloud API client: power actions, snapshots, server metrics, rescue mode, backups, server-type changes, each call takes the per-server project token. |

## Related

- Provider-agnostic dispatch: [`../Cloud/`](../Cloud/)
- Models: [`../../Models/Hetzner/`](../../Models/Hetzner/)
- MCP tools: `hetzner_enable_rescue`, `hetzner_disable_rescue`, `hetzner_enable_backups`, `hetzner_disable_backups`, `hetzner_list_snapshots`, `hetzner_delete_snapshot`, `hetzner_change_server_type`
