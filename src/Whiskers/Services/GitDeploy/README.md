# Services/GitDeploy

**Git-based deployments** (missingFeatures F5, deliberately lean — no buildpacks, no PR previews):
a repo is cloned/updated **on the target server** via `IHostCommandExecutor`, built with its own
compose file and brought up with `docker compose up -d`.

## Files

| File | Purpose |
|---|---|
| `IGitDeployService.cs` / `GitDeployService.cs` | App CRUD (JSON store `git-deploys.json`) + the deploy pipeline (credentials → fetch/clone → sha → plan → build → up) with step-wise progress lines. App ids are validated hex-only (they flow into remote paths). R1-08: the service hands out copies only; every save of the inputs sets the saver as `Owner` and bumps `Revision`, and a save from a stale revision throws `GitDeployConflictException`; `SetOwnerAsync` takes an app over only while its owner is unchanged; `DeployAsync(snapshot, gate)` runs the snapshot it was decided on and refuses when the stored app's revision or inputs differ, before the fetch and again before the build; the result is recorded without touching the inputs. R1-10: build and up hold the compose project's lock (`ResourceLocks.ComposeProject`), shared with compose webhooks and other apps of the same project; the plan is made again under it, and one that now names another project waits for that project's lock instead. |
| `GitDeployOwners.cs` | R1-08: `ActorOf` (the app as an automation actor on its owner) and `DeployAsOwnerAsync`, the webhook path: an app without owner is refused; otherwise the deployment and every planned container are decided as the owner through `IAutomationMutationRunner` (`ComposeImpact.GateFor(actor, …)`). |
| `GitDeployCommands.cs` | Pure, unit-tested command builders (`GitDeployCommandsTests`): repo URL/branch single-quoted; the access token travels base64 into a 0600 file and is served via `GIT_ASKPASS` — never in a command line or process list. `ComposeFile` is the absolute compose path in the checkout; build and up come from the deployment's `ComposePlan` (R1-07): after fetching, the deploy is planned (`--remove-orphans`), the caller's `ComposePlanGate` decides every planned container (the Git deploy page does), and build and up run with the plan's explicit `-p` / `-f`. |
| `NoopGitDeployService.cs` | Core default when the module is off — the webhook "git-deploy" action fails gracefully instead of 500ing. |

## Security notes

- Private-repo tokens live in the **vault** (`git-token:{appId}`; vault required for private repos)
  and are re-materialized on the target at every deploy (rotation-friendly).
- Only `https://` remotes (no SSH key distribution in v1); compose path must be repo-relative.
- Push-triggered redeploys go through the Webhooks module (action `git-deploy`) — HMAC-mandatory
  since F11, so an unauthenticated push can't trigger builds — and run on the app owner's authority, re-checked
  at each run (R1-08, INV-04). An app saved before owners were recorded needs an admin to take it over first.

## Related

- Module: [`../../Modules/GitDeploy/`](../../Modules/GitDeploy/) · UI: [`../../Components/Pages/GitDeployView.razor`](../../Components/Pages/GitDeployView.razor)
- Webhook dispatch: [`../Webhooks/WebhookService.cs`](../Webhooks/WebhookService.cs)
