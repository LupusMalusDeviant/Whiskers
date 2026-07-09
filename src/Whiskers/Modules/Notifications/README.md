# Modules/Notifications

The outbound notification channels + the composite that fans an event out over them — the second feature
extracted from the transitional `AllInOnePseudoModule` into its own `IWhiskersModule` (RoadToSAP Phase 1).

- `NotificationsModule.cs` — `Id = "notifications"`, enabled by default. `ConfigureServices` (moved
  **verbatim** from `Program.cs`) binds the 8 channel settings (`Mattermost`, `Matrix`, `Telegram`, `Ntfy`,
  `Discord`, `Slack`, `Email`, `WebhookNotification`), registers the 8 channels as `INotificationChannel`
  (changeme C9), and registers `CompositeNotificationService` as `INotificationService`. No nav entry and no
  MCP tools.

**Toggle:** `Features:notifications:Enabled` (`Features__notifications__Enabled=false`), restart-only.

**The soft-dependency pattern (why nothing breaks when it's off).** Eight Core services consume
`INotificationService` (CVE, Health, ImageUpdate, AutoUpdate, Metrics, LogMonitor, AI triggers, approvals).
Core registers a [`NoopNotificationService`](../../Services/Notifications/NoopNotificationService.cs) **before**
the module loop; when this module is enabled its `CompositeNotificationService` is registered afterwards and
wins by last-registration, when it's off the Noop remains. So the consumers always resolve — with the module
off they simply send into a no-op and no channel HttpClients are wired.

**Deliberately kept in Core, not this module:**

- `IInAppNotificationStore` — the in-app **feed** (bell + `/notifications` page). It's notification *data*; the
  composite writes to it and the page reads it even when the module is off, so the feed keeps working. The
  `notifications` **nav** entry therefore stays in `AllInOnePseudoModule`.
- `IContainerNotificationPrefsService` — per-container prefs, and an `IInitializable` in the Core startup loop
  (moving it into the module would break that loop when the module is off).
- The HttpClient **log-filter** block (raises the notification HttpClient categories to `Warning` so secret
  URLs aren't logged) hangs off `builder.Logging`, not `IServiceCollection`, so it can't live in
  `ConfigureServices`; it stays in Core as a harmless no-op when the module is off.

**Settings.** The two notification panels in [`Settings.razor`](../../Components/Pages/Settings.razor) are
gated by `@if (ModuleRegistry.IsEnabled("notifications"))`. The Matrix "test" button resolves
`IMatrixNotificationService` **lazily** (`IServiceProvider.GetService`) rather than via `@inject`, so the page
still loads when the module is off (an `@inject` throws at instantiation before a markup guard can help — the
lesson from the Terminal pilot). Channel implementations stay in
[`../../Services/Notifications/`](../../Services/Notifications/).

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and
[`docs/modules/notifications.md`](../../../../docs/modules/notifications.md).
