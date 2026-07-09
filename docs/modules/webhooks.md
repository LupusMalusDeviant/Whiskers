# Module: webhooks

Inbound CI/CD webhooks: define webhook URLs that GitHub/GitLab/etc. call to restart, rebuild or compose-deploy
a target. The `/webhooks` management page, the `IWebhookService`, and (in Core) the inbound endpoint.

| | |
|---|---|
| **Id** | `webhooks` |
| **Enabled by default** | yes |
| **Toggle** | `Features:webhooks:Enabled` (env `Features__webhooks__Enabled=false`) — restart required |
| **Depends on** | — (the Core inbound endpoint uses a Core no-op default) |
| **Nav** | `webhooks` — "Webhooks" (group *Automatisierung*) |
| **MCP tools** | — |
| **Services** | `IWebhookService` |

When **disabled**: the `webhooks` nav entry disappears, `/webhooks` shows a "module disabled" notice, and an
inbound `POST /api/webhooks/{id}` answers **400** ("webhooks are disabled") instead of triggering anything.

**Why a no-op:** the inbound `POST /api/webhooks/{id}` endpoint lives in `Program.cs` (an app route, not a
service) and resolves `IWebhookService` per request, so it can't be gated by the module the way a page is. A
`NoopWebhookService` default keeps that resolution working when the module is off — its `TriggerAsync` fails
gracefully (400, not a 500 from an unresolved service).

No settings section (webhooks are created/managed on the `/webhooks` page).

Code: [`src/Whiskers/Modules/Webhooks/`](../../src/Whiskers/Modules/Webhooks/) · service in
[`src/Whiskers/Services/Webhooks/`](../../src/Whiskers/Services/Webhooks/) · inbound endpoint in
[`src/Whiskers/Program.cs`](../../src/Whiskers/Program.cs).
