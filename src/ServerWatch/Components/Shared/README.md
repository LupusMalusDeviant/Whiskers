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

## Related

- Pages that use these: [`../Pages/`](../Pages/)
- Role checks: [`../../Services/Auth/`](../../Services/Auth/)
