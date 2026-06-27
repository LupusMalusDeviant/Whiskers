# Services/Metrics

Metric collection, storage and querying (CPU, RAM, disk, container stats). The design **decouples the collector from where metrics come from** so a server can use live Docker stats *or* a push/scrape telemetry pipeline (VictoriaMetrics) without the collector caring — see [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md).

A background collector samples on a schedule and writes time-series to SQLite ([`../Persistence/`](../Persistence/)); the query service reads it back for charts and reports.

## Files

| File | Purpose |
|---|---|
| `IMetricsSource.cs` | The abstraction the collector reads through — hides SSH/Docker vs. scrape behind one interface. |
| `IDockerMetricsSource.cs` / `DockerMetricsSource.cs` | Reads live metrics straight from the Docker engine (default source). |
| `IPrometheusMetricsSource.cs` / `PrometheusMetricsSource.cs` | Reads server metrics from a Prometheus/VictoriaMetrics endpoint (push/scrape source). |
| `MetricsSourceDispatcher.cs` | Routes each read to the source configured for that server (`ServerConfig.MetricsSource`); Docker is the default and fallback, Prometheus is opt-in. |
| `MetricsCollectorService.cs` | Background service that samples all servers on a schedule and persists the time-series. |
| `IMetricsQueryService.cs` / `MetricsQueryService.cs` | Queries historical container/server metrics from the time-series store. |

## Related

- Storage: [`../Persistence/`](../Persistence/) (SQLite)
- Telemetry deploy templates: [`../../../deploy/telemetry/`](../../../deploy/telemetry/)
- MCP tools: `get_server_metrics`, `get_container_metrics`
