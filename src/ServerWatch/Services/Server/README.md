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

## Related

- The mTLS/`nsenter` shell plane is implemented in [`../Docker/DockerService.cs`](../Docker/DockerService.cs); see [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md).
- Exposed to AI agents via the firewall/Nginx/systemd/SSL MCP tools in [`../../Mcp/Tools/`](../../Mcp/Tools/).
- Secret redaction for command output: [`../../Utils/SecretRedactor.cs`](../../Utils/SecretRedactor.cs).
