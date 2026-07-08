# Services/Webhooks

CI/CD **webhook** handling, registers webhook endpoints and processes incoming triggers (e.g. redeploy a container when a pipeline pushes a new image).

## Files

| File | Purpose |
|---|---|
| `IWebhookService.cs` / `WebhookService.cs` | Manages CI/CD webhooks and processes incoming webhook triggers. |

## Security notes

- **HMAC:** when a webhook has a secret configured, a valid `sha256=…` signature is required (constant-time compared). Webhooks created without a secret are currently unauthenticated — anyone who learns the URL can trigger them; a secret-management UI is still outstanding (see review finding HOCH-12 part 2). Triggering from the UI (`TestWebhook`) requires the Operator role.
- **`deploy` action:** the compose project directory (`TargetId`) must be an **absolute path** and is single-quoted for the host shell before `cd`. This prevents shell injection / breakage from a crafted or space-containing path.

## Related

- UI: [`../../Components/Pages/Webhooks.razor`](../../Components/Pages/Webhooks.razor)
- Deployment: [`../Deployment/`](../Deployment/)
