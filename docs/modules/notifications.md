# Module: notifications

The outbound notification channels (Mattermost, Matrix, Telegram, ntfy, Discord, Slack, Email, generic
webhook) plus the composite that fans each event out over the configured channels.

| | |
|---|---|
| **Id** | `notifications` |
| **Enabled by default** | yes |
| **Toggle** | `Features:notifications:Enabled` (env `Features__notifications__Enabled=false`) — restart required |
| **Depends on** | — (soft dependency on Core via `INotificationService` / `NoopNotificationService`) |
| **Nav** | none (the `notifications` feed entry stays in Core) |
| **MCP tools** | none |
| **Services** | 8 `INotificationChannel`s + `CompositeNotificationService` (`INotificationService`); binds the 8 channel settings |

When **enabled**, `CompositeNotificationService` is registered as `INotificationService` and wins over the
Core default, fanning events out over the 8 channels, the in-app feed, and the AI-trigger dispatcher.

When **disabled**, no channel is registered and the Core `NoopNotificationService` remains, so every
consumer (CVE, Health, ImageUpdate, AutoUpdate, Metrics, LogMonitor, AI triggers, approvals) still resolves an
`INotificationService` — it just sends into a no-op. The two notification panels in *Settings* are hidden.

**Stays in Core (not this module):** the in-app feed store (`IInAppNotificationStore`) and the `notifications`
nav entry — so the bell and `/notifications` page keep working when the module is off — plus the per-container
prefs (`IContainerNotificationPrefsService`, a startup `IInitializable`) and the HttpClient log-filter block.

Settings: the 8 channel sections (`Mattermost`, `Matrix`, `Telegram`, `Ntfy`, `Discord`, `Slack`, `Email`,
`WebhookNotification` config sections) — enable/URLs/tokens/throttle per channel. Since R1-30 the credentials among
them (SMTP password, Matrix/Telegram/ntfy tokens, the ntfy topic, the Mattermost/Discord/Slack/custom webhook URLs and
the signing secret) are stored in the vault: saving one needs a usable `VAULT_KEY`, the settings file only names its
vault key (`<Property>Ref`), and the editor never shows a stored value (empty keeps it). With the vault disabled or
locked a channel whose credential is in it stays silent and the log says why. Plaintext values from before move into
the vault at the first start with a usable vault.

Code: [`src/Whiskers/Modules/Notifications/`](../../src/Whiskers/Modules/Notifications/) · channel
implementations in [`src/Whiskers/Services/Notifications/`](../../src/Whiskers/Services/Notifications/).
