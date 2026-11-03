# Services/Metrics

Metric collection, storage and querying (CPU, RAM, disk, container stats). The design **decouples the collector from where metrics come from** so a server can use live Docker stats *or* a push/scrape telemetry pipeline (VictoriaMetrics) without the collector caring, see [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md).

A background collector samples on a schedule and writes time-series to SQLite ([`../Persistence/`](../Persistence/)); the query service reads it back for charts and reports.

## Files

| File | Purpose |
|---|---|
| `IMetricsSource.cs` | The abstraction the collector reads through, hides SSH/Docker vs. scrape behind one interface. |
| `IDockerMetricsSource.cs` / `DockerMetricsSource.cs` | Reads live metrics straight from the Docker engine (default source). |
| `IPrometheusMetricsSource.cs` / `PrometheusMetricsSource.cs` | Reads server metrics from a Prometheus/VictoriaMetrics endpoint (push/scrape source). |
| `MetricsSourceDispatcher.cs` | Routes each read to the source configured for that server (`ServerConfig.MetricsSource`); Docker is the default and fallback, Prometheus is opt-in. |
| `MetricsCollectorService.cs` | Background service that samples all servers on a schedule and persists the time-series (incl. host root-filesystem usage via `df`). Also emits `high_cpu` / `high_memory` / `high_disk` (server-level) / `metric_anomaly` events (thresholds from `MetricAlertSettings`) which drive [AI triggers](../Agent/Triggers/). R1-41: its cadence is the shared `EffectiveInterval<MetricsSettings>` (a reloaded value outside 5–86400 s keeps the last valid one), the same value its self-metrics report as the loop interval. |
| `IMetricsQueryService.cs` / `MetricsQueryService.cs` | Queries historical container/server metrics. R1-55: a `MetricPoint` carries the sample-weighted average (`Value`), the true maximum (`Max`) and its sample count; the range picks a tier (raw for 1 h/6 h, 10 min for 24 h, 1 h for 7 d/30 d — the coarsest whose window still gives 100 points) and is stitched at window boundaries — coarser tiers for the older part, finer tiers and the raw tail for the part newer than the tier's watermark — then thinned to 100 points keeping each point's maximum. `MetricSummary` gives the weighted average and peak of a whole history. |
| `MetricTiers.cs` | R1-55: the fixed tiers (10 min, 1 h, 8 h), window alignment and the watermark ids. |
| `IMetricRollupService.cs` / `MetricRollupService.cs` | R1-55: rolls closed windows up into the tiers (`ContainerMetricRollups`, `ServerMetricRollups`: average, maximum and sample count per series), one transaction per batch together with the tier's watermark (`MetricRollupStates`), so a crash neither loses nor double-counts a window; and applies each tier's retention — never deleting what the next coarser tier has not committed — plus the alert history's own retention. Run by the collector after its samples are saved. |
| `MetricSeriesKinds.cs` | R1-55: the container and server series over their raw and roll-up tables, shared by roll-up, retention and query. |
| `IMetricRetentionService.cs` / `MetricRetentionService.cs` | R1-55: the retention of each tier and of the alert history (`MetricRetention`, keys in the `Metrics` section). A key set by the environment or `appsettings.json` wins and is read-only; the others come from `app-settings.json` (the Settings page's gated, audited `config.save`) or the default. A value that does not parse never throws. |
| `MetricPeriods.cs` | The history ranges of the charts and MCP tools: `1h`, `6h`, `24h`, `7d`, `30d`. |

**Collector cycle (R1-55).** After each cycle's samples are saved, the collector rolls up the windows that closed (every 10 minutes; every cycle while the upgrade's first pass catches up) and, at most hourly, applies retention — in a step of its own, on the retention the cycle read, so neither a failed collection nor an unreadable `app-settings.json` holds retention up (R1-41 leftovers: the prune no longer re-reads the section, and a first read that fails starts on the defaults). The daily audit retention (R1-23) runs in the same step; R1-55 was built on top of it.

## Related

- Storage: [`../Persistence/`](../Persistence/) (SQLite or PostgreSQL); the roll-up tables are copied by `--migrate-to-postgres`
- Settings panel: [`../../Components/Shared/MetricRetentionPanel.razor`](../../Components/Shared/MetricRetentionPanel.razor)
- Telemetry deploy templates: [`../../../deploy/telemetry/`](../../../deploy/telemetry/)
- MCP tools: `get_server_metrics`, `get_container_metrics`
