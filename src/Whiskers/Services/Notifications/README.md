# Services/Notifications

Outbound alerting. Health/log/update events are dispatched through a **composite** that fans out to every configured channel (Mattermost, Matrix, Telegram, ntfy, Discord, Email, generic webhook), with throttling and per-container preferences so you only get the alerts you asked for.

Each channel has its own interface (distinct strategies) so the composite can enable/disable them independently. Channels are configured in the UI (Settings → Benachrichtigungen / Weitere Benachrichtigungskanäle), persisted live to `app-settings.json`.

## Files

| File | Purpose |
|---|---|
| `INotificationService.cs` | The notification surface consumers call (channel-agnostic). |
| `INotificationChannel.cs` | The unifying channel contract (changeme C9): every channel implements it, so the composite fans out over `IEnumerable<INotificationChannel>` instead of hard-wiring each one. `Name` defaults to the type name minus `NotificationService`. R1-40: `DeliverAsync(evt, ct)` returns `true` when sent, `false` when there was nothing to send (off, not configured, throttled), and **throws** on a failure — a channel never swallows its own failure; the deadline `ct` reaches the HTTP or SMTP call and every response is disposed. `INotificationService.SendAsync` on a channel is `DeliverAsync` without a deadline. |
| `NoopNotificationService.cs` | Core default `INotificationService` that does nothing. Registered before the module loop so every consumer still resolves when the **Notifications module** is off; the module's composite wins by last-registration when on (RoadToSAP Phase 1). |
| `CompositeNotificationService.cs` | Delegates each notification to all configured channels + the in-app feed + the AI-trigger dispatcher. Also the single enforcement point for the per-container mute/prefs (administrative events `automation_suspended` / `automation_refused` — R1-04 — and `operations_interrupted` — R1-39 — are never muted) and the writer of the persisted `AlertHistory`. R1-40: each channel gets `MaxAttempts` (2) attempts of at most `AttemptTimeout` (20 s) each, 2 s apart, the channels side by side — so `SendAsync` returns within about 42 s whatever a provider does. A failure the retries could not overcome is logged with the channel name and the redacted reason only (never the payload, a URL or a token) and counted (`whiskers_self_notification_failed_total`); a critical event (`IsCritical`: administrative events, `agent_approval`, every event of severity Error) goes to the outbox for redelivery — in memory, without waiting for the disk. Each event gets a `DeliveryId` before the first attempt, kept for every retry and redelivery (Matrix uses it as the transaction id, so a retry after a lost answer is not posted twice). Each channel gets its own copy of the event (what it records about its delivery stays with its own outbox entry). The AI-trigger dispatch runs once (never retried: a second dispatch would start the triggers twice), its wait bounded by the attempt deadline. |
| `NotificationFormatter.cs` | Single source of truth: event → title / severity / detail / in-app link, shared by the store and the outbound channels. |
| `IMattermostNotificationService.cs` / `MattermostNotificationService.cs` | Mattermost channel (webhook). |
| `IMatrixNotificationService.cs` / `MatrixNotificationService.cs` | Matrix channel. |
| `ITelegramNotificationService.cs` / `TelegramNotificationService.cs` | Telegram bot channel (sendMessage API). |
| `INtfyNotificationService.cs` / `NtfyNotificationService.cs` | ntfy push channel (ntfy.sh or self-hosted; severity → priority/tags). |
| `IDiscordNotificationService.cs` / `DiscordNotificationService.cs` | Discord incoming-webhook channel (coloured embeds per severity). |
| `ISlackNotificationService.cs` / `SlackNotificationService.cs` | Slack incoming-webhook channel (coloured attachments per severity). |
| `IEmailNotificationService.cs` / `EmailNotificationService.cs` | Email (SMTP) channel via `System.Net.Mail`. R1-40: one SMTP conversation is bounded by `SmtpDeadline` (30 s). Its end or the caller's cancellation disposes the `SmtpClient`, which aborts the connection in every phase (connect, the server's final answer — phases `SmtpClient` does not watch the token in), and the send is awaited until it has ended; `SmtpClient.Timeout` is the last bound. Partial delivery: a server that takes some recipients and refuses others (`SmtpFailedRecipient(s)Exception`) — the ones it took are recorded on the event (`NotificationEvent.DeliveredRecipients`) before the failure is thrown, and every retry and redelivery (through the outbox, also after a restart) names only the others. |
| `IWebhookNotificationService.cs` / `WebhookNotificationService.cs` | Generic outbound webhook (POSTs a JSON event). Distinct from the inbound [`../Webhooks/`](../Webhooks/). |
| `IContainerNotificationPrefsService.cs` / `ContainerNotificationPrefsService.cs` | Per-container notification preferences (which events should notify). |
| `NotificationThrottler.cs` | Suppresses duplicate/flapping notifications within a time window (read live from settings per call, so a changed window takes effect immediately; the map self-prunes so it can't grow unbounded). |
| `NotificationRetry.cs` | Retries a send and never propagates (safe inside a monitoring loop). R1-40: every attempt has its own deadline that reaches the provider's network call (an attempt past it fails as a `TimeoutException`), with a wait between attempts; the caller's cancellation ends the retries and stays a cancellation. |
| `NotificationOutbox.cs` | R1-40 (F-08): `INotificationOutbox` — the bounded (`MaxEntries` 200, oldest dropped and counted), durable (`notification-outbox.json`, `JsonFileStore`; not restored from a self-backup) store of critical events a channel still failed after the composite's retries, each for that one channel, with the redacted event and the last error (type and redacted message). Redelivery waits 1, 2, 4 … minutes (at most an hour); after `MaxAttempts` (8) redeliveries or `MaxAge` (24 h) the entry is exhausted: removed, logged and counted. `Status` (pending, redelivered, exhausted, dropped, unsaved) feeds `/metrics`. Every change is made in memory at once; only `FlushAsync` (the worker) touches the file: it reads it once — a file that cannot be read for the moment is read again by the next pass and never written over meanwhile, entries added before are merged; a corrupt one — also one that parses but is no outbox (`OutboxFile.Problem`: a `null` entry, an entry without event, id or channel) — is preserved aside by the store and its previous generation used, or, without one, replaced — and writes what changed; a failed write stays pending and is retried. `Status.Unsaved` stays set from the moment a change is made until its write is through, and while the file could not be read yet. An entry past `MaxAge` is given up on before it is sent (`DueAsync`, and again by `CurrentAsync` just before the send). `FailedAsync` counts the next wait from the failure and keeps the channel's progress (`progress`: the recipients reached). The last error keeps only scheme and host of a URL (a webhook or bot URL carries its credential in the path). Every entry that leaves undelivered (outbox full, channel switched off or removed) is logged with its reason and counted as dropped. The file holds the event as the channels got it, already redacted (INV-51). |
| `NotificationOutboxWorker.cs` | R1-40: a `FleetBackgroundService` that owns the outbox file: right after an addition, after every single redelivery and at the latest every 30 s, it writes what changed — a long backlog never holds back a write. Each due entry is checked again just before it is sent (`CurrentAsync`: still in the outbox, not past `MaxAge` by then) and given once more to its channel, within the attempt deadline, as a redelivery (`IsRedelivery`: not throttled again — it passed the throttle when it was first sent). Delivered, it leaves the outbox; its channel switched off or removed, it is dropped and counted; a failure waits longer. At stop it writes once more (bounded to 5 s). |
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

The capability-bearing channels put their secret in the request URL (Telegram bot token in the path; Discord/Slack/Mattermost/ntfy/webhook secret URLs). Those `System.Net.Http.HttpClient.*` logger categories are raised to Warning (`WhiskersHostingExtensions`, and the whole category in code by `WhiskersLoggingExtensions`), so the default HttpClient request logging can't write the token/URL to the app log. A configuration rule bound to a provider (`Logging:Console:LogLevel`) or to a more specific category can still switch it on; behind it, every log line is redacted (`RedactingLoggerFactory`): the capability URL patterns and each channel's configured secret (`KnownSecrets`, registered by `NotificationsModule` when the settings are bound) are withheld wherever they appear, also in an exception's text (R1-25).

## Wiring

These channel implementations live in Core, but their DI registration (the 8 channel settings, the
`INotificationChannel`s and `CompositeNotificationService`) is the opt-in **Notifications module**
([`../../Modules/Notifications/`](../../Modules/Notifications/), toggle `Features:notifications:Enabled`). The
in-app feed store, per-container prefs and the HttpClient log-filter stay in Core so the bell + `/notifications`
page keep working when the module is off.

## Related

- Event sources: [`../HealthMonitor/`](../HealthMonitor/), [`../LogMonitor/`](../LogMonitor/), [`../ImageUpdate/`](../ImageUpdate/)
- Config: `MATTERMOST_*` in [`../../../.env.example`](../../../.env.example); Matrix configured in the UI
