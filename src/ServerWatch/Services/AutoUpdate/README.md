# Services/AutoUpdate

Scheduled **automatic container updates**. When an image update is detected ([`../ImageUpdate/`](../ImageUpdate/)), this service can pull and recreate the container according to policy, and keeps a history of what was updated for the UI.

## Files

| File | Purpose |
|---|---|
| `IAutoUpdateService.cs` / `AutoUpdateService.cs` | Background auto-update of container images; exposes the policy and update history for the UI. |

## Related

- Update detection: [`../ImageUpdate/`](../ImageUpdate/)
- Container recreate: [`../Docker/`](../Docker/)
- Notifications: [`../Notifications/`](../Notifications/)
