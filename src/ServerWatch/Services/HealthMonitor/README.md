# Services/HealthMonitor

Background **container health watching**. Continuously tracks container health state across the fleet, raises notifications on transitions (unhealthy / stopped / OOM / restart loops), and keeps a short health history for the UI's health reports.

## Files

| File | Purpose |
|---|---|
| `ContainerHealthMonitor.cs` | The background service: polls container health, detects state transitions and restart loops, and fires notifications (respecting per-container notification prefs). |
| `IHealthStore.cs` / `InMemoryHealthStore.cs` | Stores recent health state/history per container for reports and the dashboard. |

## Related

- Notification dispatch + per-container prefs: [`../Notifications/`](../Notifications/)
- UI: [`../../Components/Pages/HealthReports.razor`](../../Components/Pages/HealthReports.razor), [`Dashboard.razor`](../../Components/Pages/Dashboard.razor)
- MCP tool: `get_health_summary`
