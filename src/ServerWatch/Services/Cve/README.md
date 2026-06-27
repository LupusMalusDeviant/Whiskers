# Services/Cve

Vulnerability scanning. A background monitor periodically scans both **host OS packages** and **container images** for known CVEs and stores the latest findings per server/container for the UI and MCP tools.

## Files

| File | Purpose |
|---|---|
| `ICveMonitorService.cs` / `CveMonitorService.cs` | Background CVE monitor; also exposes a manual scan cycle the UI can trigger. |
| `IOsCveScanner.cs` / `OsCveScanner.cs` | Scans a server's host OS packages for known CVEs. |
| `ITrivyScanner.cs` / `TrivyScanner.cs` | Scans a container image for known CVEs using [Trivy](https://github.com/aquasecurity/trivy). |
| `ICveFindingsStore.cs` / `CveFindingsStore.cs` | In-memory store of the latest CVE scan results per server/container, with summary helpers. |

## Related

- Models: [`../../Models/Cve/`](../../Models/Cve/)
- UI: [`../../Components/Pages/Cves.razor`](../../Components/Pages/Cves.razor)
- MCP tools: `get_cve_summary`, `get_server_cves`, `get_container_cves`
