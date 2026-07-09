# Services/Server

Host-level operations that go beyond Docker, the things you'd normally SSH into a box to do. All of them run through the **host command executor**, which uses the SSH-free shell plane (a privileged `nsenter` container over the mTLS Docker channel) for TCP/mTLS servers.

## Files

| File | Purpose |
|---|---|
| `IHostCommandExecutor.cs` / `HostCommandExecutor.cs` | Runs a shell command on a host and returns a `CommandResult` (stdout/stderr/exit code). The shared primitive every other service in this folder builds on. |
| `IFirewallService.cs` / `FirewallService.cs` | Inspects and manages the host firewall (ufw), list, add, remove rules. |
| `INginxService.cs` / `NginxService.cs` | Lists and edits Nginx site configurations on a host. |
| `ISystemdService.cs` / `SystemdService.cs` | Lists and controls systemd units (start/stop/restart/status). |
| `ISslCertService.cs` / `SslCertService.cs` | Lists and renews TLS certificates (certbot / Let's Encrypt). |
| `NoopHostServices.cs` | Core no-op defaults for the four services above (`NoopFirewallService` et al.), used when the **HostManagement module** is off. Reads return empty; mutations return a *failed* `CommandResult` (never a fake success). Real services win by last-registration when the module is on (RoadToSAP Phase 1). |

## Wiring

`IHostCommandExecutor` stays in **Core** (many services depend on it). The four host-admin services
(firewall/nginx/systemd/ssl) are the opt-in **HostManagement module**
([`../../Modules/HostManagement/`](../../Modules/HostManagement/), toggle `Features:host-management:Enabled`).
The MCP tools for them live in the Core, mixed `ServerTools` (which also has `list_servers` / `get_server_info`
/ `execute_command`), so it can't move — hence the `NoopHostServices` defaults above keep `ServerTools` + the
four pages resolvable when the module is off.

## Related

- The mTLS/`nsenter` shell plane is implemented in [`../Docker/DockerService.cs`](../Docker/DockerService.cs); see [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md).
- Exposed to AI agents via the firewall/Nginx/systemd/SSL MCP tools in [`../../Mcp/Tools/`](../../Mcp/Tools/).
- Secret redaction for command output: [`../../Utils/SecretRedactor.cs`](../../Utils/SecretRedactor.cs).
