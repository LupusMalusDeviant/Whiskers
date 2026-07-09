# Services/LogMonitor

Log search and **pattern-based log alerts**. Search container logs on demand, and define alert rules that fire a notification when a matching line appears.

## Files

| File | Purpose |
|---|---|
| `ILogSearchService.cs` / `LogSearchService.cs` | Full-text / regex search across container logs. |
| `ILogMonitorService.cs` / `LogMonitorService.cs` | Background log-pattern monitor; manages the alert rules and raises notifications on matches. |
| `NoopLogMonitorService.cs` | Core default `ILogMonitorService` (no rules, no monitor). Registered before the module loop so the AI-triggers page still resolves it when the **LogMonitor module** is off; the real `LogMonitorService` wins by last-registration when on (RoadToSAP Phase 1). |

## Wiring

This is the opt-in **LogMonitor module** ([`../../Modules/LogMonitor/`](../../Modules/LogMonitor/), toggle
`Features:logmonitor:Enabled`): its `ConfigureServices` registers `ILogSearchService` + the hosted
`LogMonitorService`, and the module owns the `logs` nav entry and the `LogTools` MCP tools. `ILogSearchService`
has no Core consumer; `ILogMonitorService` does (the AI-triggers page), so Core keeps the
`NoopLogMonitorService` default above for when the module is off.

## Related

- Notification dispatch: [`../Notifications/`](../Notifications/)
- UI: [`../../Components/Pages/LogSearch.razor`](../../Components/Pages/LogSearch.razor) (thin `ModuleGuard`
  wrapper) → [`../../Components/Pages/LogSearchView.razor`](../../Components/Pages/LogSearchView.razor)
- MCP tools: `search_logs`, `list_log_alerts`, `create_log_alert`
