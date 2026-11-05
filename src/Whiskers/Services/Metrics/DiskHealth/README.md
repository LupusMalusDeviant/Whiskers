# Services/Metrics/DiskHealth

R1-58 (contract [`docs/contracts/0005-disk-health-readings.md`](../../../../../docs/contracts/0005-disk-health-readings.md)):
optional disk-health readings — SMART, Linux software RAID (md) and ZFS pools — read **only** from each host's existing
metrics endpoint (`ServerConfig.MetricsEndpoint`, Prometheus/VictoriaMetrics `/api/v1/query`). Whiskers runs nothing on
the host and has no write path there; the host-side SMART job is the operator's
([`docs/operations/disk-health.md`](../../../../../docs/operations/disk-health.md)). Off by default.

## Files

| File | Purpose |
|---|---|
| `DiskHealthModels.cs` | `DiskHealthState` (Healthy, DegradedOrFailing, Unknown, Unsupported), `DiskHealthItem` (kind, display-escaped name, state, reason, collection-age evidence), `DiskHealthReading` (overall = worst item; Unsupported only when the host never had a reading), `DiskHealthKinds`, and `DiskHealthQueries` — the six fixed instant queries per host (`For(serverId)`, each selected on `server="<id>"`) and their results. |
| `DiskHealthParser.cs` | Pure, fixture-tested (`DiskHealthReadingTests`): the six results → a reading. Freshness comes only from collection-age evidence — SMART: `smartmon_smartctl_run` **and** `node_textfile_mtime_seconds` of `smartmon.prom` within 2 hours (`DefaultFreshness`); md/ZFS: `node_scrape_collector_success` = 1 at query time — never from the sample time. A value that reads bad is DegradedOrFailing even with old evidence; anything not clearly good with fresh evidence is Unknown; an item of the last reading that is now missing (query failed, series gone) is carried as Unknown; a failed source query with no line of its own adds a `(query failed)` line, so a host with a failed query never reads Healthy. No items: Unsupported before any reading, Unknown after one. Label values are display-escaped (`DisplayName`: no control or bidi characters, at most 64 characters). |
| `DiskHealthAlerts.cs` | The three alerts (`disk_failing`, `array_degraded`, `disk_health_unknown`) on R1-56's `BreachTracker` as 1/0 against threshold 1: raised after 10 minutes, cleared after 10 minutes of good readings, no gap reset (a pause is no good reading). Nothing is fed for a group that is neither clearly bad nor clearly good — Unknown and Unsupported never clear an open alert. `disk_health_unknown` is armed only by a transition out of Healthy or DegradedOrFailing. All three are recoverable and never held by quiet hours (`NotificationEventTypes`). |
| `DiskHealthService.cs` | `IDiskHealthService` — the read side (`Latest(serverId)`, `All()`) for the dashboard; the monitor publishes into it. |
| `DiskHealthSettingsService.cs` | `DiskHealthSettings` (section `DiskHealth`, `{ "Enabled": false }`) and `IDiskHealthSettingsService` — the setting and its one write path, `SaveAsync`, a `config.save` (Admin) run through `IUiMutationRunner` (R1-11, audited R1-22); published only after the file is written. A section that does not bind keeps the last value (off at start). |
| `DiskHealthMonitor.cs` | `FleetBackgroundService`, every 5 minutes, only while the setting is on: every enabled server with a `MetricsEndpoint` gets its six queries (side by side, 10 s each through `IPrometheusQueryClient`, the stop honoured); one without is recorded as Unsupported, never queried. Failures are isolated per host and cycle (R1-41). Off: nothing is queried or shown, the readings are dropped and open alerts closed unconfirmed; a server no longer configured is closed and dropped the same way. |
| `DiskHealthStateStore.cs` | `disk-health.json` (`DataPathOptions.DiskHealthJson`): the last readings, the open alerts and the armed Unknown state, so a restart neither forgets a missing disk (it stays Unknown) nor raises an open alert twice. Local to the install, not part of a self-backup. |

## Related

- Query client: [`../PrometheusQueryClient.cs`](../PrometheusQueryClient.cs)
- UI: [`../../../Components/Shared/DiskHealthBlock.razor`](../../../Components/Shared/DiskHealthBlock.razor) (dashboard), [`../../../Components/Shared/DiskHealthPanel.razor`](../../../Components/Shared/DiskHealthPanel.razor) (Settings)
- node_exporter template: [`../../../../../deploy/telemetry/host/docker-compose.yml`](../../../../../deploy/telemetry/host/docker-compose.yml) and `Services/Onboarding/OnboardingCommands.NodeExporterCompose` (textfile collector enabled)
- Tests: `DiskHealthReadingTests`, `DiskHealthAlertTests`, `DiskHealthMonitorTests`, `DiskHealthSettingTests`, `ComponentTests/DiskHealthPanelTests`
