# Services/Webhooks

CI/CD **webhook** handling, registers webhook endpoints and processes incoming triggers (e.g. redeploy a container when a pipeline pushes a new image).

## Files

| File | Purpose |
|---|---|
| `IWebhookService.cs` / `WebhookService.cs` | Manages CI/CD webhooks and processes incoming webhook triggers. Generates the mandatory HMAC secret on create, regenerates on demand (one-time display), and runs the signed self-test. R1-09: a valid call runs its action through `IAutomationMutationRunner` on the webhook's `Owner` (`ActorOf`): restart / rebuild as `container.restart` / `container.update`, deploy as `compose.deploy` plus each planned container, Git deploy as `gitdeploy.deploy` and then on the app owner (`GitDeployOwners`). Every delivery is decided, also one without an owner or with a target or plan that cannot be resolved or looked up (audited, refused and reported; the lookup error stays in the log, and a compose planning error — it can quote `.env` values — is replaced by a generic reason everywhere, the page's Test shows it to the person; a plan that changes between the decision and `up` is a reported refusal too; an interpolated project name is decided and audited as its source expression); the action itself runs only with an owner. A refusal for lost authority (or no owner) switches the webhook off (only while its owner is unchanged) and notifies an admin (`automation_suspended`), any other refusal — including the Git app owner's (`GitDeployOwners.RunAsOwnerAsync`) — notifies (`automation_refused`). `SetOwnerAsync` takes a webhook over only while its owner is unchanged. |
| `WebhookSignatures.cs` | R1-10 (INV-42): the provider rules and `WebhookRequest`. **github** — `X-Hub-Signature-256` over the body; GitHub signs no time and no delivery id (`X-GitHub-Delivery` is ignored), so a delivery is identified by the hash of its signed body and is recognised as a repeat for `DeliveryRetention`, not by its age. **whiskers** — `X-Whiskers-Timestamp` (Unix seconds), `X-Whiskers-Delivery` (8–128 chars `[A-Za-z0-9._-]`) and `X-Whiskers-Signature: sha256=HMAC(secret, "{timestamp}.{delivery}.{body}")`; the signature is checked first, then the timestamp against `TimestampTolerance` (both directions); the signed delivery id identifies it. Constant-time comparison. `SignedTest` builds the page's Test delivery for either. |
| `WebhookDeliveries.cs` | R1-10: `WebhookReply` / `WebhookAcceptance` and the delivery store (table `WebhookDeliveries`, unique per webhook and delivery key). `RecordAsync` inserts once (a claim token recognises a retried insert); a key on record answers with its run and state; a row still `Queued` by a stopped process is taken over by exactly one later delivery (it never ran); a `Running` row of another process reads as `interrupted` and does not run again. `StartAsync` / `FinishAsync` move a run Queued → Running → Succeeded / Failed / Refused; `ForgetAsync` drops a delivery that could not be queued; `PruneAsync` removes finished keys after `DeliveryRetention`. |
| `WebhookOptions.cs` | R1-10: section `Webhooks` — `MaxBodyBytes` (1 MiB), `RequestsPerMinute` (300, all webhooks), `RequestsPerMinutePerWebhook` (30), `QueueCapacity` (100), `MaxConcurrentRuns` (4), `TimestampTolerance` (5 min), `DeliveryRetention` (90 days), `TestRunTimeout` (10 min). |
| `WebhookEndpoint.cs` | R1-10: `POST /api/webhooks/{id}` (mapped in the pipeline, anonymous). Reads at most `MaxBodyBytes` with the request's cancellation, then answers at once: 202 `queued` + `runId`, 200 `duplicate` + `runId` + `state`, 400 `rejected` for every invalid request (unknown, disabled, bad signature, stale, too large — no detail), 429 / 503 with `Retry-After`. |
| `WebhookTargets.cs` | What a webhook acts on, shared by the Webhooks page's Test and the trigger (R1-06, R1-09): `ServerOf` (a webhook without a server acts on the server with the id `local`, never on "the default server"), `ContainerAsync` (all containers, stopped ones too; exact id or name, else a unique id prefix; an ambiguous prefix is an error) and `ComposePlanAsync` (the deploy plan of the absolute project directory). |
| `NoopWebhookService.cs` | Core default `IWebhookService` for when the **Webhooks module** is off. Keeps the Core `POST /api/webhooks/{id}` endpoint's per-request resolution working; `AcceptAsync` rejects every delivery (endpoint → 400, not 500), reads return empty, create throws. Real service wins by last-registration when the module is on (RoadToSAP Phase 1). |

## Delivery (R1-10, INV-42)

`AcceptAsync`: global rate → size → webhook lookup → per-webhook rate → enabled → provider rule → `RecordAsync` →
bounded queue. A new delivery is answered at once with its run id; a repeat (concurrent, later, after a restart) with
the first run's id and state. Workers (`MaxConcurrentRuns`, started with the first delivery, stopped with the app) run
each delivery once, holding a lock per target (`TargetKey`: server + container / directory, or the Git app), so runs on
one resource never overlap whichever webhook fires them. A webhook switched off or removed while its delivery waited is
refused, not run. The run's operation id (R1-18) is its run id (`AutomationActor.OperationId`), so its decision, run,
verification and audit carry it; nested decisions get ids of their own. The page's Test goes through the same path and
waits for its run (`TestRunTimeout`).

## Wiring

This is the opt-in **Webhooks module** ([`../../Modules/Webhooks/`](../../Modules/Webhooks/), toggle
`Features:webhooks:Enabled`): its `ConfigureServices` registers `IWebhookService` and owns the `webhooks` nav
entry. The inbound `POST /api/webhooks/{id}` endpoint stays in Core (`Program.cs`) and resolves the service per
request, so Core keeps the `NoopWebhookService` default above for when the module is off.

## Security notes

- **HMAC is mandatory (F11 / HOCH-12 part 2):** every webhook has a server-generated 256-bit secret; a valid signature by the webhook's provider rule is required (constant-time compared) — `github` (`X-Hub-Signature-256` over the raw body; GitHub/Gitea signing works natively) or `whiskers` (signed timestamp and delivery id, R1-10). Freshness is enforced only where the provider signs a time; for `github` a replay is recognised by its body for the retention period. Webhooks without a secret (legacy rows) are rejected fail-closed AND disabled at boot by the module's `InitializeAsync` (kept, never deleted; the admin gets a `webhook_disabled` notification). The secret is shown exactly once (create/regenerate) and is not retrievable afterwards; re-enabling a secret-less webhook requires regenerating its secret first. The UI test button sends a genuinely signed request through the same validation path (`TriggerSignedTestAsync`). Triggering from the UI requires the Operator role.
- **Owner (R1-09, INV-04):** every action of an external call is decided by the Mutation Gateway on the webhook owner's authority, re-checked at each delivery. The page's Test is decided as the clicking person first and then runs like an external call, so both authorities are explicit. Managing webhooks (`webhook.save` incl. takeover, `webhook.rotate_secret`, `webhook.set_enabled`, `webhook.delete`, Admin) is decided on the stored webhook; the secret is only the result of create / rotate, never a gateway argument.
- **`deploy` action:** the compose project directory (`TargetId`) must be an **absolute path**. The deploy is planned (R1-07, `ComposePlan.FromDirectory`: the directory's default compose file + override, its `.env`, the effective project) and runs with explicit `-p` / `--project-directory` / `-f`; every path is single-quoted. The **Test** button decides each planned container as the clicking person, an external call as the owner; the trigger plans again right before `up` and refuses when that plan touches a container that was not decided.

## Related

- UI: [`../../Components/Pages/Webhooks.razor`](../../Components/Pages/Webhooks.razor)
- Deployment: [`../Deployment/`](../Deployment/)
