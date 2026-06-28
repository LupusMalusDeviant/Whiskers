# Services/Notifications

Outbound alerting. Health/log/update events are dispatched through a **composite** that fans out to every configured channel (Mattermost, Matrix, Telegram, ntfy, Discord, Email, generic webhook), with throttling and per-container preferences so you only get the alerts you asked for.

Each channel has its own interface (distinct strategies) so the composite can enable/disable them independently. Channels are configured in the UI (Settings → Benachrichtigungen / Weitere Benachrichtigungskanäle), persisted live to `app-settings.json`.

## Files

| File | Purpose |
|---|---|
| `INotificationService.cs` | The notification surface consumers call (channel-agnostic). |
| `CompositeNotificationService.cs` | Delegates each notification to all configured channels + the AI-trigger dispatcher. |
| `NotificationFormatter.cs` | Single source of truth: event → title / severity / detail / in-app link, shared by the store and the outbound channels. |
| `IMattermostNotificationService.cs` / `MattermostNotificationService.cs` | Mattermost channel (webhook). |
| `IMatrixNotificationService.cs` / `MatrixNotificationService.cs` | Matrix channel. |
| `ITelegramNotificationService.cs` / `TelegramNotificationService.cs` | Telegram bot channel (sendMessage API). |
| `INtfyNotificationService.cs` / `NtfyNotificationService.cs` | ntfy push channel (ntfy.sh or self-hosted; severity → priority/tags). |
| `IDiscordNotificationService.cs` / `DiscordNotificationService.cs` | Discord incoming-webhook channel (coloured embeds per severity). |
| `IEmailNotificationService.cs` / `EmailNotificationService.cs` | Email (SMTP) channel via `System.Net.Mail`. |
| `IWebhookNotificationService.cs` / `WebhookNotificationService.cs` | Generic outbound webhook (POSTs a JSON event). Distinct from the inbound [`../Webhooks/`](../Webhooks/). |
| `IContainerNotificationPrefsService.cs` / `ContainerNotificationPrefsService.cs` | Per-container notification preferences (which events should notify). |
| `NotificationThrottler.cs` | Suppresses duplicate/flapping notifications within a time window. |
| `InAppNotificationStore.cs` | `IInAppNotificationStore`, the bell feed + persistent history (no external channel needed); fed by the composite. Keeps an in-memory cache for the bell's live updates AND write-through-persists every event to SQLite (`NotificationEntity`), hydrating on startup so the history survives restarts. Formats each event into an `InAppNotification` (title, severity) with a relative, path-base-safe `Link`, and serves the filtered/paged query for the `/notifications` page ([`../../Components/Pages/NotificationsLog.razor`](../../Components/Pages/NotificationsLog.razor)). |

## Related

- Event sources: [`../HealthMonitor/`](../HealthMonitor/), [`../LogMonitor/`](../LogMonitor/), [`../ImageUpdate/`](../ImageUpdate/)
- Config: `MATTERMOST_*` in [`../../../.env.example`](../../../.env.example); Matrix configured in the UI
