# Modules/HostManagement

Host administration for a server — firewall (ufw), nginx sites, systemd units and TLS certificates (certbot),
bundled as **one** module because they share the Core `IHostCommandExecutor` and the same audience (you turn
host management on or off as a set). The seventh feature extracted from `AllInOnePseudoModule` (RoadToSAP
Phase 1). No top-level nav (the pages are reached from the Servers page) and no MCP tools of its own.

- `HostManagementModule.cs` — `Id = "host-management"`, enabled by default. `ConfigureServices` (moved
  **verbatim** from `Program.cs`) registers `IFirewallService`, `INginxService`, `ISystemdService`,
  `ISslCertService`. Pages: `/firewall/{id}`, `/nginx/{id}`, `/services/{id}`, `/ssl/{id}`.

**Toggle:** `Features:host-management:Enabled` (`Features__host-management__Enabled=false`), restart-only. When
off, the four pages show a "module disabled" notice.

**Why no-ops, and why the MCP tools stay in Core.** The firewall/nginx/systemd/ssl MCP tools live in
`ServerTools`, which **also** carries core server ops (`list_servers`, `get_server_info`, `execute_command`).
Splitting a tool class isn't allowed under the byte-gleich move rule, so `ServerTools` **stays in Core** — and
it (plus the four pages) still injects these services. Core therefore registers no-op defaults
([`NoopHostServices.cs`](../../Services/Server/NoopHostServices.cs): `NoopFirewallService` et al.) before the
module loop; the real services win by last-registration when enabled. With the module off, reads return empty
and mutations return a **failed** `CommandResult` (never a fake "rule added" / "cert renewed" — a security
footgun), so tools/pages answer "module disabled" cleanly.

**Page gating.** The four `@page` files wrap their content in `<ModuleGuard ModuleId="host-management">`
inline (no separate `*View` split is needed here: the service injections resolve the Core no-op either way, so
they never throw at instantiation, and each page's `OnInitializedAsync` loads through a try/catch). Service
code stays in [`../../Services/Server/`](../../Services/Server/); `IHostCommandExecutor` remains Core.

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and
[`docs/modules/host-management.md`](../../../../docs/modules/host-management.md).
