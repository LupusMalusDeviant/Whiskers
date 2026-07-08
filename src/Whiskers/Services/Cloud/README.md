# Services/Cloud

**Out-of-band cloud control.** A provider-agnostic layer for power, snapshot and metrics operations on cloud servers, it works even when SSH/Docker is unreachable, because it talks to the cloud provider's API, not the box itself.

It resolves a Whiskers server's configured provider + per-server API key, finds the matching VM in that account (by public IP), and dispatches the operation to the right provider client ([`../Hetzner/`](../Hetzner/) or [`../Hostinger/`](../Hostinger/)).

## Files

| File | Purpose |
|---|---|
| `ICloudControlService.cs` / `CloudControlService.cs` | Provider-agnostic control plane: resolves provider + credentials, matches the server in the account, and dispatches power/snapshot/metrics calls to the provider-specific client. |

## Behaviour notes

- **Weak-resolution warning:** the VM is matched by public IP; when that fails and only a name-match succeeds (common, since `SshHost` is often a mesh address ≠ the cloud public IP), the result carries a `Note` that power/reset responses surface with ⚠️ — so a destructive op on the wrong VM is visible.
- **Hostinger has no hard reset:** `cloud_hard_reset` on a Hostinger VM triggers a graceful RESTART and says so explicitly (may be ineffective on a hung system); Hetzner does a real power-cycle.

## Related

- Provider clients: [`../Hetzner/`](../Hetzner/), [`../Hostinger/`](../Hostinger/)
- Models: [`../../Models/Cloud/`](../../Models/Cloud/)
- UI: [`../../Components/Pages/Cloud.razor`](../../Components/Pages/Cloud.razor)
- MCP tools: `list_cloud_servers`, `cloud_status`, `cloud_metrics`, `cloud_power_on`, `cloud_shutdown`, `cloud_reboot`, `cloud_hard_reset`, `cloud_create_snapshot`
