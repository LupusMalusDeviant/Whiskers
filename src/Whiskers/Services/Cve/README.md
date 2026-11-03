# Services/Cve

Vulnerability scanning. A background monitor periodically scans both **host OS packages** and **container images** for known CVEs and stores the latest findings per server/container for the UI and MCP tools.

Findings are **de-duplicated per CVE-ID** for display (one CVE > all real affected instances behind it), each instance is confirmed by the scanner against the actually-installed package version (a `Verified` flag separates real CVE matches from synthetic pending-update markers), carries its **OS context** (image OS from Trivy, host OS from `ServerSystemInfo`), and has an **age** ("open for N days") that survives restarts via a small persisted first-seen table.

**Identity & failure handling (2026-07):** a finding's identity key is built from the container **name** (not its ID), so a container recreate (every image update changes the ID) keeps the persisted age and does not re-notify every CVE. A failed scan (Trivy timeout, transient apt error) returns an empty result with `Error` set; the monitor **keeps the previous good results** instead of overwriting them, avoiding a false "clean" state and a re-notification storm on the next successful scan, and backs off ~15 min (rather than a full interval) before retrying. An atomic scan gate stops a manual trigger and the background loop from running overlapping full scans. After each server's container scans, entries for containers that no longer exist are pruned (the OS entry is never pruned), and stale `CveFirstSeen` rows (gone **and** older than 30 days) are cleaned up so neither the store nor the age table grows unbounded across recreates. The apt scan commands force `LC_ALL=C.UTF-8` so non-English hosts report the same findings.

## Files

| File | Purpose |
|---|---|
| `ICveMonitorService.cs` / `CveMonitorService.cs` | Background CVE monitor; also exposes a manual scan cycle the UI can trigger. Stamps host-OS context onto OS findings and records first-seen timestamps after each cycle. R1-41: its interval is the shared `EffectiveInterval<CveMonitorSettings>` — a value out of range (1–720 h) is not used: at start it is clamped (with a warning), after a reload the last valid one stays — and `/metrics` (`whiskers_cve_stale_targets`) and the MCP `get_cve_summary` judge staleness by that same value, so a bad reload can neither overflow a `TimeSpan` nor move a threshold away from the cadence the loop keeps. |
| `IOsCveScanner.cs` / `OsCveScanner.cs` | Scans a server's host OS packages for known CVEs. R1-48: its `apt-get update` (package index) is a designed exemption of the monitoring cycle (PRODUCT_CONTRACT S-06), pinned in `MutationGatewayCoverageTests`. |
| `ITrivyScanner.cs` / `TrivyScanner.cs` | Scans a container image for known CVEs using [Trivy](https://github.com/aquasecurity/trivy); captures the image OS and the CVE published date. Requests a 16 MB output cap and refuses a truncated report outright — see **Large reports** below. R1-48: pulling the scanner image, the throw-away `--rm` container and the cache volume are a designed exemption (S-06); only the CVE monitor and the update-risk assessment (inside its `image.assess_update` decision) run it (pinned). |
| `ICveFindingsStore.cs` / `CveFindingsStore.cs` | In-memory store of the latest CVE scan results per server/container, with summary helpers and `BuildGroups`, which **de-duplicates** every finding into one `CveGroup` per CVE-ID listing all real affected (server, container/OS, package) instances behind it. |
| `ICveAgeStore.cs` / `CveAgeStore.cs` | Persists (SQLite, `CveFirstSeen` table) when each vulnerability instance was first detected, so the "open for N days" age survives restarts. Recorded after each scan cycle; read when grouping. |
| `NoopCveServices.cs` | Core no-op defaults (`NoopCveFindingsStore` / `NoopCveMonitorService` / `NoopCveAgeStore`) for when the **Cve module** is off — the findings store + monitor are read by the Core Dashboard/ContainerDetail/Settings pages, which then show no CVE data. Real services win by last-registration when on (RoadToSAP Phase 1). |

## The `whiskers_cve_*` series (2026-08-27)

`CveMetrics` turns the stored results into the numbers `/metrics` publishes. The one worth alerting on is
**`whiskers_cve_stale_targets`**.

A failed scan deliberately keeps the previous results rather than reporting a false all-clear. That is the
right trade, and it has a cost nobody had measured: a target whose scanner broke months ago looks exactly like
one scanned this morning — same findings, still plausible, simply never changing. The Authentik image sat like
that from July to late August. The only thing separating the two was the age of the data, and nothing watched
it.

| Series | What it says |
|---|---|
| `whiskers_cve_stale_targets{server}` | Targets whose data is older than **two** scan intervals. One missed cycle retries in ~15 min and repairs itself; two consecutive misses is a scanner that is not coming back. Alert on this above zero. |
| `whiskers_cve_data_age_seconds{server}` | Age of the least recently scanned target. A **maximum**, not a mean — nine fresh targets and one abandoned one would average to something reassuring and wrong. |
| `whiskers_cve_findings{server,severity}` | Open findings per severity. Severities with nothing in them are absent, not zero. |
| `whiskers_cve_distinct_ids` | Distinct CVE identifiers fleet-wide. On the real fleet: 550 behind 4658 findings — the findings count is the size of the display, this is the size of the problem. |

**No container labels, deliberately.** The endpoint bounds cardinality by server, never by container: a
container label across a large fleet turns a few dozen series into thousands and fills the time-series
database — a monitoring outage caused by monitoring. So the endpoint says *that* something is stale, and
`get_cve_summary` (MCP) names *which* target, worst first, where one question is asked at a time and
cardinality costs nothing. Both halves are needed: a count with no name leaves an operator nowhere to go.

## Der containerd-Image-Store bricht Trivys Docker-Export (2026-08-28)

Trivy exportiert das Image standardmäßig aus dem Docker-Daemon und liest das entstehende Tar. Auf einem Host,
dessen Docker den **containerd-Image-Store** benutzt (`io.containerd.snapshotter.v1` statt des klassischen
`overlay2`), fehlen in diesem Export Schichten, und Trivy bricht ab mit
`file blobs/sha256/… not found in tar`.

**Es ist nichts beschädigt** — und alles an diesem Fehler lädt zur falschen Diagnose ein. Vier Hypothesen
wurden geprüft und widerlegt, bevor der Speichertreiber als einziger Unterschied übrig blieb:

| Vermutung | Widerlegt durch |
|---|---|
| `NetworkMode=host` | zwei Betroffene laufen im Bridge-Netz, `serverwatch` selbst host-networked und gesund |
| beschädigte lokale Schichten | Image entfernt und frisch gezogen — Fehler unverändert |
| Docker-Version | alle Server auf 29.5.x, identischer Image-Digest, einer scannt, einer nicht |
| Trivy-Cache | mit komplett frischem Cache derselbe Fehler |

`TrivyScanner` versucht deshalb bei **genau dieser** Fehlersignatur einen zweiten Lauf mit
`--image-src remote`, liest das Image also aus der Registry statt aus dem Daemon. Der lokale Weg bleibt die
Vorgabe: Er ist kostenlos und erfasst lokal gebaute Images, die in keiner Registry liegen. Die Wiederholung ist
bewusst **eng** an die Signatur gebunden — ein Blanko-Retry würde einen nicht erreichbaren Host beantworten,
indem er still aus dem Internet zieht, und nur der zweite Fehlschlag wäre je zu sehen.

Bekannte Grenze: private Images ohne hinterlegte Zugangsdaten im Trivy-Container scheitern auch im zweiten
Lauf — dann greift der Eintrag aus dem Abschnitt darüber und das Ziel ist wenigstens sichtbar fehlerhaft
statt abwesend.

## Ein nie erfolgreich gescanntes Ziel ist unsichtbar, nicht leer (2026-08-28)

Ein fehlgeschlagener Scan behält absichtlich die vorherigen Ergebnisse. Für ein Ziel, das **noch nie**
erfolgreich gescannt wurde, gibt es aber nichts zu behalten — also wurde gar nichts gespeichert, und das Ziel
fehlte in jeder Liste. Ein fehlendes Ziel liest sich überall wie eines ohne Befunde.

Im Feld gefunden: Auf infomaniak liefen zehn Container, im Bestand standen acht Ziele. `ghostunnel` und
`fenrir-sentinel` fehlten, weil ihre lokalen Image-Schichten beschädigt sind — Trivy scheitert dort mit
`file blobs/sha256/… not found in tar`, dasselbe Image scannt auf drei anderen Servern fehlerfrei. Keiner der
beiden war dort je erfolgreich gescannt worden, also gab es keinen Vorgänger, also wurde nichts gespeichert.
**Die Veraltungsmetrik kann das prinzipiell nicht sehen: Was nicht da ist, kann nicht altern.**

`CveScanOutcome.Decide` trennt die zwei Hälften der Regel, und beide sind nötig:

| Lage | Entscheidung | Warum |
|---|---|---|
| Scan erfolgreich | `Store` | auch ein Ergebnis ohne Befunde — sonst ist ein sauberes Image von einem ungescannten nicht zu unterscheiden |
| Fehlgeschlagen, Vorgänger vorhanden | `KeepPrevious` | echte Befunde durch ein leeres Ergebnis zu ersetzen meldet Entwarnung und beim nächsten Erfolg jede CVE neu |
| Fehlgeschlagen, **kein** Vorgänger | `Store` | sonst ist das Ziel abwesend, und abwesend sieht aus wie sauber |

Ein gespeicherter Fehlschlag wird **nie** als neuer Befund gemeldet (`ShouldNotify`) — das würde jemanden
wegen eines Scannerproblems anpiepsen. Sichtbar wird er stattdessen über `whiskers_cve_scan_failures{server}`
und namentlich über `get_cve_summary`, inklusive Grund und der Zahl der noch bekannten Befunde: Ein Fehlschlag
über alten, echten Daten ist eine andere Lage als einer über gar nichts.

## Results of a server that was removed (2026-08-27)

`PruneServer` only ever runs for servers that still exist, so the results of a server **deleted from the
fleet** were never looked at again. Found in the field: a server removed in July was still reporting 419
vulnerabilities six weeks later, listed as current findings of a machine that no longer exists — and because
their identity keys still counted as "live", the `CveFirstSeen` ages behind them could never be pruned either.
The stale age rows were the symptom; the phantom results were the cause.

`PruneRemovedServers(configuredServerIds)` runs once per cycle and drops every result whose server is not
configured any more, the OS entry included (it is protected in `PruneServer` because a container listing says
nothing about the host — a server that is gone has no host left to protect). Two rules it will not bend:

- The caller passes **every configured server, enabled or not**. Switching a server off is not deleting it; a
  fortnight of maintenance must not cost its findings or their ages.
- An **empty** set removes nothing. "No servers configured" and "the server list could not be read" arrive
  looking identical, and acting on the second would wipe the whole fleet's findings and re-notify all of them
  on the next scan. Doing nothing is recoverable; that is not.

## Large reports (2026-08-27)

Trivy's JSON for a large image runs to several megabytes — the Authentik server image measures 3.4 MB. The host
executor capped command output at 1 MB, so the document arrived cut in half with a truncation marker appended,
and the scan failed every cycle with `'0xE2' is an invalid start of a value` — the marker's first byte. The
error named the parser, the cause was the limit, and that image silently kept its stale findings for months.

The scanner now asks for a 16 MB cap (about five times the largest report observed) and checks
`CommandResult.OutputTruncated` **before** parsing: a report that was cut off is reported as cut off and
discarded, never parsed into a partial verdict — a half-read report would look exactly like a clean bill of
health for everything the cut removed. Output that was never truncated and still cannot be read keeps failing
loudly as a parse error, because that is a bug to find rather than a limit to raise.

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
