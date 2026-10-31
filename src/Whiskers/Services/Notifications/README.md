# Services/Notifications

Outbound alerting. Health/log/update events are dispatched through a **composite** that fans out to every configured channel (Mattermost, Matrix, Telegram, ntfy, Discord, Email, generic webhook), with throttling and per-container preferences so you only get the alerts you asked for.

Each channel has its own interface (distinct strategies) so the composite can enable/disable them independently. Channels are configured in the UI (Settings → Benachrichtigungen / Weitere Benachrichtigungskanäle), persisted live to `app-settings.json`.

## Files

| File | Purpose |
|---|---|
| `INotificationService.cs` | The notification surface consumers call (channel-agnostic). |
| `INotificationChannel.cs` | The unifying channel contract (changeme C9): every channel implements it, so the composite fans out over `IEnumerable<INotificationChannel>` instead of hard-wiring each one. `Name` defaults to the type name minus `NotificationService`. |
| `NoopNotificationService.cs` | Core default `INotificationService` that does nothing. Registered before the module loop so every consumer still resolves when the **Notifications module** is off; the module's composite wins by last-registration when on (RoadToSAP Phase 1). |
| `CompositeNotificationService.cs` | Delegates each notification to all configured channels + the in-app feed + the AI-trigger dispatcher. Also the single enforcement point for the per-container mute/prefs (administrative events `automation_suspended` / `automation_refused` are never muted, R1-04) and the writer of the persisted `AlertHistory`. |
| `NotificationFormatter.cs` | Single source of truth: event → title / severity / detail / in-app link, shared by the store and the outbound channels. |
| `IMattermostNotificationService.cs` / `MattermostNotificationService.cs` | Mattermost channel (webhook). |
| `IMatrixNotificationService.cs` / `MatrixNotificationService.cs` | Matrix channel. |
| `ITelegramNotificationService.cs` / `TelegramNotificationService.cs` | Telegram bot channel (sendMessage API). |
| `INtfyNotificationService.cs` / `NtfyNotificationService.cs` | ntfy push channel (ntfy.sh or self-hosted; severity → priority/tags). |
| `IDiscordNotificationService.cs` / `DiscordNotificationService.cs` | Discord incoming-webhook channel (coloured embeds per severity). |
| `ISlackNotificationService.cs` / `SlackNotificationService.cs` | Slack incoming-webhook channel (coloured attachments per severity). |
| `IEmailNotificationService.cs` / `EmailNotificationService.cs` | Email (SMTP) channel via `System.Net.Mail`. |
| `IWebhookNotificationService.cs` / `WebhookNotificationService.cs` | Generic outbound webhook (POSTs a JSON event). Distinct from the inbound [`../Webhooks/`](../Webhooks/). |
| `IContainerNotificationPrefsService.cs` / `ContainerNotificationPrefsService.cs` | Per-container notification preferences (which events should notify). |
| `NotificationThrottler.cs` | Suppresses duplicate/flapping notifications within a time window (read live from settings per call, so a changed window takes effect immediately; the map self-prunes so it can't grow unbounded). |
| `NotificationRetry.cs` | Retries a send once on failure and never propagates (safe inside a monitoring loop). With the per-client 15s HttpClient timeout, this bounds how long a slow endpoint can delay a background cycle. |
| `InAppNotificationStore.cs` | `IInAppNotificationStore`, the bell feed + persistent history (no external channel needed); fed by the composite. Keeps an in-memory cache for the bell's live updates AND write-through-persists every event to SQLite (`NotificationEntity`), hydrating on startup so the history survives restarts. Formats each event into an `InAppNotification` (title, severity) with a relative, path-base-safe `Link`, and serves the filtered/paged query for the `/notifications` page ([`../../Components/Pages/NotificationsLog.razor`](../../Components/Pages/NotificationsLog.razor)). |

## Cross-cutting rules enforced by the composite

- **Mute/prefs are checked once, centrally.** They used to be consulted only by the container health
  monitor, so muting a container still let its log alerts, CVE findings, image updates and metric alarms
  through. Every producer now passes the same gate. Events without a container name (server-level events
  such as `server_unreachable`) are never suppressed by it.
- **Every delivered event is persisted to `AlertHistory`** (server id, container, type, message) — the
  queryable, fleet-aware record behind the in-app feed. Retention is handled by the metrics collector's
  hourly prune. A failing history write never blocks the notification itself.
- **Redacted once, before anything else (R1-25, INV-51).** After the mute check the composite redacts a copy of the
  event (`CompositeNotificationService.Redacted`: container, image, server names and the free-text `ImageInfo`) —
  the in-app feed, the alert history, every channel and the AI-trigger dispatcher see only that copy, whoever
  produced it (a matched log line, a connection error, a model's summary). A failed channel test names no URL.
- **HTML encoding.** Matrix renders `formatted_body` as HTML, so its message is built from an escaped copy
  of the event (`MatrixNotificationService.HtmlEscaped`) — the log-alert detail carries raw log lines, which
  are third-party text. The plain-text body stays verbatim.

## Server-level events

`server_unreachable` / `server_recovered` come from the [health monitor](../HealthMonitor/) when a host
stops answering the fleet-wide container listing. They carry `ServerId`/`ServerName` instead of a
container, link to `/servers`, and are available as AI-trigger event types.

`certificate_expiring` (Warning) / `certificate_expired` (Error) come from the certificate monitor
(`Services/Certificates/`). Every 12 hours it reads Whiskers' mTLS client certificate and CA bundle from disk,
the certificate each mTLS proxy presents in its handshake, and certbot's certificates on every enabled server.
Each certificate is announced once per stage — 30, 14 and 7 days ahead, then on expiry. The last announced stage
is kept in `certificate-alerts.json`, so a restart does not repeat an alert. A renewed certificate has a new
fingerprint (certbot: a new expiry date) and starts over. These events are deduplicated at the source and are
never throttled.

## Secret hygiene

The capability-bearing channels put their secret in the request URL (Telegram bot token in the path; Discord/Slack/Mattermost/ntfy/webhook secret URLs). Those `System.Net.Http.HttpClient.*` logger categories are raised to Warning (`WhiskersHostingExtensions`, and the whole category in code by `WhiskersLoggingExtensions`), so the default HttpClient request logging can't write the token/URL to the app log. Behind that, every log line is redacted (`RedactingLoggerProvider`): the capability URL patterns and each channel's configured secret (`KnownSecrets`, registered by `NotificationsModule` when the settings are bound) are withheld wherever they appear, also in an exception's text (R1-25).

## Wiring

These channel implementations live in Core, but their DI registration (the 8 channel settings, the
`INotificationChannel`s and `CompositeNotificationService`) is the opt-in **Notifications module**
([`../../Modules/Notifications/`](../../Modules/Notifications/), toggle `Features:notifications:Enabled`). The
in-app feed store, per-container prefs and the HttpClient log-filter stay in Core so the bell + `/notifications`
page keep working when the module is off.

## Related

- Event sources: [`../HealthMonitor/`](../HealthMonitor/), [`../LogMonitor/`](../LogMonitor/), [`../ImageUpdate/`](../ImageUpdate/)
- Config: `MATTERMOST_*` in [`../../../.env.example`](../../../.env.example); Matrix configured in the UI
