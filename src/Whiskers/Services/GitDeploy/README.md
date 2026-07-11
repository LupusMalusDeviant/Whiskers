# Services/GitDeploy

**Git-based deployments** (missingFeatures F5, deliberately lean — no buildpacks, no PR previews):
a repo is cloned/updated **on the target server** via `IHostCommandExecutor`, built with its own
compose file and brought up with `docker compose up -d`.

## Files

| File | Purpose |
|---|---|
| `IGitDeployService.cs` / `GitDeployService.cs` | App CRUD (JSON store `git-deploys.json`) + the deploy pipeline (credentials → fetch/clone → sha → build → up) with step-wise progress lines. App ids are validated hex-only (they flow into remote paths). |
| `GitDeployCommands.cs` | Pure, unit-tested command builders (`GitDeployCommandsTests`): repo URL/branch/compose path single-quoted; the access token travels base64 into a 0600 file and is served via `GIT_ASKPASS` — never in a command line or process list. |
| `NoopGitDeployService.cs` | Core default when the module is off — the webhook "git-deploy" action fails gracefully instead of 500ing. |

## Security notes

- Private-repo tokens live in the **vault** (`git-token:{appId}`; vault required for private repos)
  and are re-materialized on the target at every deploy (rotation-friendly).
- Only `https://` remotes (no SSH key distribution in v1); compose path must be repo-relative.
- Push-triggered redeploys go through the Webhooks module (action `git-deploy`) — HMAC-mandatory
  since F11, so an unauthenticated push can't trigger builds.

## Related

- Module: [`../../Modules/GitDeploy/`](../../Modules/GitDeploy/) · UI: [`../../Components/Pages/GitDeployView.razor`](../../Components/Pages/GitDeployView.razor)
- Webhook dispatch: [`../Webhooks/WebhookService.cs`](../Webhooks/WebhookService.cs)
