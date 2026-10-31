# Services/Cloud

**Out-of-band cloud control.** A provider-agnostic layer for power, snapshot and metrics operations on cloud servers, it works even when SSH/Docker is unreachable, because it talks to the cloud provider's API, not the box itself.

It resolves a Whiskers server's configured provider + per-server API key, finds the matching VM in that account (by public IP), and dispatches the operation to the right provider client ([`../Hetzner/`](../Hetzner/) or [`../Hostinger/`](../Hostinger/)).

## Files

| File | Purpose |
|---|---|
| `ICloudControlService.cs` / `CloudControlService.cs` | Provider-agnostic control plane: resolves provider + credentials, matches the server in the account, and dispatches power/snapshot/metrics calls to the provider-specific client. The API key comes from the vault (R1-29). |
| `CloudCredentials.cs` | R1-29: where a server's cloud API key lives — in the vault under `ServerConfig.CloudKeyRef`, or (not migrated yet, no usable vault) still as the legacy plaintext field. `KeyOf` reads it; a key kept in a disabled or locked vault is an error, never a silent "no key". |

## Behaviour notes

- **Weak-resolution warning:** the VM is matched by public IP; when that fails and only a name-match succeeds (common, since `SshHost` is often a mesh address ≠ the cloud public IP), the result carries a `Note` that power/reset responses surface with ⚠️ — so a destructive op on the wrong VM is visible.
- **Hostinger has no hard reset:** `cloud_hard_reset` on a Hostinger VM triggers a graceful RESTART and says so explicitly (may be ineffective on a hung system); Hetzner does a real power-cycle.
- **A failed action is a failure:** a Hetzner action answered with status `error` throws (with Hetzner's error message) instead of returning a message, so the gateway's event is Failed (R1-21); the Hostinger hard-reset fallback is noted in the event.

## Wiring

This service, its two provider clients ([`../Hetzner/`](../Hetzner/), [`../Hostinger/`](../Hostinger/)) and the
`CloudTools` + `HetznerTools` MCP classes are the opt-in **CloudControl module**
([`../../Modules/CloudControl/`](../../Modules/CloudControl/), toggle `Features:cloud-control:Enabled`). It's a
clean extraction — nothing in Core consumes them, so there are no no-op defaults; the `/cloud` page uses the
`ModuleGuard` wrapper pattern. The §3.6 C10 `ICloudProvider` seam (pluggable providers behind a common
contract) is deferred to a separate refactor.

## Related

- Provider clients: [`../Hetzner/`](../Hetzner/), [`../Hostinger/`](../Hostinger/)
- Models: [`../../Models/Cloud/`](../../Models/Cloud/)
- UI: [`../../Components/Pages/Cloud.razor`](../../Components/Pages/Cloud.razor)
- MCP tools: `list_cloud_servers`, `cloud_status`, `cloud_metrics`, `cloud_power_on`, `cloud_shutdown`, `cloud_reboot`, `cloud_hard_reset`, `cloud_create_snapshot`
