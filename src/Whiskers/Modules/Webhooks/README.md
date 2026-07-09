# Modules/Webhooks

Inbound CI/CD webhooks — the `/webhooks` management page and the `IWebhookService` that processes incoming
triggers (restart / rebuild / compose-deploy). The sixth feature extracted from `AllInOnePseudoModule`
(RoadToSAP Phase 1). No MCP tools.

- `WebhooksModule.cs` — `Id = "webhooks"`, enabled by default. `ConfigureServices` (moved **verbatim** from
  `Program.cs`) registers `IWebhookService` → `WebhookService`. Nav: the `webhooks` entry ("Webhooks", group
  *Automatisierung*).

**Toggle:** `Features:webhooks:Enabled` (`Features__webhooks__Enabled=false`), restart-only. When off, the
`webhooks` nav entry disappears and `/webhooks` shows a "module disabled" notice.

**The endpoint stays in Core + the no-op.** The inbound route `POST /api/webhooks/{id}` is registered on the
app in `Program.cs` (an app-level route can't move into a module's `ConfigureServices`), and it resolves
`IWebhookService` **per request**. So Core registers a [`NoopWebhookService`](../../Services/Webhooks/NoopWebhookService.cs)
default before the module loop; the real `WebhookService` wins by last-registration when enabled. With the
module off, the no-op's `TriggerAsync` returns a graceful failure so the endpoint answers **400**, not a 500
from an unresolved service. (`CreateWebhookAsync` throws rather than pretend a webhook was created; reads
return empty.)

**DI-safe page guard.** [`Webhooks.razor`](../../Components/Pages/Webhooks.razor) is a thin route wrapper —
`<ModuleGuard ModuleId="webhooks"><WebhooksView/></ModuleGuard>`. The interactive logic that injects
`IWebhookService` lives in the child [`WebhooksView.razor`](../../Components/Pages/WebhooksView.razor). Service
code stays in [`../../Services/Webhooks/`](../../Services/Webhooks/).

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and [`docs/modules/webhooks.md`](../../../../docs/modules/webhooks.md).
