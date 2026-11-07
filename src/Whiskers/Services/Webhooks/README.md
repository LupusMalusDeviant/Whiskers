# Services/Webhooks

CI/CD **webhook** handling, registers webhook endpoints and processes incoming triggers (e.g. redeploy a container when a pipeline pushes a new image).

## Files

| File | Purpose |
|---|---|
| `IWebhookService.cs` / `WebhookService.cs` | Manages CI/CD webhooks and processes incoming webhook triggers. Generates the mandatory HMAC secret on create, regenerates on demand (one-time display), and runs the signed self-test. R1-09: a valid call runs its action through `IAutomationMutationRunner` on the webhook's `Owner` (`ActorOf`): restart / rebuild as `container.restart` / `container.update`, deploy as `compose.deploy` plus each planned container, Git deploy as `gitdeploy.deploy` and then on the app owner (`GitDeployOwners`). Every delivery is decided, also one without an owner or with a target or plan that cannot be resolved or looked up (audited, refused and reported; the lookup error stays in the log, and a compose planning error — it can quote `.env` values — is replaced by a generic reason everywhere, the page's Test shows it to the person; a plan that changes between the decision and `up` is a reported refusal too; an interpolated project name is decided and audited as its source expression); the action itself runs only with an owner. A refusal for lost authority (or no owner) switches the webhook off (only while its owner is unchanged) and notifies an admin (`automation_suspended`), any other refusal — including the Git app owner's (`GitDeployOwners.RunAsOwnerAsync`) — notifies (`automation_refused`). `SetOwnerAsync` takes a webhook over only while its owner is unchanged. R1-31 (INV-52): the secret is stored encrypted with a vault-derived key bound to the webhook (`ProtectedFields`) and returned in plaintext only to the caller that created or regenerated it; without a usable vault both are refused before anything is written. A delivery (and the signed test) opens it first — one that cannot be opened is rejected like a webhook without a secret. R1-48: management writes are marked `webhook.*`; a compose deployment runs its `docker compose up` through the host primitive (`ExecuteMutationAsync`), inside the run's `compose.deploy` decision. The run workers are started with the flow suppressed, so no caller's scope travels with them. |
| `WebhookSignatures.cs` | R1-10 (INV-42): the provider rules and `WebhookRequest`. **github** — `X-Hub-Signature-256` over the body; GitHub signs no time and no delivery id (`X-GitHub-Delivery` is ignored), so a delivery is identified by the hash of its signed body and is recognised as a repeat for `DeliveryRetention`, not by its age. **whiskers** — `X-Whiskers-Timestamp` (Unix seconds), `X-Whiskers-Delivery` (8–128 chars `[A-Za-z0-9_-]` — no `.`, the separator of the signed fields, so they split one way only) and `X-Whiskers-Signature: sha256=HMAC(secret, "{timestamp}.{delivery}.{body}")`; the signature is checked first, then the timestamp against `TimestampTolerance` (both directions); the signed delivery id identifies it. Constant-time comparison. `SignedTest` builds the page's Test delivery for either. |
| `WebhookDeliveries.cs` | R1-10: `WebhookReply` / `WebhookAcceptance` and the delivery store (table `WebhookDeliveries`, unique per webhook and delivery key). `RecordAsync` inserts once as `Recording` (a claim token recognises a retried insert); `AcceptAsync` moves it to `Queued` under its claim before the delivery is answered "accepted" (INV-45, review round 4: the acceptance is persisted). A key on record answers with its run and state. A claim is live while this process holds its run (being recorded, queued, or running); while its delivery is still being recorded a repeat is answered `Busy` (it may yet fail), and only once the run is in the queue a repeat is a duplicate of it. A row whose claim is not live is taken over by exactly one later delivery (`Orphaned`) only if it was never accepted (`Recording`: a lost insert answer, a failure before the acceptance) or this process accepted it and lost the answer (`Queued` under one of its own claims) — it never ran (a run its worker gave up keeps its claim until it is settled or the process ends); a takeover is fenced on the state and claim it read, and one whose update answers "nothing changed" reads the row again, so a retried update after a lost commit answer still knows it took it. A row an earlier process accepted and left `Queued` is never taken over (`AwaitingReconcile`): a repeat is `Busy` and asks for a reconciliation pass; once `ReconcileAsync` settled it, a repeat is a duplicate of the failed run; a `Running` row whose claim is not live reads as `interrupted` and does not run again. `StartAsync` and `FinishAsync` act only under the claim that queued the run. `FindAsync` reads a key without recording it; `StartAsync` / `FinishAsync` move a run Queued → Running → Succeeded / Failed / Refused; `PruneAsync` removes finished keys after `DeliveryRetention`. `SettleAsync` settles a given-up run (queued or running → failed, fenced by its claim); `UnsettledOfOthersAsync` (queued and running rows not under this process's delivery claims, whatever their age) and `AdoptAsync` (a fenced takeover under a `reconcile:` claim, which never counts as this process's own, so a row a failed pass leaves stays awaiting reconciliation and is found again) serve the start reconciliation (INV-45). |
| `WebhookOptions.cs` | R1-10: section `Webhooks` — `MaxBodyBytes` (1 MiB), `RequestsPerMinute` (300, all webhooks), `RequestsPerMinutePerWebhook` (30), `QueueCapacity` (100), `MaxConcurrentRuns` (4), `TimestampTolerance` (5 min), `DeliveryRetention` (90 days), `RunTimeout` (30 min), `TestRunTimeout` (10 min). |
| `WebhookEndpoint.cs` | R1-10: `POST /api/webhooks/{id}` (mapped in the pipeline, anonymous). Rate admission first (`Admit`: a limited request is answered without reading its body; an oversized one counts), then reads at most `MaxBodyBytes` with the request's cancellation and answers at once: 202 `queued` + `runId`, 200 `duplicate` + `runId` + `state`, 400 `rejected` for every invalid request (unknown, disabled, bad signature, stale, too large — no detail), 429 / 503 with `Retry-After`. |
| `WebhookTargets.cs` | What a webhook acts on, shared by the Webhooks page's Test and the trigger (R1-06, R1-09): `ServerOf` (a webhook without a server acts on the server with the id `local`, never on "the default server"), `ContainerAsync` (all containers, stopped ones too; exact id or name, else a unique id prefix; an ambiguous prefix is an error) and `ComposePlanAsync` (the deploy plan of the absolute project directory). |
| `NoopWebhookService.cs` | Core default `IWebhookService` for when the **Webhooks module** is off. Keeps the Core `POST /api/webhooks/{id}` endpoint's per-request resolution working; `AcceptAsync` rejects every delivery (endpoint → 400, not 500), reads return empty, create throws. Real service wins by last-registration when the module is on (RoadToSAP Phase 1). |

## Delivery (R1-10, INV-42)

`Admit` (before the body is read): the global rate, then a per-id rate counted in one of 1024 buckets of the id as
sent — so a known and an unknown id are limited alike and the bookkeeping stays bounded. `AcceptAsync`: size →
webhook lookup → enabled → provider rule → a queue slot (`QueueCapacity`) → `RecordAsync` → queue. A delivery is
recorded only once its slot is reserved, so every answer naming a queued run is kept; when the queue is full a known
delivery is still answered with its run and a new one is `Busy` and not recorded. A new delivery is answered at once
with its run id; a repeat (concurrent, later, after a restart) with the first run's id and state. Workers
(`MaxConcurrentRuns`, started with the first delivery without the caller's execution context — no mutation scope,
culture or user travels to later deliveries — and stopped with the app) run each delivery once, holding a lock
(`TargetAsync`, the process-wide `ResourceLocks`, reference-counted so a lock nothing holds or awaits is not kept). A
container action and a compose deployment hold their **server** — one at a time per server, together with Git
deployments, which hold it from their checkout to `up`: a rebuild starts the replacement under a new id and renames the
old container before it ends, and a deployment's files can lie anywhere (a symlinked directory, a `COMPOSE_FILE`
elsewhere, a Git checkout), so no name, id or path is one resource throughout — the server is. A Git webhook holds its
app's server too, before its webhook is read again; the deployment it starts runs under that lock (`Holding`) and only for the app at the revision whose
server it holds (an app saved since — moved elsewhere — is refused; `GitDeployOwners.RunAsOwnerAsync(revision)`). A server
is locked by its id (`ServerKey`), whichever alias is stored — the id in another case, the display name. The container or plan is resolved again once the lock is held, and
the action gets exactly what was found then (a compose deployment whose project changes again before it writes is
refused). The webhook is read again once the run holds its resource: switched off, removed or
pointed elsewhere meanwhile → refused; taken over → it runs on the new owner. `RunTimeout` bounds the run: a run that
cannot get its resource in time fails; a delivery that ends before its decision (no turn, changed meanwhile, an unknown
action, given up by its worker before or after its start — also a start that landed after its deadline, settled once
whichever way it lands — or a cancellation before the gateway's decision) is recorded as the gateway's Denied event of
its run id on the webhook owner's authority — by the webhook's id, unowned, when the webhook was gone before the worker
read it — within its own bookkeeping time, never the run's deadline (INV-45: every accepted delivery is one event). Not
accepted, so no event: an unknown id, a webhook switched off, an unreadable secret, a signature that does not verify, a
stale timestamp, and a delivery answered `Busy` (queue full, not recordable or not accepted, a repeat still being
recorded or awaiting reconciliation) — nothing was queued and the provider may retry (one residual: an acceptance
that landed with both its answer and read-back lost is answered `Busy` and is still one attempt — taken over and run
by its next delivery in this process, or reconciled once at the next start); a `Duplicate` names the run whose event it is; lookups are abandoned at the deadline (they only read); the run's database work
(its reads and its start) and the action's own start (the owner's authority is looked up synchronously) run on the
thread pool and are waited for only until the deadline — SQLite executes commands
synchronously and does not cancel them — so a locked database never holds a worker past it: the run is given up
(recorded first — through the gateway, so a database outage keeps the event in the audit outbox — then settled failed
and final in one claim-fenced update, tried again until it lands or Whiskers stops — `SettleRetryDelay` 5 s doubling
up to 5 minutes, a new attempt only once the previous one ended, logged at the first failure and then at doubling
intervals; the claim is released once it is settled — a give-up cut off by a stop keeps it until the process ends — so
no later delivery takes it over; its event is keyed by its run id (`NotRunEventKey`), so the give-up, the
reconciliation and the audit outbox's later insert are always one row (the worker's own refusals — its webhook
changed, an unknown action — have a key kind of their own, `RefusedEventKey`, and are reconciled as refused, not
logged again; the reconciliation settles a row it records only once that event is provably on record);
it is then in the webhook's log and sends `automation_refused`, and it needs a new delivery: a new delivery id, or for
GitHub a new push — a GitHub "Redeliver" is a duplicate of it), and a start that still lands later finds it settled
(it never runs). What a stop, a crash or a give-up during a stop leaves queued or running is settled at the next start
(`ReconcileAsync`, called by the module's `InitializeAsync` before `app.Run()`): the audit outbox and then the audit
log are read once per pass for all rows; a row with no event is recorded once as not run (keyed), settled failed and
written to the webhook's log; one with its "did not run" event is settled failed and logged; one with only the
gateway's refusal is settled refused and not logged again (its run did); a running one with its intent on record stays
"interrupted", a queued one is failed. The webhook is read strictly: a read that fails skips the row. One
`automation_refused` notification per pass names the webhooks. A row that cannot be taken or settled is skipped, not
the pass, and passes are retried in the background (`ReconcileRetryDelay` 5 s, doubling up to 5 minutes) until one
completes. There is no age cut-off: retention prunes only finished rows and never-accepted `Recording` rows, so every
left-behind queued or running row is reconciled. A worker busy at a stop takes no further queued run; at the stop the runs no worker took are taken out of the queue and every page Test waiting for one is told at once that Whiskers is stopping. Recording
its outcome (only under the run's own claim, so a run a later delivery took over is never touched) and the hourly
retention pass get a bounded time of their own the same way, and pruning runs beside the workers; the action gets the
cancellation (the gateway, the planner and the host executor honour it); an action that still does not stop keeps its
resource until it ends — nothing overlaps it — but gives its worker back, at most `MaxConcurrentRuns` such runs at once;
beyond that it keeps its worker as well until it ends. With the queue full, a delivery left queued without a live claim is
answered `Busy` (nothing would run it), not as queued. The Test's messages, also the worker's own (`Webhook_Run_*`), are
localized (`Webhook_Test_*`). The run's operation id (R1-18) is its run id (`AutomationActor.OperationId`), so its decision, run,
verification and audit carry it; nested decisions get ids of their own. The page's Test goes through the same path and
waits for its run (`TestRunTimeout`).

## Wiring

This is the opt-in **Webhooks module** ([`../../Modules/Webhooks/`](../../Modules/Webhooks/), toggle
`Features:webhooks:Enabled`): its `ConfigureServices` registers `IWebhookService` and owns the `webhooks` nav
entry. The inbound `POST /api/webhooks/{id}` endpoint stays in Core (`Startup/WhiskersPipelineExtensions.cs`) and resolves the service per
request, so Core keeps the `NoopWebhookService` default above for when the module is off.

## Security notes

- **HMAC is mandatory (F11 / HOCH-12 part 2):** every webhook has a server-generated 256-bit secret; a valid signature by the webhook's provider rule is required (constant-time compared) — `github` (`X-Hub-Signature-256` over the raw body; GitHub/Gitea signing works natively) or `whiskers` (signed timestamp and delivery id, R1-10). Freshness is enforced only where the provider signs a time; for `github` a replay is recognised by its body for the retention period. Webhooks without a secret (legacy rows) are rejected fail-closed AND disabled at boot by the module's `InitializeAsync` (kept, never deleted; the admin gets a `webhook_disabled` notification). The secret is shown exactly once (create/regenerate) and is not retrievable afterwards; re-enabling a secret-less webhook requires regenerating its secret first. The UI test button sends a genuinely signed request through the same validation path (`TriggerSignedTestAsync`). Triggering from the UI requires the Operator role.
- **Owner (R1-09, INV-04):** every action of an external call is decided by the Mutation Gateway on the webhook owner's authority, re-checked at each delivery. The page's Test is decided as the clicking person first and then runs like an external call, so both authorities are explicit. Managing webhooks (`webhook.save` incl. takeover, `webhook.rotate_secret`, `webhook.set_enabled`, `webhook.delete`, Admin) is decided on the stored webhook; the secret is only the result of create / rotate, never a gateway argument.
- **`deploy` action:** the compose project directory (`TargetId`) must be an **absolute path**. The deploy is planned (R1-07, `ComposePlan.FromDirectory`: the directory's default compose file + override, its `.env`, the effective project) and runs with explicit `-p` / `--project-directory` / `-f`; every path is single-quoted. The **Test** button decides each planned container as the clicking person, an external call as the owner; the trigger plans again right before `up` and refuses when that plan touches a container that was not decided.

## Related

- UI: [`../../Components/Pages/Webhooks.razor`](../../Components/Pages/Webhooks.razor)
- Deployment: [`../Deployment/`](../Deployment/)
- Audit (R1-22): every configuration writer (create, rotate, enable, takeover, delete, automatic switch-off) runs under
  one lock and records the webhook's versioned diff ([`../Mutations/ConfigChange.cs`](../Mutations/ConfigChange.cs)) without its trigger counters; the automatic switch-off,
  made after the run's own event, writes its own `webhook.suspended` row with the version before and after.
