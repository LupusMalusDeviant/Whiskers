# Module: cve

CVE monitoring: a background scanner that checks each server's host OS (apt) and every running container's
image (Trivy) for known vulnerabilities, deduplicated per CVE, with an age indicator and notifications above a
configurable severity. The `/cves` page + the CVE MCP tools.

| | |
|---|---|
| **Id** | `cve` |
| **Enabled by default** | yes |
| **Toggle** | `Features:cve:Enabled` (env `Features__cve__Enabled=false`) — restart required |
| **Depends on** | — (soft dependency on Core via no-op defaults; uses `INotificationService` which has its own Core no-op) |
| **Nav** | `cves` — "CVE-Monitor" (group *Übersicht*) |
| **MCP tools** | `get_cve_summary`, `list_cve_groups`, `get_container_cves`, `get_server_cves` (dedicated `CveTools`) |
| **Services** | `ICveFindingsStore`, `ICveAgeStore`, `IOsCveScanner`, `ITrivyScanner`, hosted `ICveMonitorService` |

When **disabled**: the scanner doesn't run, the `cves` nav entry + MCP tools disappear, `/cves` shows a
"module disabled" notice, and the CVE panel in *Settings* is hidden.

**Soft dependency:** the Core `Dashboard` and `ContainerDetail` pages (plus `Settings`) read the CVE findings
store, so Core keeps `NoopCveFindingsStore` + `NoopCveMonitorService` (+ `NoopCveAgeStore`) defaults — those
pages then show no CVE data when the module is off, rather than failing.

Settings: the CVE panel (`CveMonitor` config section) — enable, scan targets (containers/host-OS), interval,
Trivy image, notify severity threshold, etc.

**Note:** the changeme C8 refactor (service-locator removal in `CveMonitorService`) is deferred to a separate
follow-up; this module PR is a byte-identical extraction.

Code: [`src/Whiskers/Modules/Cve/`](../../src/Whiskers/Modules/Cve/) · services in
[`src/Whiskers/Services/Cve/`](../../src/Whiskers/Services/Cve/) · tools in
[`src/Whiskers/Mcp/Tools/CveTools.cs`](../../src/Whiskers/Mcp/Tools/CveTools.cs).
