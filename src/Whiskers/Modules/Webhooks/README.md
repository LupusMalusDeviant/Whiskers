# Modules/Webhooks

Inbound CI/CD webhooks — the `/webhooks` management page and the `IWebhookService` that processes incoming
triggers (restart / rebuild / compose-deploy). The sixth feature extracted from `AllInOnePseudoModule`
(RoadToSAP Phase 1). No MCP tools.

- `WebhooksModule.cs` — `Id = "webhooks"`, enabled by default. `ConfigureServices` (moved **verbatim** from
  `Program.cs`) registers `IWebhookService` → `WebhookService`. Nav: the `webhooks` entry ("Webhooks", group
  *Automatisierung*). `InitializeAsync` runs after the DB migration, inside `RunWhiskersStartupAsync`, so before
  `app.Run()` — no delivery can arrive while it runs. It does two things, in this order:
  1. **INV-45 reconciliation** (`IWebhookService.ReconcileAsync`): deliveries an earlier process accepted and
     left queued or running (a stop, a crash, a database outage) are recorded once as not run, settled failed,
     written to the webhook's log, and the admins get one combined `automation_refused` notification. A pass that
     cannot settle every row is retried in the background with a growing pause; a failure is logged and never
     stops the start. Until a row is settled, a repeat of its delivery is answered busy — it is never taken over.
  2. The **F11 upgrade path**: legacy webhooks without a secret are **disabled** (never deleted) and the admin
     gets a one-time `webhook_disabled` notification — secrets are mandatory now.

**Toggle:** `Features:webhooks:Enabled` (`Features__webhooks__Enabled=false`), restart-only. When off, the
`webhooks` nav entry disappears and `/webhooks` shows a "module disabled" notice.

**The endpoint stays in Core + the no-op.** The inbound route `POST /api/webhooks/{id}` is registered on the
app in the pipeline (`WhiskersPipelineExtensions`; an app-level route can't move into a module's `ConfigureServices`), and it resolves
`IWebhookService` **per request**. So Core registers a [`NoopWebhookService`](../../Services/Webhooks/NoopWebhookService.cs)
default before the module loop; the real `WebhookService` wins by last-registration when enabled. With the
module off, the no-op's `Admit` rejects every request before its body is read (and its `AcceptAsync` every delivery), so the endpoint answers **400**, not a 500
from an unresolved service. The endpoint itself is [`WebhookEndpoint`](../../Services/Webhooks/WebhookEndpoint.cs)
(R1-10: rate admission before the body is read, bounded body, deduplicated deliveries queued and answered with a run
id); the module also binds its bounds, section `Webhooks` ([`WebhookOptions`](../../Services/Webhooks/WebhookOptions.cs)). (`CreateWebhookAsync` throws rather than pretend a webhook was created; reads
return empty.)

**DI-safe page guard.** [`Webhooks.razor`](../../Components/Pages/Webhooks.razor) is a thin route wrapper —
`<ModuleGuard ModuleId="webhooks"><WebhooksView/></ModuleGuard>`. The interactive logic that injects
`IWebhookService` lives in the child [`WebhooksView.razor`](../../Components/Pages/WebhooksView.razor). Service
code stays in [`../../Services/Webhooks/`](../../Services/Webhooks/).

See [`docs/modules/webhooks.md`](../../../../docs/modules/webhooks.md).
