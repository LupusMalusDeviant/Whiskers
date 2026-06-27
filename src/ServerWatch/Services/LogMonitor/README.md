# Services/LogMonitor

Log search and **pattern-based log alerts**. Search container logs on demand, and define alert rules that fire a notification when a matching line appears.

## Files

| File | Purpose |
|---|---|
| `ILogSearchService.cs` / `LogSearchService.cs` | Full-text / regex search across container logs. |
| `ILogMonitorService.cs` / `LogMonitorService.cs` | Background log-pattern monitor; manages the alert rules and raises notifications on matches. |

## Related

- Notification dispatch: [`../Notifications/`](../Notifications/)
- UI: [`../../Components/Pages/LogSearch.razor`](../../Components/Pages/LogSearch.razor)
- MCP tools: `search_logs`, `list_log_alerts`, `create_log_alert`
