# Services/Cve

Vulnerability scanning. A background monitor periodically scans both **host OS packages** and **container images** for known CVEs and stores the latest findings per server/container for the UI and MCP tools.

Findings are **de-duplicated per CVE-ID** for display (one CVE > all real affected instances behind it), each instance is confirmed by the scanner against the actually-installed package version (a `Verified` flag separates real CVE matches from synthetic pending-update markers), carries its **OS context** (image OS from Trivy, host OS from `ServerSystemInfo`), and has an **age** ("open for N days") that survives restarts via a small persisted first-seen table.

## Files

| File | Purpose |
|---|---|
| `ICveMonitorService.cs` / `CveMonitorService.cs` | Background CVE monitor; also exposes a manual scan cycle the UI can trigger. Stamps host-OS context onto OS findings and records first-seen timestamps after each cycle. |
| `IOsCveScanner.cs` / `OsCveScanner.cs` | Scans a server's host OS packages for known CVEs. |
| `ITrivyScanner.cs` / `TrivyScanner.cs` | Scans a container image for known CVEs using [Trivy](https://github.com/aquasecurity/trivy); captures the image OS and the CVE published date. |
| `ICveFindingsStore.cs` / `CveFindingsStore.cs` | In-memory store of the latest CVE scan results per server/container, with summary helpers and `BuildGroups`, which **de-duplicates** every finding into one `CveGroup` per CVE-ID listing all real affected (server, container/OS, package) instances behind it. |
| `ICveAgeStore.cs` / `CveAgeStore.cs` | Persists (SQLite, `CveFirstSeen` table) when each vulnerability instance was first detected, so the "open for N days" age survives restarts. Recorded after each scan cycle; read when grouping. |

## Related

- Models: [`../../Models/Cve/`](../../Models/Cve/) (`CveFinding`, `CveGroup`/`CveAffected`, `CveFirstSeenEntity`)
- UI: [`../../Components/Pages/Cves.razor`](../../Components/Pages/Cves.razor)
- MCP tools: `list_cve_groups` (de-duplicated, recommended), `get_cve_summary`, `get_server_cves`, `get_container_cves`
