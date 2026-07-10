# Module: cloud-control

Out-of-band cloud control: power actions (reboot / shutdown / hard-reset / power-on) and snapshots on cloud
servers through provider APIs (Hetzner, Hostinger). The `/cloud` page + the cloud/Hetzner MCP tools.

| | |
|---|---|
| **Id** | `cloud-control` |
| **Enabled by default** | yes |
| **Toggle** | `Features:cloud-control:Enabled` (env `Features__cloud-control__Enabled=false`) — restart required |
| **Depends on** | — |
| **Nav** | `cloud` — "Cloud" (group *Infrastruktur*) |
| **MCP tools** | `list_cloud_servers`, `cloud_status/reboot/shutdown/power_on/hard_reset/metrics/create_snapshot` (`CloudTools`) + `hetzner_*` (`HetznerTools`) |
| **Services** | `ICloudControlService` (+ provider clients `IHetznerService`, `IHostingerService`) |

When **disabled**: the `cloud` nav entry + MCP tools disappear and `/cloud` shows a "module disabled" notice.
A clean extraction — no Core page or service consumes these, so no no-op defaults are needed.

Credentials: per-server cloud provider + API key are configured under *Server* (not a Settings panel here).

**Deferred:** the §3.6 C10 `ICloudProvider` seam (Hetzner/Hostinger as pluggable providers behind a common
contract) is a separate refactor of destructive power/snapshot dispatch — this module PR is a byte-identical
extraction.

Code: [`src/Whiskers/Modules/CloudControl/`](../../src/Whiskers/Modules/CloudControl/) · dispatcher in
[`Services/Cloud`](../../src/Whiskers/Services/Cloud/) · providers in
[`Services/Hetzner`](../../src/Whiskers/Services/Hetzner/),
[`Services/Hostinger`](../../src/Whiskers/Services/Hostinger/) · tools in
[`Mcp/Tools/CloudTools.cs`](../../src/Whiskers/Mcp/Tools/CloudTools.cs) +
[`HetznerTools.cs`](../../src/Whiskers/Mcp/Tools/HetznerTools.cs).
