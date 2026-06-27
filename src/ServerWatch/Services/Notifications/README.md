# Services/Notifications

Outbound alerting. Health/log/update events are dispatched through a **composite** that fans out to every configured channel (Mattermost, Matrix), with throttling and per-container preferences so you only get the alerts you asked for.

Each channel has its own interface (distinct strategies) so the composite can enable/disable them independently.

## Files

| File | Purpose |
|---|---|
| `INotificationService.cs` | The notification surface consumers call (channel-agnostic). |
| `CompositeNotificationService.cs` | Delegates each notification to all configured providers. |
| `IMattermostNotificationService.cs` / `MattermostNotificationService.cs` | Mattermost channel (webhook). |
| `IMatrixNotificationService.cs` / `MatrixNotificationService.cs` | Matrix channel. |
| `IContainerNotificationPrefsService.cs` / `ContainerNotificationPrefsService.cs` | Per-container notification preferences (which events should notify). |
| `NotificationThrottler.cs` | Suppresses duplicate/flapping notifications within a time window. |

## Related

- Event sources: [`../HealthMonitor/`](../HealthMonitor/), [`../LogMonitor/`](../LogMonitor/), [`../ImageUpdate/`](../ImageUpdate/)
- Config: `MATTERMOST_*` in [`../../../.env.example`](../../../.env.example); Matrix configured in the UI
