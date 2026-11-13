# Services/GitDeploy

**Git-based deployments** (missingFeatures F5, deliberately lean — no buildpacks, no PR previews):
a repo is cloned/updated **on the target server** via `IHostCommandExecutor`, built with its own
compose file and brought up with `docker compose up -d` — since contract 0006 through `IComposeRunner`: pull
(`--ignore-buildable`, new), build with `--pull`, inspect the images it built, `up -d --pull never --no-build`, then
the re-check of the containers it created (budgets 300 s / 600 s / 300 s).

## Files

| File | Purpose |
|---|---|
| `IGitDeployService.cs` / `GitDeployService.cs` | App CRUD (JSON store `git-deploys.json`) + the deploy pipeline (credentials → fetch/clone → sha → plan → build → up) with step-wise progress lines. App ids are validated hex-only (they flow into remote paths). R1-08: the service hands out copies only; a new or renamed app never takes the name of another one (case-insensitive, checked under the lock — contract 0001) and its repository URL carries no credentials; every save of the inputs sets the saver as `Owner` and bumps `Revision`, and a save from a stale revision throws `GitDeployConflictException`; `SetOwnerAsync` takes an app over only while its owner is unchanged; `DeployAsync(snapshot, gate)` runs the snapshot it was decided on and refuses when the stored app's revision or inputs differ, before the fetch and again before the build; the result is recorded without touching the inputs. R1-10: the whole deployment — credentials, checkout, plan, build, up — holds its server's lock (`ResourceLocks.ServerKey`, by the server's id whatever alias is stored; a webhook delivery takes it before it re-reads its webhook, shared with webhook compose deployments and restarts/rebuilds on that server), so nothing else deploys or acts on a container there while it writes its checkout and compose reads it, whatever path or alias the other uses; the stored app is checked once the lock is held (a save during the wait refuses the deployment). Deleting an app removes its checkout under the same lock (the app is read again once it is held). R1-42: a save trims the branch and refuses one that is not a plain branch name (`CommandOperands.RequireGitRef`); a deployment checks the stored branch again before anything runs on the target, so an app saved before R1-42 with such a branch fails without touching the server. |
| `IGitDeployService.SetTokenAsync` | Brokkr (contract 0001): capability `gitdeploy.set_token` — replaces or removes only the token of a stored app at a given revision (stale → `GitDeployConflictException`) through the same save path; the given owner (the tool passes the approving person) becomes the owner. Used by the `set_git_deploy_token` MCP tool. |
| `GitDeployOwners.cs` | R1-08: `ActorOf` (the app as an automation actor on its owner) and `DeployAsOwnerAsync`, the webhook path: an app without owner is refused; otherwise the deployment and every planned container are decided as the owner through `IAutomationMutationRunner` (`ComposeImpact.GateFor(actor, …)`). |
| `GitDeployCommands.cs` | Pure, unit-tested command builders (`GitDeployCommandsTests`): repo URL/branch single-quoted against the shell and, since R1-42, the branch validated and every operand placed where git cannot read it as an option — `git clone --branch=<b> --single-branch -- <url> repo`, `git remote set-url origin <url>` (an update always fetches from the configured repository, never the one first cloned — Fable review H2) and `git fetch origin +refs/heads/<b>:refs/remotes/origin/<b>`, `git checkout -q -f -B <b> refs/remotes/origin/<b>`, `git reset --hard refs/remotes/origin/<b>`; a non-`https://` URL or an unsafe branch throws before a command exists; the access token travels base64 into a 0600 file and is served via `GIT_ASKPASS` — never in a command line or process list. `ComposeFile` is the absolute compose path in the checkout; build and up come from the deployment's `ComposePlan` (R1-07): after fetching, the deploy is planned (`--remove-orphans`), the caller's `ComposePlanGate` decides every planned container (the Git deploy page does), and build and up run with the plan's explicit `-p` / `-f`. |
| `NoopGitDeployService.cs` | Core default when the module is off — the webhook "git-deploy" action fails gracefully instead of 500ing. |

## Security notes

- Private-repo tokens live in the **vault** (`git-token:{appId}`; vault required for private repos)
  and are re-materialized on the target at every deploy (rotation-friendly).
- Only `https://` remotes (no SSH key distribution in v1); compose path must be repo-relative.
- The branch is a plain branch name (leading letter or digit; letters, digits, `. _ / -`; no `..`, `//`, `@{`, trailing
  `/`, `.` or `.lock`) — R1-42, INV-41. Quoting alone would let `--upload-pack=…` reach git as an option.
- Push-triggered redeploys go through the Webhooks module (action `git-deploy`) — HMAC-mandatory
  since F11, so an unauthenticated push can't trigger builds — and run on the app owner's authority, re-checked
  at each run (R1-08, INV-04). An app saved before owners were recorded needs an admin to take it over first.

## Related

- Module: [`../../Modules/GitDeploy/`](../../Modules/GitDeploy/) · UI: [`../../Components/Pages/GitDeployView.razor`](../../Components/Pages/GitDeployView.razor)
- Webhook dispatch: [`../Webhooks/WebhookService.cs`](../Webhooks/WebhookService.cs)
- Audit (R1-22): save, takeover and delete record the app's versioned diff ([`../Mutations/ConfigChange.cs`](../Mutations/ConfigChange.cs)); the token is a secret (a replaced
  token is a change, fingerprints only), the revision counter and the last deploy are not part of the version. The
  revision check, the token write and the diff run under the app lock.
