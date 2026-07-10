# Services/Cve

Vulnerability scanning. A background monitor periodically scans both **host OS packages** and **container images** for known CVEs and stores the latest findings per server/container for the UI and MCP tools.

Findings are **de-duplicated per CVE-ID** for display (one CVE > all real affected instances behind it), each instance is confirmed by the scanner against the actually-installed package version (a `Verified` flag separates real CVE matches from synthetic pending-update markers), carries its **OS context** (image OS from Trivy, host OS from `ServerSystemInfo`), and has an **age** ("open for N days") that survives restarts via a small persisted first-seen table.

**Identity & failure handling (2026-07):** a finding's identity key is built from the container **name** (not its ID), so a container recreate (every image update changes the ID) keeps the persisted age and does not re-notify every CVE. A failed scan (Trivy timeout, transient apt error) returns an empty result with `Error` set; the monitor **keeps the previous good results** instead of overwriting them, avoiding a false "clean" state and a re-notification storm on the next successful scan, and backs off ~15 min (rather than a full interval) before retrying. An atomic scan gate stops a manual trigger and the background loop from running overlapping full scans. After each server's container scans, entries for containers that no longer exist are pruned (the OS entry is never pruned), and stale `CveFirstSeen` rows (gone **and** older than 30 days) are cleaned up so neither the store nor the age table grows unbounded across recreates. The apt scan commands force `LC_ALL=C.UTF-8` so non-English hosts report the same findings.

## Files

| File | Purpose |
|---|---|
| `ICveMonitorService.cs` / `CveMonitorService.cs` | Background CVE monitor; also exposes a manual scan cycle the UI can trigger. Stamps host-OS context onto OS findings and records first-seen timestamps after each cycle. |
| `IOsCveScanner.cs` / `OsCveScanner.cs` | Scans a server's host OS packages for known CVEs. |
| `ITrivyScanner.cs` / `TrivyScanner.cs` | Scans a container image for known CVEs using [Trivy](https://github.com/aquasecurity/trivy); captures the image OS and the CVE published date. |
| `ICveFindingsStore.cs` / `CveFindingsStore.cs` | In-memory store of the latest CVE scan results per server/container, with summary helpers and `BuildGroups`, which **de-duplicates** every finding into one `CveGroup` per CVE-ID listing all real affected (server, container/OS, package) instances behind it. |
| `ICveAgeStore.cs` / `CveAgeStore.cs` | Persists (SQLite, `CveFirstSeen` table) when each vulnerability instance was first detected, so the "open for N days" age survives restarts. Recorded after each scan cycle; read when grouping. |
| `NoopCveServices.cs` | Core no-op defaults (`NoopCveFindingsStore` / `NoopCveMonitorService` / `NoopCveAgeStore`) for when the **Cve module** is off — the findings store + monitor are read by the Core Dashboard/ContainerDetail/Settings pages, which then show no CVE data. Real services win by last-registration when on (RoadToSAP Phase 1). |

## Wiring

This is the opt-in **Cve module** ([`../../Modules/Cve/`](../../Modules/Cve/), toggle `Features:cve:Enabled`):
its `ConfigureServices` registers the stores, scanners and hosted monitor, and it owns the `cves` nav entry and
the dedicated `CveTools`. Because the Core Dashboard/ContainerDetail/Settings pages consume `ICveFindingsStore`
(and Settings consumes `ICveMonitorService`), Core keeps the `NoopCveServices` defaults above for when the
module is off. (The C8 service-locator removal in `CveMonitorService` is a deferred, separate follow-up.)

## Related

- Models: [`../../Models/Cve/`](../../Models/Cve/) (`CveFinding`, `CveGroup`/`CveAffected`, `CveFirstSeenEntity`)
- UI: [`../../Components/Pages/Cves.razor`](../../Components/Pages/Cves.razor)
- MCP tools: `list_cve_groups` (de-duplicated, recommended), `get_cve_summary`, `get_server_cves`, `get_container_cves`
