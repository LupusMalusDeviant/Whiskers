# Module: host-management

Host administration for a server, bundled as one module: firewall (ufw) rules, nginx site configs, systemd
units and TLS certificates (certbot). Four `{ServerId}`-parameterized pages reached from the Servers page.

| | |
|---|---|
| **Id** | `host-management` |
| **Enabled by default** | yes |
| **Toggle** | `Features:host-management:Enabled` (env `Features__host-management__Enabled=false`) — restart required |
| **Depends on** | — (Core `ServerTools` + pages use Core no-op defaults) |
| **Nav** | — (pages reached from the Servers page: `/firewall/{id}`, `/nginx/{id}`, `/services/{id}`, `/ssl/{id}`) |
| **MCP tools** | — of its own; the firewall/nginx/systemd/ssl tools stay in the Core, mixed `ServerTools` |
| **Services** | `IFirewallService`, `INginxService`, `ISystemdService`, `ISslCertService` |

When **disabled**: the four pages show a "module disabled" notice, and the host-management MCP tools (in
`ServerTools`) answer "module disabled" — reads return empty, mutations return a failed result (they never
fake a success like "firewall rule added").

**Why the tools stay in Core:** the host-management MCP tools live in `ServerTools`, which also holds core
server ops (`list_servers`, `get_server_info`, `execute_command`). Under the byte-gleich move rule a tool class
can't be split, so `ServerTools` stays in Core; the four services get Core no-op defaults for when the module
is off. `IHostCommandExecutor` (shared by many services) also stays Core.

No settings section (managed on the per-server pages).

Code: [`src/Whiskers/Modules/HostManagement/`](../../src/Whiskers/Modules/HostManagement/) · services in
[`src/Whiskers/Services/Server/`](../../src/Whiskers/Services/Server/) · tools in
[`src/Whiskers/Mcp/Tools/ServerTools.cs`](../../src/Whiskers/Mcp/Tools/ServerTools.cs) (Core).
