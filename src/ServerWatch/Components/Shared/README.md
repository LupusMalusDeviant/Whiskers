# Components/Shared

Reusable UI widgets used across multiple pages.

## Files

| File | Purpose |
|---|---|
| `ResourceGauge.razor` | A progress bar that changes colour at thresholds (CPU/RAM/disk usage). |
| `HealthBadge.razor` | A small status badge rendering a container's health state. |
| `LogViewer.razor` | A scrolling log display component. |
| `RoleGuard.razor` | Renders its child content only if the current user holds the required role. |
| `AiChat.razor` | The floating read-only advisor chat widget (see [`../../Services/AiChat/`](../../Services/AiChat/)). |
| `ChatWidget.razor` | Renders a curated rich widget embedded in an agent reply — a live CPU/RAM ApexChart or a status card for a server/container. Driven by a [`ChatWidgetSpec`](../../Models/Agent/ChatWidget.cs) parsed from a `[[chart:…]]` / `[[status:…]]` token (see [`../../Services/Agent/Chat/`](../../Services/Agent/Chat/)); data via `IMetricsQueryService`. |
| `NotificationBell.razor` | AppBar bell + unread badge + dropdown feed of recent events, with a toast on arrival (see [`../../Services/Notifications/InAppNotificationStore.cs`](../../Services/Notifications/InAppNotificationStore.cs)). |

## Related

- Pages that use these: [`../Pages/`](../Pages/)
- Role checks: [`../../Services/Auth/`](../../Services/Auth/)
