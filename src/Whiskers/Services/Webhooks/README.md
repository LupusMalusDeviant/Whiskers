# Services/Webhooks

CI/CD **webhook** handling, registers webhook endpoints and processes incoming triggers (e.g. redeploy a container when a pipeline pushes a new image).

## Files

| File | Purpose |
|---|---|
| `IWebhookService.cs` / `WebhookService.cs` | Manages CI/CD webhooks and processes incoming webhook triggers. Generates the mandatory HMAC secret on create, regenerates on demand (one-time display), and runs the signed self-test. |
| `NoopWebhookService.cs` | Core default `IWebhookService` for when the **Webhooks module** is off. Keeps the Core `POST /api/webhooks/{id}` endpoint's per-request resolution working; `TriggerAsync` fails gracefully (endpoint → 400, not 500), reads return empty, create throws. Real service wins by last-registration when the module is on (RoadToSAP Phase 1). |

## Wiring

This is the opt-in **Webhooks module** ([`../../Modules/Webhooks/`](../../Modules/Webhooks/), toggle
`Features:webhooks:Enabled`): its `ConfigureServices` registers `IWebhookService` and owns the `webhooks` nav
entry. The inbound `POST /api/webhooks/{id}` endpoint stays in Core (`Program.cs`) and resolves the service per
request, so Core keeps the `NoopWebhookService` default above for when the module is off.

## Security notes

- **HMAC is mandatory (F11 / HOCH-12 part 2):** every webhook has a server-generated 256-bit secret; a valid `X-Hub-Signature-256`-style `sha256=…` signature over the raw body is required (constant-time compared) — GitHub/Gitea/GitLab webhook signing works natively. Webhooks without a secret (legacy rows) are rejected fail-closed AND disabled at boot by the module's `InitializeAsync` (kept, never deleted; the admin gets a `webhook_disabled` notification). The secret is shown exactly once (create/regenerate) and is not retrievable afterwards; re-enabling a secret-less webhook requires regenerating its secret first. The UI test button sends a genuinely signed request through the same validation path (`TriggerSignedTestAsync`). Triggering from the UI requires the Operator role.
- **`deploy` action:** the compose project directory (`TargetId`) must be an **absolute path** and is single-quoted for the host shell before `cd`. This prevents shell injection / breakage from a crafted or space-containing path.

## Related

- UI: [`../../Components/Pages/Webhooks.razor`](../../Components/Pages/Webhooks.razor)
- Deployment: [`../Deployment/`](../Deployment/)
