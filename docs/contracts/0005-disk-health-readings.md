# Contract 0005 — Optional disk-health readings (R1-58)

Status: **Accepted** · 5 November 2026 by the maintainer, including the decisions listed at the end (all five as
recommended). To be implemented in R1-58.
R1-58 is part of 1.0 (maintainer decision of 5 November 2026; `WORK_PACKAGES.md` still says "Post-1.0" and is corrected
with this package).

## Purpose

Whiskers shows whether each host's disks and arrays are healthy: SMART status, Linux software RAID (md), ZFS pools.
It reads them **only** from the host's existing metrics source (Prometheus/VictoriaMetrics `MetricsEndpoint`). It never
runs `smartctl` or anything else on the host and gets no write path there. A reading Whiskers cannot vouch for is never
shown as healthy (WORK_PACKAGES R1-58; R1-11, R1-22, R1-56).

## Current state (verified at `0611f336`)

- **The only metrics reader is `PrometheusMetricsSource`** (`IPrometheusMetricsSource.GetServerSystemInfoAsync(serverId)`).
  - Its private `QueryScalarAsync` reads only the first series' value. It ignores `status`, `resultType` and timestamps,
    and has no cancellation token.
  - Selector: `{server="<ServerConfig.Id>"}`.
  - There are no recorded `/api/v1/query` fixtures and no tests for it.
- **Configuration:** `ServerConfig.MetricsSource` (`Docker` by default) and `ServerConfig.MetricsEndpoint`, which only
  onboarding sets. A Docker-source server has no endpoint.
- **node_exporter compose** (`deploy/telemetry/host/docker-compose.yml`, copied verbatim into
  `OnboardingCommands.NodeExporterCompose`): `--path.rootfs=/host`, listening on the Tailscale IP.
  - The **textfile collector is not enabled** (no `--collector.textfile.directory`).
  - The built-in `mdadm` and `zfs` collectors are on by default and read `/host/proc`.
- **Alerting (R1-56):** `BreachTracker` is numeric with elapsed-time durations. A kind must be in
  `NotificationEventTypes.RecoverableKinds` and classified as suppressible or not.
- **Gated settings:** `IAppSettingsStore.SaveSectionAsync` is behind `config.save` (Admin).
  `MetricRetentionSettingsTests` is the test model (Viewer/Operator/demoted Admin refused, kill switch, audit failure,
  failed save leaves the value unchanged).
- **UI:** there is no host-detail page. The host panel is the expandable server group on `Dashboard.razor`, and every
  page requires sign-in (Viewer = read).

## Boundaries (fixed)

- No new agent, no command on the host, no write path on the host. `IHostCommandExecutor` is not used.
- The host-side job that writes SMART data (a cron or systemd timer running a script) is the **operator's**. Whiskers
  documents it and does not install it.
- Whiskers reads `MetricsEndpoint` (`/api/v1/query`), never exporter text directly.
- Disabled by default.

## Sources and metrics

| Source | Series (node_exporter names) | Collection-age evidence |
|---|---|---|
| SMART, through the textfile collector (`smartmon.sh` from prometheus-community's textfile-collector-scripts, or the documented equivalent) | `smartmon_device_smart_healthy{disk}` (1/0), `smartmon_device_smart_available{disk}`, `smartmon_device_smart_enabled{disk}` | `smartmon_smartctl_run{disk}` (unix time of the run) **and** `node_textfile_mtime_seconds{file="smartmon.prom"}` |
| md RAID (built-in `mdadm` collector) | `node_md_state{device,state}`, `node_md_disks{device,state="active\|failed\|spare"}`, `node_md_disks_required{device}` | `node_scrape_collector_success{collector="mdadm"}` = 1 in the same query time; the series exists only while it is scraped |
| ZFS (built-in `zfs` collector) | `node_zfs_zpool_state{zpool,state}` (online, degraded, faulted, offline, removed, unavail, suspended) | `node_scrape_collector_success{collector="zfs"}` = 1 |

The query sample timestamp never counts as freshness. A stale textfile is still scraped with a fresh sample time.
Freshness comes **only** from `smartmon_smartctl_run` / `node_textfile_mtime_seconds`, with a limit of **2 hours**
(decision 2).

## Types and interfaces

```csharp
public enum DiskHealthState { Healthy, DegradedOrFailing, Unknown, Unsupported }

public sealed record DiskHealthItem(
    string Kind,                 // "smart" | "md" | "zfs"
    string Name,                 // disk / md device / pool, from the series label, display-escaped
    DiskHealthState State,
    string Reason,               // why — e.g. "SMART overall-health failed", "md0: 1 of 2 disks failed", "smartmon.prom 3 h old"
    DateTime? CollectedUtc);     // the collection-age evidence used, if any

public sealed record DiskHealthReading(
    string ServerId,
    DiskHealthState Overall,     // worst item; Unsupported only when the host never had a reading
    IReadOnlyList<DiskHealthItem> Items,
    DateTime ReadUtc);

public interface IPrometheusQueryClient       // new: generic, typed, cancellable instant query
{
    Task<PromQueryResult> QueryAsync(string endpoint, string promql, CancellationToken ct); // status + vector samples with labels
}

public static class DiskHealthParser          // pure; fixture-tested
{
    public static DiskHealthReading Parse(string serverId, DiskHealthQueries results, DateTime nowUtc,
                                          DiskHealthReading? previous, TimeSpan freshness);
}

public interface IDiskHealthService           // read side for UI and alerts
{
    DiskHealthReading? Latest(string serverId);
    IReadOnlyDictionary<string, DiskHealthReading> All();
}
```

- `PrometheusMetricsSource` is **not** rewritten. The new client is added beside it, with a stub-handler test helper.
- `DiskHealthMonitor : FleetBackgroundService` runs every 5 minutes, only while the setting is on. It covers every
  enabled server with a `MetricsEndpoint`.
  - Six fixed queries per host, each selected on `server="<id>"`.
  - The 10 s HTTP timeout and the caller's cancellation are honoured.
  - Failures are isolated per host and per cycle (R1-41).

### State rules (the core of the tests)

- **Healthy:** every present item reads good and every piece of collection-age evidence is within the freshness limit.
- **DegradedOrFailing:** any of the following.
  - SMART healthy = 0.
  - An md array with failed disks, or with fewer active disks than required.
  - A ZFS pool whose state is not `online`.
- **Unknown:** the host had a reading before, and now one of these holds.
  - The query fails or is malformed.
  - Series are missing or the result is partial (some sources present, an earlier one gone).
  - Collection-age evidence is missing or older than the freshness limit. A fixture with fresh sample timestamps but an
    expired textfile reads Unknown.
- **Unsupported:** the host never had a reading. Examples: a Docker-source server without `MetricsEndpoint`, or a host
  with none of the three sources.
- **Neither Unknown nor Unsupported is ever Healthy.** Neither clears an existing DegradedOrFailing alert: the last bad
  state stays until a fresh, good reading.

### Setting (R1-11, R1-22)

- Section `DiskHealth` with `{ "Enabled": false }`, saved with `config.save` (Admin) through `IUiMutationRunner`.
- Viewer, Operator and a demoted Admin are refused; so are a save under the kill switch and an audit failure.
- A failed save leaves the setting unchanged.

### Alerting (R1-56)

New recoverable kinds, all **unsuppressible**:

- `disk_failing` (SMART).
- `array_degraded` (md/ZFS).
- `disk_health_unknown`.

They are fed to `BreachTracker` as 1/0 against threshold 1.

- **Open:** after the state has held for 10 minutes (two cycles).
- **Clear:** after a good reading has held for 10 minutes.
- `disk_health_unknown` fires only on a transition **out of** Healthy or DegradedOrFailing.
- Unsupported never alerts.

### UI

- **Dashboard:** each server's expanded panel gets a "Disks" block with the overall state and one line per item
  (state, name, reason, age). Any signed-in role can read it.
- **Settings → Monitoring:** a switch "Read disk health (SMART, RAID, ZFS)" for Admins. Its help text links the
  operations guide.
- When the feature is off, nothing is queried and nothing is shown.

### node_exporter and documentation

- **Deploy compose and onboarding template** (decision 3):
  - Add `--collector.textfile.directory=/host/var/lib/node_exporter/textfile_collector`.
  - The directory is reached through the existing read-only `/:/host` mount, so no new mount is needed.
- **`docs/operations/disk-health.md`:**
  - Installing `smartmontools`, plus the documented shell script (not Python) that writes `smartmon.prom` atomically
    every 15 minutes through a systemd timer.
  - The freshness limit.
  - What each state means.
  - That md and ZFS need nothing extra.

## Tests (acceptance)

| Test | Proves |
|---|---|
| `DiskHealthReadingTests` (recorded `/api/v1/query` JSON fixtures) | Healthy; SMART failed; md with a failed disk and with fewer active disks than required; ZFS degraded/faulted; partial result; malformed JSON; `status: error`; missing series → Unknown after a reading, Unsupported before any; fresh sample time + expired `smartmon_smartctl_run`/mtime → Unknown; Unknown/Unsupported never Healthy and never clear DegradedOrFailing |
| `DiskHealthSettingTests` | off by default; save refused for Viewer, Operator and a demoted Admin, by the kill switch and by audit failure; failed save leaves it off |
| `DiskHealthAlertTests` | failing disk, degraded array and a reading turning Unknown raise alerts after the duration; Unknown alerts only on a transition out of Healthy or DegradedOrFailing; Unsupported never alerts |
| `DiskHealthMonitorTests` | off → no query; per-host failure isolated; cancellation honoured |
| Panel (bUnit) | Viewer sees the block; no Admin-only control appears for a Viewer |
| Onboarding / compose | node_exporter template and deploy compose carry the textfile flag |

## Plan triage

- **[Claude-prüfbar]:** every test above. They run on fixtures and a stub HTTP handler.
- **[braucht Nutzer/Hardware]:**
  - A host with real SMART disks and the timer installed.
  - One md array and one ZFS pool, if present in the fleet.
  - Checking that the Dashboard shows them.

## Decisions for the maintainer

1. **SMART source:** the textfile collector with the community `smartmon.sh` format, documented as a shell script.
   `smartctl_exporter` is not supported in 1.0. *Recommended:* yes. A single, well-known metric set with a real
   collection timestamp.
2. **Freshness limit:** 2 hours for SMART textfile data, with the script running every 15 minutes. *Recommended:* yes.
3. **Enable the textfile collector in our node_exporter compose and onboarding template:** read-only, through the
   existing `/host` mount. *Recommended:* yes. Without it, onboarded hosts cannot deliver SMART at all.
4. **Alert durations:** 10 minutes to open and to clear; all three kinds unsuppressible by quiet hours.
   *Recommended:* yes. A dying disk should not wait for the morning.
5. **Placement:** a Dashboard server panel block plus a settings switch, with no new page. *Recommended:* yes.
