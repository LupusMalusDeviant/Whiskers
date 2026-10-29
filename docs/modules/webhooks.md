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
inbound `POST /api/webhooks/{id}` answers **400** (`{"status":"rejected"}`) instead of triggering anything.

**Why a no-op:** the inbound `POST /api/webhooks/{id}` endpoint lives in `Program.cs` (an app route, not a
service) and resolves `IWebhookService` per request, so it can't be gated by the module the way a page is. A
`NoopWebhookService` default keeps that resolution working when the module is off — its `AcceptAsync` rejects
every delivery (400, not a 500 from an unresolved service).

**Calling a webhook (R1-10).** Each webhook has a signature scheme:

- `github` — `X-Hub-Signature-256: sha256=<HMAC-SHA256(secret, body)>` (GitHub, Gitea). GitHub signs no time, so a
  repeated body is recognised for the retention period (90 days), not by its age.
- `whiskers` — `X-Whiskers-Timestamp: <Unix seconds>`, `X-Whiskers-Delivery: <unique id, 8–128 of A-Z a-z 0-9 _ -; no dot>`
  and `X-Whiskers-Signature: sha256=<HMAC-SHA256(secret, "<timestamp>.<delivery>.<body>")>`; a timestamp more than
  five minutes off is refused.

Answers: **202** `{"status":"queued","runId":…}` (the action runs in a queue; its result is in the webhook log),
**200** `{"status":"duplicate","runId":…,"state":…}` for a delivery seen before (it does not run again; `interrupted`
means it was running when Whiskers stopped), **400** `{"status":"rejected"}` for any invalid request, **429** / **503**
with `Retry-After`. Runs on the same target never overlap.

Settings (section `Webhooks`): `MaxBodyBytes`, `RequestsPerMinute`, `RequestsPerMinutePerWebhook`, `QueueCapacity`,
`MaxConcurrentRuns`, `TimestampTolerance`, `DeliveryRetention`, `RunTimeout` (30 min: a run that cannot get its target
in time fails; an action that does not stop keeps its target until it ends, so nothing overlaps it), `TestRunTimeout` — see
[`WebhookOptions`](../../src/Whiskers/Services/Webhooks/WebhookOptions.cs). Webhooks themselves are managed on the
`/webhooks` page.

Code: [`src/Whiskers/Modules/Webhooks/`](../../src/Whiskers/Modules/Webhooks/) · service in
[`src/Whiskers/Services/Webhooks/`](../../src/Whiskers/Services/Webhooks/) · inbound endpoint
[`WebhookEndpoint`](../../src/Whiskers/Services/Webhooks/WebhookEndpoint.cs), mapped in
[`WhiskersPipelineExtensions`](../../src/Whiskers/Startup/WhiskersPipelineExtensions.cs).
