# Services/Webhooks

CI/CD **webhook** handling, registers webhook endpoints and processes incoming triggers (e.g. redeploy a container when a pipeline pushes a new image).

## Files

| File | Purpose |
|---|---|
| `IWebhookService.cs` / `WebhookService.cs` | Manages CI/CD webhooks and processes incoming webhook triggers. Generates the mandatory HMAC secret on create, regenerates on demand (one-time display), and runs the signed self-test. R1-09: a valid call runs its action through `IAutomationMutationRunner` on the webhook's `Owner` (`ActorOf`): restart / rebuild as `container.restart` / `container.update`, deploy as `compose.deploy` plus each planned container, Git deploy as `gitdeploy.deploy` and then on the app owner (`GitDeployOwners`). Every delivery is decided, also one without an owner or with a target or plan that cannot be resolved or looked up (audited, refused and reported; the lookup error stays in the log); the action itself runs only with an owner. A refusal for lost authority (or no owner) switches the webhook off (only while its owner is unchanged) and notifies an admin (`automation_suspended`), any other refusal — including the Git app owner's (`GitDeployOwners.RunAsOwnerAsync`) — notifies (`automation_refused`). `SetOwnerAsync` takes a webhook over only while its owner is unchanged. |
| `WebhookTargets.cs` | What a webhook acts on, shared by the Webhooks page's Test and the trigger (R1-06, R1-09): `ServerOf` (a webhook without a server acts on the server with the id `local`, never on "the default server"), `ContainerAsync` (all containers, stopped ones too; exact id or name, else a unique id prefix; an ambiguous prefix is an error) and `ComposePlanAsync` (the deploy plan of the absolute project directory). |
| `NoopWebhookService.cs` | Core default `IWebhookService` for when the **Webhooks module** is off. Keeps the Core `POST /api/webhooks/{id}` endpoint's per-request resolution working; `TriggerAsync` fails gracefully (endpoint → 400, not 500), reads return empty, create throws. Real service wins by last-registration when the module is on (RoadToSAP Phase 1). |

## Wiring

This is the opt-in **Webhooks module** ([`../../Modules/Webhooks/`](../../Modules/Webhooks/), toggle
`Features:webhooks:Enabled`): its `ConfigureServices` registers `IWebhookService` and owns the `webhooks` nav
entry. The inbound `POST /api/webhooks/{id}` endpoint stays in Core (`Program.cs`) and resolves the service per
request, so Core keeps the `NoopWebhookService` default above for when the module is off.

## Security notes

- **HMAC is mandatory (F11 / HOCH-12 part 2):** every webhook has a server-generated 256-bit secret; a valid `X-Hub-Signature-256`-style `sha256=…` signature over the raw body is required (constant-time compared) — GitHub/Gitea/GitLab webhook signing works natively. Webhooks without a secret (legacy rows) are rejected fail-closed AND disabled at boot by the module's `InitializeAsync` (kept, never deleted; the admin gets a `webhook_disabled` notification). The secret is shown exactly once (create/regenerate) and is not retrievable afterwards; re-enabling a secret-less webhook requires regenerating its secret first. The UI test button sends a genuinely signed request through the same validation path (`TriggerSignedTestAsync`). Triggering from the UI requires the Operator role.
- **Owner (R1-09, INV-04):** every action of an external call is decided by the Mutation Gateway on the webhook owner's authority, re-checked at each delivery. The page's Test is decided as the clicking person first and then runs like an external call, so both authorities are explicit. Managing webhooks (`webhook.save` incl. takeover, `webhook.rotate_secret`, `webhook.set_enabled`, `webhook.delete`, Admin) is decided on the stored webhook; the secret is only the result of create / rotate, never a gateway argument.
- **`deploy` action:** the compose project directory (`TargetId`) must be an **absolute path**. The deploy is planned (R1-07, `ComposePlan.FromDirectory`: the directory's default compose file + override, its `.env`, the effective project) and runs with explicit `-p` / `--project-directory` / `-f`; every path is single-quoted. The **Test** button decides each planned container as the clicking person, an external call as the owner; the trigger plans again right before `up` and refuses when that plan touches a container that was not decided.

## Related

- UI: [`../../Components/Pages/Webhooks.razor`](../../Components/Pages/Webhooks.razor)
- Deployment: [`../Deployment/`](../Deployment/)
