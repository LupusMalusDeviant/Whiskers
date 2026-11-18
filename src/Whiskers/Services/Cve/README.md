# Services/Cve

Vulnerability scanning. A background monitor periodically scans both **host OS packages** and **container images** for known CVEs and stores the latest findings per server/container for the UI and MCP tools.

Findings are **de-duplicated per CVE-ID** for display (one CVE > all real affected instances behind it), each instance is confirmed by the scanner against the actually-installed package version (a `Verified` flag separates real CVE matches from synthetic pending-update markers), carries its **OS context** (image OS from Trivy, host OS from `ServerSystemInfo`), and has an **age** ("open for N days") that survives restarts via a small persisted first-seen table.

**Identity & failure handling (2026-07):** a finding's identity key is built from the container **name** (not its ID), so a container recreate (every image update changes the ID) keeps the persisted age and does not re-notify every CVE. A failed scan (Trivy timeout, transient apt error) returns an empty result with `Error` set; the monitor **keeps the previous good results** instead of overwriting them, avoiding a false "clean" state and a re-notification storm on the next successful scan, and backs off ~15 min (rather than a full interval) before retrying. An atomic scan gate stops a manual trigger and the background loop from running overlapping full scans. After each server's container scans, entries for containers that no longer exist are pruned (the OS entry is never pruned), and stale `CveFirstSeen` rows (gone **and** older than 30 days) are cleaned up so neither the store nor the age table grows unbounded across recreates. The apt scan commands force `LC_ALL=C.UTF-8` so non-English hosts report the same findings. A host without apt is **not scannable**, never clean: the list command echoes a marker only when `apt` exists, and without it the result carries an `Error` (review 1.0.0). A `cve_finding` carries `ServerId`/`ServerName` and, for a container, the container's own name in `ContainerName` — so the per-container mute applies and the alert history files it under its server (review 1.0.0).

## Files

| File | Purpose |
|---|---|
| `ICveMonitorService.cs` / `CveMonitorService.cs` | Background CVE monitor; also exposes a manual scan cycle the UI can trigger. Stamps host-OS context onto OS findings and records first-seen timestamps after each cycle. R1-41: its interval is the shared `EffectiveInterval<CveMonitorSettings>` — a value out of range (1–720 h) is not used: at start it is clamped (with a warning), after a reload the last valid one stays — and `/metrics` (`whiskers_cve_stale_targets`) and the MCP `get_cve_summary` judge staleness by that same value, so a bad reload can neither overflow a `TimeSpan` nor move a threshold away from the cadence the loop keeps. Containers come from the detailed fleet listing: a server that did not answer it (failed, timed out, not asked) is neither scanned nor pruned, so its stored findings and their ages survive, and its cycle is recorded as no success (Fable review H4). |
| `IOsCveScanner.cs` / `OsCveScanner.cs` | Scans a server's host OS packages for known CVEs. R1-48: its `apt-get update` (package index) is a designed exemption of the monitoring cycle (PRODUCT_CONTRACT S-06), pinned in `MutationGatewayCoverageTests`. |
| `ITrivyScanner.cs` / `TrivyScanner.cs` | Scans a container image for known CVEs using [Trivy](https://github.com/aquasecurity/trivy); captures the image OS and the CVE published date. Requests a 16 MB output cap and refuses a truncated report outright — see **Large reports** below. R1-48: pulling the scanner image, the throw-away `--rm` container and the cache volume are a designed exemption (S-06); only the CVE monitor and the update-risk assessment (inside its `image.assess_update` decision) run it (pinned). |
| `ICveFindingsStore.cs` / `CveFindingsStore.cs` | In-memory store of the latest CVE scan results per server/container, with summary helpers and `BuildGroups`, which **de-duplicates** every finding into one `CveGroup` per CVE-ID listing all real affected (server, container/OS, package) instances behind it. |
| `CveAgeStore.cs` (with `ICveAgeStore`) | Persists (SQLite, `CveFirstSeen` table) when each vulnerability instance was first detected, so the "open for N days" age survives restarts. Recorded after each scan cycle; read when grouping. |
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

## The containerd image store breaks Trivy's Docker export (2026-08-28)

By default Trivy exports the image from the Docker daemon and reads the resulting tar. On a host whose Docker uses
the **containerd image store** (`io.containerd.snapshotter.v1` instead of the classic `overlay2`), layers are missing
from that export, and Trivy stops with `file blobs/sha256/… not found in tar`.

**Nothing is damaged**, and everything about this error invites the wrong diagnosis. Four hypotheses were tested and
refuted before the storage driver remained as the only difference:

| Hypothesis | Refuted by |
|---|---|
| `NetworkMode=host` | two affected containers run on a bridge network, while `serverwatch` itself is host-networked and scans fine |
| damaged local layers | image removed and pulled again: same error |
| Docker version | all servers on 29.5.x, identical image digest, one scans and one does not |
| Trivy cache | the same error with a completely fresh cache |

So on **exactly this** error signature `TrivyScanner` makes a second run with `--image-src remote`, reading the image
from the registry instead of the daemon. The local path stays the default: it is free and covers locally built images
that are in no registry. The retry is deliberately bound **tightly** to the signature: a blanket retry would answer an
unreachable host by silently pulling from the internet, and only the second failure would ever be visible.

Known limit: private images without credentials available to the Trivy container fail in the second run too; then
the rule of the next section applies, and the target is at least visibly failing instead of absent.

## A target never scanned successfully is invisible, not empty (2026-08-28)

A failed scan deliberately keeps the previous results. For a target that was **never** scanned successfully there is
nothing to keep, so nothing was stored and the target was missing from every list. A missing target reads everywhere
like one without findings.

Found in the field: one server ran ten containers, and the inventory had eight targets. Two were missing (one of
them `ghostunnel`) because their local image layers were damaged: Trivy failed there with
`file blobs/sha256/… not found in tar`, while the same image scanned cleanly on three other servers. Neither had ever
been scanned successfully on that server, so there was no previous result and nothing was stored.
**The staleness metric cannot see this in principle: what is not there cannot age.**

`CveScanOutcome.Decide` separates the two halves of the rule, and both are needed:

| Situation | Decision | Why |
|---|---|---|
| Scan succeeded | `Store` | also a result without findings; otherwise a clean image cannot be told from an unscanned one |
| Failed, previous result **with findings** | `KeepPrevious` | replacing real findings with an empty result reports an all-clear, and every CVE as new at the next success |
| Failed, **no** previous result | `Store` | otherwise the target is absent, and absent looks clean |
| Failed, previous result **without** findings (clean, or failed itself) | `Store` | review 1.0.0: an old "clean" would otherwise stay, although the target can no longer be checked (host without apt) |

A stored failure is **never** reported as a new finding (`ShouldNotify`); that would page someone over a scanner
problem. It shows instead in `whiskers_cve_scan_failures{server}` and by name in `get_cve_summary`, with the reason
and the number of findings still known: a failure over old, real data is a different situation from one over
nothing.

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
