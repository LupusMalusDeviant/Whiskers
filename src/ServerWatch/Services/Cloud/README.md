# Services/Cloud

**Out-of-band cloud control.** A provider-agnostic layer for power, snapshot and metrics operations on cloud servers, it works even when SSH/Docker is unreachable, because it talks to the cloud provider's API, not the box itself.

It resolves a ServerWatch server's configured provider + per-server API key, finds the matching VM in that account (by public IP), and dispatches the operation to the right provider client ([`../Hetzner/`](../Hetzner/) or [`../Hostinger/`](../Hostinger/)).

## Files

| File | Purpose |
|---|---|
| `ICloudControlService.cs` / `CloudControlService.cs` | Provider-agnostic control plane: resolves provider + credentials, matches the server in the account, and dispatches power/snapshot/metrics calls to the provider-specific client. |

## Related

- Provider clients: [`../Hetzner/`](../Hetzner/), [`../Hostinger/`](../Hostinger/)
- Models: [`../../Models/Cloud/`](../../Models/Cloud/)
- UI: [`../../Components/Pages/Cloud.razor`](../../Components/Pages/Cloud.razor)
- MCP tools: `list_cloud_servers`, `cloud_status`, `cloud_metrics`, `cloud_power_on`, `cloud_shutdown`, `cloud_reboot`, `cloud_hard_reset`, `cloud_create_snapshot`
