# Services/Webhooks

CI/CD **webhook** handling, registers webhook endpoints and processes incoming triggers (e.g. redeploy a container when a pipeline pushes a new image).

## Files

| File | Purpose |
|---|---|
| `IWebhookService.cs` / `WebhookService.cs` | Manages CI/CD webhooks and processes incoming webhook triggers. |
| `NoopWebhookService.cs` | Core default `IWebhookService` for when the **Webhooks module** is off. Keeps the Core `POST /api/webhooks/{id}` endpoint's per-request resolution working; `TriggerAsync` fails gracefully (endpoint → 400, not 500), reads return empty, create throws. Real service wins by last-registration when the module is on (RoadToSAP Phase 1). |

## Wiring

This is the opt-in **Webhooks module** ([`../../Modules/Webhooks/`](../../Modules/Webhooks/), toggle
`Features:webhooks:Enabled`): its `ConfigureServices` registers `IWebhookService` and owns the `webhooks` nav
entry. The inbound `POST /api/webhooks/{id}` endpoint stays in Core (`Program.cs`) and resolves the service per
request, so Core keeps the `NoopWebhookService` default above for when the module is off.

## Security notes

- **HMAC:** when a webhook has a secret configured, a valid `sha256=…` signature is required (constant-time compared). Webhooks created without a secret are currently unauthenticated — anyone who learns the URL can trigger them; a secret-management UI is still outstanding (see review finding HOCH-12 part 2). Triggering from the UI (`TestWebhook`) requires the Operator role.
- **`deploy` action:** the compose project directory (`TargetId`) must be an **absolute path** and is single-quoted for the host shell before `cd`. This prevents shell injection / breakage from a crafted or space-containing path.

## Related

- UI: [`../../Components/Pages/Webhooks.razor`](../../Components/Pages/Webhooks.razor)
- Deployment: [`../Deployment/`](../Deployment/)
