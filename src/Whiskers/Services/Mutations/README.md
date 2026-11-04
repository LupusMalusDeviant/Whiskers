# Services/Mutations

The **Mutation Gateway** ([ADR-0005](../../../../docs/adr/0005-mutation-gateway.md)): the one path every
infrastructure mutation is meant to take. An entry point describes the mutation as a `MutationRequest`; the gateway
decides, executes the supplied delegate, verifies and records.

**State: WP2 step 2.3.** MCP tools, the agent, the UI and unattended automation run every mutation through the
gateway:

- direct MCP calls in [`McpGuardrailFilter`](../../Mcp/McpGuardrailFilter.cs) (surface `Mcp`), which also reports each call's outcome to the call history (R1-20);
- agent tool calls in [`AgentToolInvoker`](../Agent/AgentToolInvoker.cs) (surface `Agent`, the principal acting
  `OnBehalfOf` the user, key or trigger that started it);
- UI handlers through [`IUiMutationRunner`](UiMutationRunner.cs) (surface `Ui`, the signed-in user as a `Human`
  principal);
- scheduled tasks, AutoUpdate, AutoPatch and the VPN bring-up at start (R1-43, `vpn.bootstrap`) through
  [`IAutomationMutationRunner`](AutomationMutationRunner.cs) (surface `Automation`, an `Automation` principal acting
  `OnBehalfOf` the recorded owner).

A tool counts as mutating when the catalog lists it (`ICapabilityCatalog.FindByMcpTool`). Webhooks and Git deploy
(2.4) run through the gateway since R1-08 / R1-09, terminal opens (2.5, `terminal.container_open` / `terminal.host_open`)
since R1-17; step 2.6 makes bypassing the gateway fail. Brokkr provisioning (contract 0001) gives
`gitdeploy.save`, `webhook.save` and `webhook.rotate_secret` an MCP tool each and adds `gitdeploy.set_token`
(`set_git_deploy_token`); one MCP tool per capability, so the UI keeps running these by capability id. A new
`webhook.save` called with an `appId` resolves its target through that Git app (its server, its name and repository).
Because the tool switch applies to its capability on every surface (INV-15), switching off `create_git_deploy_app`,
`create_git_deploy_webhook` or `rotate_git_deploy_webhook_secret` in Settings → MCP also stops saving Git apps,
creating or taking over webhooks and regenerating webhook secrets on the pages; a stale `revision` on a Git app is
refused as `TargetChanged`.

### UI handlers

```csharp
await Mutations.RunAsync("network.create", new { name, driver, serverId = sid },
    _ => DockerService.CreateNetworkAsync(name, driver, sid));
```

The capability id is a string literal and the service call a lambda written in that call: the architecture test
recognises a gated UI call by exactly that shape. The arguments name the target the way MCP tools do
(`containerId`, `serverId`, `name`, …) and never carry secrets. A refusal throws `MutationRefusedException` (the page
shows its message as a warning); an exception of the service call itself reaches the page unchanged. The page keeps
its own role check and audit row.

For a human in the UI the policy applies only the rules PC-D1 names — authorization, the kill switch, protected
resources (`GuardrailEngine.CreateForHumans`). Tool lists, argument patterns and confirmation are about delegating to
the agent or a key; a person's click is not sent for approval.

Security configuration (R1-11) is Admin-only with target kind `config` (the `section` argument names it):
`config.save`, `config.whitelist`, `config.roles`, `config.mcp_permissions`, `config.agent` (agent provider,
key and system prompt), `mcp.key_create`, `mcp.key_delete`,
`guardrails.save`, `guardrails.kill_switch_off`, `vault.secret_set`, `vault.secret_delete`, `registry.save`,
`registry.delete`. `GuardrailMutationPolicy` makes one narrow exception, for a person in the UI only and after
authorization: `guardrails.kill_switch_off` is evaluated with `ReadOnlyMode` off (every other rule applies). A
fail-closed store (`LoadError`) has none: it is repaired on the host (fix `guardrails.json`, or remove it together with `guardrails.json.bak`, restart). `UiMutationGatewayCoverageTests` lists every configuration write.

### Unattended automation (PC-D6)

```csharp
var outcome = await _automation.RunAsync(new AutomationActor("task:" + task.TaskId, $"Scheduled task '{task.Name}'",
        task.CreatedBy), "container.restart", new { containerId, serverId = task.ServerId },
    _ => _docker.RestartContainerAsync(containerId, task.ServerId));
```

Same shape as the UI runner (literal capability id, lambda in the call), but the result is a `MutationOutcome` —
a background loop has nobody to show an exception to. The owner id is `user:<email>` or `key:<id>`
(`AutomationOwners.IdOf`, see [Principal identity](#principal-identity)); anything else is suspended; `IAutomationOwnerResolver` reads its authority **on every run** (INV-04): a user's role
only while the whitelist still lets them sign in, a key's level only while it exists and is enabled — otherwise
`read`, so the run is refused. The owner's level is the ceiling; the policy applies the PC-D1 rules (authorization,
kill switch, protected resources) without asking for approval — the owner approved by scheduling.

Where the owner is kept: scheduled tasks in `ScheduledTaskEntity.CreatedBy` (set by the UI and `create_scheduled_task`;
taken over in Scheduled Tasks), AutoPatch, AutoUpdate and the VPN bring-up in [`IAutomationOwnerStore`](AutomationOwnerStore.cs)
(`automation-owners.json`, taken over in Settings → Automation owners). AutoPatch skips its night without an owner; the
VPN bring-up (R1-43) does not run without one either — never in the grace period — and records that refusal as the
gateway's Denied event (`IAutomationMutationRunner.RefuseAsync`, with `read` authority).

**Owners are required (R1-04).** `Automation:RequireOwner` defaults to `true`: an item without an owner is refused.
The grace period stays available when set explicitly to `false`: an item without an owner (created before 2.3) then
runs as `unowned:<runner>` with exactly the level of the capability it runs, audited as "(no owner)".

**Authority loss suspends (PC-D6, R1-04).** `IAutomationMutationRunner.LostAuthority` says whether an item can no
longer run on its owner's authority at all: no owner while owners are required, an owner whose local account was
deleted (`IOwnerAccounts`, with local-only and with mixed sign-in), who was removed from the whitelist, whose
key was deleted or disabled, or whose level no longer covers the capability. A run refused for that reason
**suspends** the item — a scheduled task is disabled, AutoPatch / AutoUpdate are blocked in the owner store
(`SuspendAsync`, kept across restarts) — and sends an `automation_suspended` notification. A scheduled task is checked for every
capability its run was refused (a backup whose retention deletes need more than the backup itself), and AutoPatch
stops and suspends when its owner loses authority half-way through a cycle. `automation_suspended` and
`automation_refused` are administrative events: no container mute hides them. A refusal by policy
(kill switch, protected resource, forbidden argument) only reports `automation_refused` and leaves the item on.
Taking an item over records the old and the new authority (`AutomationOwners.AuthorityOf`) in the audit row.

**Agent-created items keep the agent's rules (R1-04).** A task whose `CreatedVia` / `DelegatedBy` is an agent
session is evaluated with the agent rule set (autonomy level and confirmation, tool lists, forbidden arguments), not
the PC-D1 rules for a person: scheduling never turns an agent's action into one the user authorised by hand. An
unattended `Confirm` is refused (R1-01).

## Principal identity

R1-03 (INV-01, INV-06). Every principal carries a stable id built from typed fields, never from a display name:

| Principal | Id | Credential | On behalf of |
|---|---|---|---|
| Web user | `user:<email>` (trimmed, lower-case) | — | — |
| API key | `key:<key id>` (a rename keeps it) | key id | — |
| AI trigger | `trigger:<trigger id>` (each trigger its own) | — | — |
| Agent | `agent:<session id>`, `SessionId` set | the source's key id | the user, key or trigger it acts for (same session id) |
| Automation | `task:<id>`, `autoupdate`, `autopatch` | — | the owner (`user:` / `key:`), re-resolved per run; `DelegatedBy` = the id of the call that created the task (e.g. `agent:<session>`, stored as `ScheduledTasks.CreatedVia`), provenance only |

`MutationPrincipals.Of` / `.Agent` build them for the MCP filter and the agent; `MutationPrincipals.ToAgent` maps back
for the guardrail engine. A principal without an email or key id is `anonymous`: it can be checked, never own
anything. Owners recorded before the scheme were bare emails: the `PrincipalIdsForTaskOwners` database migration and
`AutomationOwnerStore` (on load) rewrite exactly those to `user:<email>` (`AutomationOwners.MigrateLegacy`). Any other
value (anything but one ASCII email, the same rule in SQL and in C#) is ambiguous, stays as it is and resolves as suspended (`read`) until an Admin takes the item over. The MCP call
log records the same ids; the audit log keeps display names in `Actor` and the ids in `Details.principal` (incl.
`session`, `onBehalfOf` and `delegatedBy`). The UI shows an owner through `IAutomationOwnerResolver.Describe` (name +
`OwnerState`, localized as `AutomationOwners_State_*`).

## Files

| File | Purpose |
|---|---|
| `VerifierSupport.cs` | R1-37: what the verifiers share — call arguments and answers read the same for every surface (an MCP `CallToolResult` or a page's value), host reads where a command that could not run (exit -1) fails the read-back with the state unknown, polling for effects a provider applies later (bounded by a budget within the deadline), and `ServiceVerifier` (a module that is off → `NotVerifiable`). |
| `MutationVerifiers.cs` | R1-36/37: the factories of every registered verifier (one per `ReadBack` contract); the app registers them, the tests build the same list. |
| `VerificationContracts.cs` | R1-37 (INV-43): the verification contract of every catalog capability — `ReadBack` (what is read) or `NotVerifiable` with the reason (Whiskers' own records, arbitrary commands and queries, effects that end where they started, nested-only capabilities). `ReadBackVerificationTests.Every_capability_declares_its_verification_contract` holds the catalog, the contracts and the registered verifiers together. |
| `ResourceLocks.cs` | R1-10 (INV-42): one lock per resource key for the whole process (a Core singleton). Webhook container actions, webhook compose deployments and Git deployments (from checkout to `up`) all take `Server(serverId)` — no name, id or path identifies a container or a deployment's files throughout (a rebuild changes the id; symlinks, `COMPOSE_FILE`, checkouts), the server does. `ServerKey` resolves a stored id or alias (another case, the display name) to the server's id, as execution does, so every alias is one lock. `Holding(key)` marks the holder's flow: work it starts (a Git deployment a webhook delivery runs) does not wait for its own lock. Reference-counted: a key exists only while it is held or awaited. |
| `MutationAttributes.cs` | `[MutatingOperation("<area>.<verb>")]` on service-interface methods that change infrastructure; `[MutationPrimitive]` (R1-48) on a generic execution method whose capability lies in its arguments (`IHostCommandExecutor.ExecuteMutationAsync`) — it needs an open scope whose capability's execution is a raw host command (`MutationCompositions.RawHostCommands`), and its callers are pinned by `MutationGatewayCoverageTests`; `[MutationHandler("<capability>")]` on a class whose public methods run only inside a decision for that capability (R1-48: it names the capability, and its calls are checked against it — no blanket exemption); `[MutationEntryPoint]` on the runner methods whose lambda argument the architecture test counts as gated. |
| `MutationCompositions.cs` | R1-48: the explicit table of nested operations — which capabilities a decided operation may run inside its own scope (`app.deploy` → `image.pull` + `container.create`, `compose.deploy` → `app.deploy`, `server.onboard` → `certificate.issue_mtls` + `server.save`, `workload.*` → `container.*`, `image.assess_update` → `image.pull`, vault writes of the stores that keep secrets, `nginx.*` → `nginx.reload`, `dns.record_set` → `dns.record_delete`, `cloud.hard_reset` → `cloud.reboot`, `account.create` / `account.delete` → `config.roles`, …). `Allows(outer, inner)` is the transitive closure. `RawHostCommands` lists the capabilities whose execution is a raw host command — the only scopes the host primitive runs in. The runtime guard and the architecture test both read it; a composed capability is never above the composing one's level (`MutationGatewayCoverageTests`). |
| `MutationModels.cs` | `Capability`, `CapabilityRisk`, `MutationPrincipal` (incl. `OnBehalfOf`, `AllowedTools`), `MutationSurface`, `MutationTarget`, `MutationRequest` (incl. `AgentOrigin`, R1-15: set by Whiskers from the agent session, it makes a gateway approval for a web-chat call interactive), `MutationOutcome<T>` (incl. `ApprovalId`, `MatchedRules`), `MutationContext`. |
| `CapabilityCatalog.cs` | `ICapabilityCatalog` — the single table of mutating capabilities and their levels (INV-13, R1-05). `McpPermissionLevels.DefaultToolLevels` takes its mutating entries from it; `RoleFor` maps a level to the UI role pages check (`CapabilityTableConsistencyTests` holds hard-coded page roles to it). R1-49 adds the local-account capabilities (target kind `account`, id argument `account` = the email): `account.create`, `account.password_reset`, `account.delete` (Admin) and `account.password_change` — the one read-level capability (every signed-in user, own account only; the service refuses anyone else's), which the kill switch therefore lets through. |
| `CapabilityOverrides.cs` | `ICapabilityOverrides` + `McpToolCapabilityOverrides`: the MCP tool's Enabled switch and a stricter stored level (Settings → MCP), applied for **every** principal kind (INV-15). The catalog level is the floor. |
| `MutationTargets.cs` | `MutationTargets.FromArguments` (target from tool arguments), `MutationTargets.CanonicalArguments` (the arguments a tool runs with: the target's key and `serverId` replaced by the resolved identity, keys the caller did not pass stay absent) and `IMutationTargetResolver` + `MutationTargetResolver` (R1-06, INV-24/57): the server first (empty = the default server; exact id, else one id / name ignoring case), then containers (id, name or unique prefix), networks (id, name or unique prefix; `Related` = the named container for `network.connect` / `network.disconnect`, which require one, and the attached containers for anything else — decided by the capability, never by which arguments the call carries), cloud servers (Whiskers id or name), scheduled tasks (id or unique name), DNS zones (normalised, within `Dns:AllowedZones`) and Kubernetes workloads (the pod's **live** controller as `Name`). Since R1-13 the resolver also lists the target's other inventory names (`Aliases`: a workload's pod, a Git app's stored name and repository, a webhook's name) and every other resource the operation touches, one alias group each (`RelatedResources`: each attached or named container, each container a compose plan recreates, a webhook's target). Unknown or ambiguous targets are refused before policy. Creating capabilities (`network.create`, `scheduler.create`) resolve only the server; `server.onboard` / `server.save` match only that server by its own id (exact, else ignoring case — never the default server or a name) and pass a new one through; volumes, log alerts and the Whiskers-wide kinds pass through. |
| `MutationGateway.cs` | `IMutationGateway` + `MutationGateway` (the pipeline) and `IMutationVerifier`. R1-36 (INV-43): a verifier reads the effect back after the call returned; a read-back that does not show it (or cannot be made) turns the outcome into `Failed` with verification `Failed`, the value withheld and `MutationVerificationFailedException` as its error — so no page, agent, MCP client or runner reads it as a success. `StateUnknown` on that exception says the read-back could not be made at all (unproven, not disproven), so a caller that would undo the call (automatic rollback) does not act on it. A call that threw is not verified (`NotRun`). R1-37: a capability without a verifier records `NotVerifiable` with the reason its contract gives, never success; a verifier that cannot read this call back throws `MutationNotVerifiableException` (`NotVerifiable` + reason), and a verifier answering `NotRun` counts as `NotVerifiable`. The reason is `MutationOutcome.VerificationNote` and the audit row's reason ("Not verifiable: …"). R1-53: the gateway logs one line per decision (operation, capability, surface, verdict, decision id, `HttpRequestId`), one per refusal or wait for approval (status, approval, decision — on every path that ends an operation without running it: a policy refusal, a pending approval, an audit intent that could not be written, a duplicate refused at the claim, an approval no longer usable) and one per outcome (status, verification, approval, audit row id; `Warning` when it failed) — the reasons stay in the audit row — and holds the log scope `OperationId`/`Capability` from the decision until the outcome is written, so the services the execution calls log under it. `RequestCorrelation.CurrentOrNone` names the request (`none` for background work). |
| `AutomationMutationRunner.cs` | `IAutomationMutationRunner` + `AutomationMutationRunner`, `AutomationActor`, `AutomationOptions` (`RequireOwner`), `IAutomationOwnerResolver` + `AutomationOwnerResolver`, `AutomationOwners.IdOf`: the automation entry to the gateway. `AutomationActor.OperationId` (R1-10) gives a run an operation id of its own (a webhook delivery's run id); otherwise the gateway assigns one per call. `RefuseAsync` (R1-43) records a run the item refused itself before the gateway (no owner, blocked) as one Denied event; an ownerless item is recorded with `read`, never the grace level. |
| `PrincipalIdentity.cs` | `PrincipalIds` (the stable id scheme `user:` / `key:` / `trigger:` / `agent:<session>`) and `MutationPrincipals` (gateway principal from an `AgentPrincipal` and back; the agent principal is the session acting on behalf of its user, key or trigger). |
| `OwnerAccounts.cs` | `IOwnerAccounts` + `OwnerAccounts`: whether a user owner still has a way in (R1-04). Local-only sign-in needs the Identity row. With Google/OIDC as well, `known-local-accounts.json` records every email seen with a local account (at startup and when the wizard creates one) and every email seen signed in federated: no local row → *Deleted* if it had one, *Present* if seen federated, otherwise *Unverified* (refused, not suspended, until the owner signs in or an admin takes over). Missing or unreadable record → unverified (fail closed). |
| `FederatedSignInRecorder.cs` | Blazor `CircuitHandler` that records a federated sign-in (`IOwnerAccounts.RememberFederated`) from the circuit's authentication state; only observes, never touches the sign-in itself (R1-04). |
| `AutomationOwnerStore.cs` | `IAutomationOwnerStore` + `AutomationOwnerStore`: owners of AutoPatch / AutoUpdate / the VPN bring-up (`vpnbootstrap`, R1-43) in `automation-owners.json`, written atomically; an unreadable file blocks every feature until each is taken over. |
| `ComposePlan.cs` | R1-07: `ComposeDeployment` (what is asked: server, files or directory, project directory, explicit project, new `.env`, `--remove-orphans`), `ComposePlan` (the effective project, files, services and `Impact` — every existing container recreated, removed as orphan or colliding by name; `UpCommand` / `BuildCommand` / `DeployScript` with explicit `-p`, `--project-directory`, `-f`, `--env-file`; `EnsureDecided`), `ComposeModel` (pure: project name precedence and normalisation, `.env` parsing, interpolation, file merging, impact) and `ComposePlanGate`. Anything that cannot be worked out throws `ComposePlanException` (fail closed). |
| `ComposePlanner.cs` | `IComposePlanner` + `ComposePlanner` (singleton): reads what the deployment does not bring along from the host (a directory's default compose file + override, the existing `.env`, a container's `config_files` / working directory for a `.env` change) and lists the server's containers. Read-only. |
| `ComposeImpact.cs` | UI side of a plan: `DeployAsync` (plan, decide the capability, then each planned container nested, plan again and run it), `GateFor` (a `ComposePlanGate` for plans made inside an operation, e.g. Git deploy) and `RunForEachAsync`. |
| `UiMutationRunner.cs` | `IUiMutationRunner` + `UiMutationRunner` (scoped, per circuit), `MutationRefusedException`, `MutationFailedException`: the UI's entry to the gateway. A refusal with a `ReasonCode` (`MutationReasonCodes`, R1-05) is shown in the user's language (`Mutation_Refused_<code>`); the audit keeps the English `Reason`. `RefuseAsync` records a refusal the page made itself (its role check) as the gateway's Denied event (R1-21). R1-26: every call re-checks the session through `ICurrentUserService.CheckSessionAsync` (never the role the page cached); a revoked session is refused through `IMutationGateway.RefuseAsync` (`Session_Revoked_Refusal`) and nothing runs, a lowered role decides the level. |
| `MutationPolicy.cs` | `IMutationPolicy` + `GuardrailMutationPolicy`, the adapter onto the core guardrail engine. Its `MutationPolicyDecision.Policy` names the preset and policy version the verdict came from (R1-12). For an agent it evaluates the principal the agent acts for, with that key's tool list; for a human in the UI the PC-D1 rule set. The rules also see the resolved target (`__resolvedTargetId`, `__resolvedTargetName`, `__resolvedRelated<n>` — related names, aliases, related resources and a pod's container), so a protected container is caught by id prefix or another name, as a container attached to a removed network, and a workload by its live controller. A protected-resource hit raises its approval at Admin level (R1-16). Without engine or store it refuses everything. |
| `PolicyDecisions.cs` | R1-12 (INV-22/23): `PolicySnapshot` (preset + the exact policy in canonical JSON + its SHA-256 = the policy version), `PolicyCanonicalJson` (keys sorted at every level; `InputHash` of a call's arguments), `EffectivePolicy` (live active preset, or a pinned preset with the global kill switch; a fail-closed store → fail-closed), `PolicyDecisionRecord`, `IPolicyDecisionStore` + `DbPolicyDecisionStore` (decision and, once per version, its snapshot in one save; versions are never rewritten). `PinnedPolicy` itself lives with `AgentContext` in `Models/Agent`. |
| `MutationAudit.cs` | `IMutationAuditSink` + `AuditLogMutationSink`: writes the intent (with the approval id and approver) before execution and turns the same `AuditLog` row into the outcome. Unlike `AuditLogService`, it throws on a failed write. R1-23: the intent goes through `IAuditChain.WriteAsync`, the outcome through `IAuditChain.UpdateAsync` — the row is locked and checked against its newest chain link first, and a row changed in the database is refused (no outcome is written over it; recovery then leaves it too). R1-19 (INV-45): the security fields are columns of their own — `EventId` (unique: an insert retried after a lost commit answer finds its row instead of adding one), `OperationId`, `ParentOperationId`, `DecisionId`, `TriggerId`, `Surface`, `PrincipalKind`/`PrincipalId`/`CredentialId`/`PrincipalSessionId`/`OnBehalfOfId`/`OnBehalfOfKind`/`DelegatedBy`, `SessionId`, `ApprovalId`/`ApprovedBy`, `Phase` (`intent` → `outcome`), `Outcome`, `Verification`, `DurationMs`, and the redacted `Arguments` in full (payload values withheld, length kept) — so shortening the capped `Details` can never drop one; a pending refusal names the approval it waits for (`RecordDeniedAsync(…, approvalId, …)`). A refusal whose insert fails — also an intent whose insert fails, kept as the gateway's refusal under the same event id — is kept in the `AuditOutbox` before the sink throws. The arguments are redacted value by value (`RedactedArguments`: payloads keep their length, a secret-named key at any depth `***` with everything below it, every other string on its own), so no pattern can run across the document. The sink also knows the intents this process is writing right now (`InFlight`, until their outcome write ended — written or failed). |
| `AuditOutbox.cs` | R1-19: the durable fallback for a refusal (it has no intent row): the complete redacted event as `audit-outbox/<EventId>.json`, inserted later by `AuditRecovery` (idempotent by `EventId`, so a file whose event is already in the table is just removed). An event that cannot be kept either is counted as lost. |
| `AuditRecovery.cs` | R1-19: `IAuditRecovery` + `AuditRecovery` (a background loop, every minute). An outcome whose write failed is kept in memory and written again; the outbox is drained; `intent` rows are settled from the operation journal — `Executed`/`Failed` as recorded, `Unknown` when it was running when Whiskers stopped, `Denied` when it never ran — and an intent this process is writing right now (`AuditLogMutationSink.InFlight`) is left alone; a settlement goes through `IAuditChain.UpdateAsync` (R1-23: never over a row that no longer matches its newest chain link — such a row stays unsettled, so the check stays Degraded) and only updates a row that is still `intent` and whose journal entry is still what the pass read (never an outcome the gateway wrote meanwhile, never on a picture that changed); one claimed by this process that is no longer in flight (its journal finish failed) stays and counts as unsettled. `Health` (pending outcomes, outbox and lost events, unsettled intents — unknown, so incomplete, until the first pass —, a lost `AuditLogService` row in the last hour) feeds the `audit` readiness check (Degraded). |
| `TriggerAuthority.cs` | R1-13: `ITriggerAuthority` (the gateway's per-mutation question "may this AI trigger do this now?", implemented by the Agent module's `AiTriggerAuthority`) and `TriggerAuthority` (`TriggerOf(principal)`, `GlobMatch`). |
| `OperationJournal.cs` | R1-18 (INV-39): `IOperationJournal` + `DbOperationJournal` (table `Operations`). `ClaimAsync` claims an operation id before it runs — `Claimed`, or `AlreadyRunning` (this process runs it now), `AlreadyRun` (executed or failed), `OutcomeUnknown` (a `Running` row of another process: a restart cut it off; never replayed), `Mismatch` (the id with other inputs: `InputHashOf` = principal, capability and arguments without the gateway's `__…` keys). A claim token (process + claim) lets a retried insert recognise its own row, and only that token finishes the claim or gives it back — a retried release never frees a later claim. `InspectAsync` answers the same question without claiming (before the approval gate). `FinishAsync` records `Executed` / `Failed` and the verification; `ReleaseAsync` gives a claim back when the call is refused after it (nothing ran) — exactly one later claimer can take it. Rows are never deleted. |
| `ShutdownDrain.cs` | R1-39 (F-16): `IShutdownDrain` + `ShutdownDrain` (an `IHostedLifecycleService`). `TryEnter` registers one piece of unattended mutating work (a scheduled task run, one AutoUpdate or AutoPatch container) and answers `null` once stopping has begun — the caller then starts nothing. `StoppingAsync` (before the host cancels the background loops) stops admitting and waits for what runs, at most `Budget` (30 s), then cancels `Abort`; what is still running is cut off by the stop and reconciled at the next start. The host `ShutdownTimeout` is raised to 45 s; Compose `stop_grace_period` / Helm `terminationGracePeriodSeconds` are 60 s. `NoShutdownDrain` for code built outside the host. |
| `ProcessIdentity.cs` | R1-39: `ProcessIdentity.Current`, a fresh id per Whiskers process, written on every run and update put on record before it starts; the reconciler tells this process's open rows from cut-off ones by it. |
| `InterruptedWorkReconciler.cs` | R1-39: one pass at start. Journal rows still `Running` under another process become `Interrupted` (conditional on the row as read; a retry stays `OutcomeUnknown`, `AuditRecovery` settles the intent as `Unknown`); task runs without an end of another process — `TaskRunHistory.ProcessInstance` is not this process's `ProcessIdentity.Current`, or is null (written before identities existed); decided by identity, never by the clock — become failed runs with `InterruptedResult` (also the task's last result unless a later run recorded one). Nothing is run again. One `operations_interrupted` notification (administrative, never muted) names up to 20 of them. AutoUpdate attempts without an end (`UpdateHistory.CompletedAt` null) of another process (`UpdateHistory.ProcessInstance`, same rule) become failed updates with `InterruptedResult`, and the matching update policy is switched off (`InterruptedUpdateResult`) until an admin switches it on again. Every update is conditional on the row as read (`ExecuteUpdate`); a task's last result only while its `LastRun` is still that run's start. A `FleetBackgroundService`. An attempt is closed in one transaction with switching its policy off, a task run in one transaction with its task's last result; each transaction runs inside the provider's execution strategy (PostgreSQL's `EnableRetryOnFailure` refuses one opened outside it), and a retry after a lost commit still reports the work. Every mark commits on its own: when a later step fails, what was marked before is still logged and notified, the failure is raised after that, and what stayed open is marked by the next start. As `IStartupReconciliation` (`IStartupReconciliation.cs`) its `Completed` ends after the pass, also a failed one; AutoUpdate waits for it before its first check. |
| `MutationScope.cs` | The ambient (`AsyncLocal`) scope the gateway opens around a delegate; nested service calls run inside it. `MutationScope.Fail(reason)` lets code that answers with an error text instead of throwing (most MCP tools) mark the operation as failed. `MutationScope.Record(result)` (R1-20, through `IToolAuditLog`) adds what the tool reports about its work — at most ten `MutationDomainResult`s — to the gateway's outcome (`results` in its details, redacted); a reported failure also fails the operation. `Covers(serverId, identities)` says whether this or an enclosing decision saw a resource (target, related resources, argument values) on that server — used by `ComposePlan.EnsureDecided` (R1-07). `PinnedPolicy` and `TriggerId` (R1-13) are inherited by nested scopes. R1-37: `ReadBack(key, value)` tells the capability's verifier what the call really acted on when its arguments do not say it (in memory only, never audited; the gateway hands it over as `MutationContext.ReadBackFacts`) — e.g. `ComposeImpact.ReportProject`, the compose project a plan resolved and the services its files define (a top-level `name:` included; an interpolated name is reported empty and the deployment is then not verifiable). `ApprovedBy` (contract 0001): the person (`user:<email>`) whose approval this run used, this scope only; `AutomationOwners.PersonFor` turns it (or a calling person) into the owner of what the Brokkr provisioning tools set up — never the key. |
| `MutationScopeGuard.cs` | `MutationScopeProxy<T>` (a `DispatchProxy` around each service interface with marked methods), `IMutationScopeMonitor` and `MutatingMethods`. **Enforced since R1-48:** a marked call needs an open scope whose capability is the call's own or composes it (`MutationScope.Authorizes`, `MutationCompositions`) — and a call that names its server (`string serverId`) and is allowed only through a composition must name the server the composing decision was taken for (an `app.deploy` on A cannot pull on B; a decision without a server target, an operation without a `serverId` parameter and the decided capability itself are not matched by server). A call that omits its `serverId` acts where its operation routes an omitted server, declared once with `[OmittedServer]` (`OmittedServerAttribute.cs`: the configured default, or a fixed id such as `OmittedServer.Local`; implementations with a fixed fallback read it through `OmittedServer.For<T>`, so guard and implementation cannot drift; every guarded operation with an omittable `serverId` must declare it, test-enforced). `IMutationScopeMonitor.ResolveServer` resolves the call's and the decision's server through the registry to canonical ids; an unknown server, an undeclared omission, a missing or failing registry and an unresolvable decision server match nothing (review rounds 2 and 3); a primitive needs a scope of a `RawHostCommands` capability. Otherwise `MutationScopeViolationException` is thrown before the call runs and the sighting is logged at Error. A closed scope authorizes nothing: `MutationScope.Current` skips a scope (or a parent) that was disposed, so work that escaped it (`Task.Run`, a timer, fire-and-forget) is refused, and a scope begun there does not nest under the dead one; a nested scope begun from escaped work while the outer one was still open stops being authorized when the outer one closes (fail closed, not atomic), so background work started from code that can run in a decision suppresses the flow (`ExecutionContext.SuppressFlow`, test-enforced in `MutationGatewayCoverageTests`). No capability is only recorded: every one is refused outside a decision (the first-run wizard uses the narrow first-admin methods of the role and whitelist services instead, see below). `MutationScopeMonitor.ReportOnly` exists for tests. The guard implements only the guarded interface; `IMutationScopeProxy.Inner` gives the instance behind it (used by `AddInitializable` for startup warm-up). |

Registration: [`../../Startup/WhiskersMutationGatewayExtensions.cs`](../../Startup/WhiskersMutationGatewayExtensions.cs)
(`AddGuardrailPolicy`, `AddMutationGateway`, `AddMutationScopeGuard`), called **last** in `Program.cs` so module
registrations are wrapped too.

## Pipeline

1. Unknown capability → **Denied**.
2. Tool switched off in Settings → MCP → **Denied**. A stricter stored level raises the requirement.
3. Principal level below the capability level → **Denied**.
4. Target cannot be resolved (unknown or ambiguous server, container, network, cloud server, task, zone or workload) → **Denied**. From here on `MutationContext.Request.Target` is canonical; the MCP and agent adapters run the tool with `MutationTargets.CanonicalArguments`, which also passes the resolved `serverId` to every tool that takes one (`McpToolParameters.TakesServerId`, the agent entry's parameters). The refusal carries `MutationReasonCodes.TargetUnknown` / `TargetAmbiguous` / `TargetNotResolved`.
   An AI trigger (R1-13): the gateway notes the trigger behind the call (`MutationRequest.TriggerId`: the trigger
   itself, an agent on its behalf, or — through `MutationScope.TriggerId` — a mutation nested in theirs). For every
   such call, nested ones included, `ITriggerAuthority` checks the trigger's grant (capability, the target's canonical
   identities — id, inventory name, `Aliases` — and every other resource in `MutationTarget.RelatedResources`, each needing a granted alias of its own) and its owner's current authority; a refusal, an error or no registered authority →
   **Denied** (`MutationReasonCodes.TriggerNotAllowed`). The caller's own level check (step 3) still applies.
5. Policy: `Deny` → **Denied** ("Blocked by guardrails: …"); an exception → **Denied**. The policy is the live
   active preset, or for a pinned run (an AI trigger's, `MutationRequest.PinnedPolicy`) that run's preset with the
   global kill switch (`EffectivePolicy`, INV-23); a call nested in a pinned run's mutation without a pin of its own
   inherits it from `MutationScope.PinnedPolicy`, and a pinned request is always decided with the agent rule set
   (also an owner-authorised scheduled task). Target fields are stored without URL credentials. Every verdict — Allow, Confirm and Deny — is then **recorded**
   (`IPolicyDecisionStore`, INV-22, R1-12) before anything acts on it; a failed write → **Denied**. From here on
   `MutationRequest.DecisionId` names the record, the audit rows carry it as `decisionId`, the outcome as
   `MutationOutcome.DecisionId`, and an approval raised for the call as `Approval.DecisionId`. `DecideAsync` runs
   steps 1–5 without executing: an agent session uses it before it pauses for an approval or reports a block, so
   both rest on a recorded decision. A snapshot that cannot be stored fails the record — a decision is stored alone
   only when the exact version is verifiably there already.
6. Policy `Confirm` on the `Automation` surface (scheduler, AutoUpdate, AutoPatch) or for an AI trigger (R1-13, nested
   mutations included) → **Denied** (`UnattendedConfirm`), before the approval gate and also for `DecideAsync`: nobody
   can confirm an unattended run, and no approval is raised (R1-01).
   Policy `Confirm` otherwise: an operation id the journal already holds (ran, running, cut off, other inputs) →
   **Denied** with its reason code (step 7), before any approval is raised or used — also for `DecideAsync`, so an
   agent session never pauses for it (`MutationDecisionResult.ReasonCode`). A call without an operation id of its own
   that retries an open approval for exactly this call takes that approval's operation id (R1-18), before the
   decision is recorded — so the retry's decision, run and audit carry the first call's id. Choosing the id and the
   approval gate's answer happen under one admission lock per binding (striped), so concurrent identical first calls
   share one id and one approval. Then
   [`IApprovalGate`](../Approvals/): runs only with an approval a human gave for exactly this call
   (the agent session's own approval for this operation, or approve-then-retry for a direct MCP call); otherwise
   **PendingApproval**.
7. Audit intent written; if that fails → **Denied** (PC-D5). Then the operation id is claimed in the journal
   (`IOperationJournal`, R1-18): already running, already run, cut off by a restart (outcome unknown) or used for
   other inputs → **Denied** with `OperationRunning` / `OperationAlreadyRun` / `OperationOutcomeUnknown` /
   `OperationIdReused`; a journal that cannot be written → **Denied** (`OperationNotRegistered`). A call nested in
   another one's execution carries `MutationRequest.ParentOperationId` (journal and audit). Only now is the approval used up
   (`IApprovalGate.TryConsumeAsync`, one conditional database update since R1-14, so also across a restart); a concurrent call that already used it turns this attempt's row into **Denied**.
8. Delegate runs inside a `MutationScope`, cancelled at the capability's deadline; an exception, the deadline or a
   reported failure (`MutationScope.Fail`, or an MCP result with `isError`) → **Failed**. A reported failure keeps
   the delegate's value, so the caller still gets the tool's own answer. A delegate stopped by the deadline or by
   its caller while it ran is **Failed** with "its effect is unknown" (R1-38, INV-40), never "not executed".
9. The capability's `IMutationVerifier`, if any → `Verified` / `Failed` / `NotVerifiable` + reason; otherwise `NotVerifiable` with the reason of its `VerificationContracts` row.
10. The journal claim is finished (`Executed` / `Failed` with the verification; a failed write leaves it `Running`, so
    a retry after a restart is refused as outcome unknown), then the audit row is completed, with
    `CancellationToken.None` so a cancelled caller still leaves a record. A refusal after the claim (the approval was
    used by another call) gives the id back.

Refusals (1–7) are recorded as one row each; a failed refusal record is logged and does not change the outcome. A
failed outcome write is handed to `IAuditRecovery`, a failed refusal is kept in the `AuditOutbox` (R1-19): the event stays incomplete — visibly, the
`audit` health check is Degraded — until it is written again or settled from the journal.
Over MCP a tool that throws is rethrown, so the client sees the same error as before the gateway; a tool that
reported a failure is answered with its own text, marked `isError`. The agent gets the tool's text as an error.

## Known gaps after step 2.3

- **Grace period (opt-in).** With `Automation:RequireOwner=false`, ownerless tasks and an ownerless AutoUpdate run
  with the level of the capability they run until an Admin takes them over. The default is `true` since R1-04.
- **Self-backup and retention** (R1-48): `IBackupService` create / delete / prune / restore are marked
  (`selfbackup.*`); the DB-backup retention `rm` runs through the host primitive. The two start-up paths before the
  gateway exists — the pre-upgrade backup and its pruning (S-09, `UpgradeGuard`) and the command-line restore (S-10,
  `RestoreCli`) — use narrow unmarked methods (`CreatePreUpgradeBackupAsync`, `DeletePreUpgradeBackupAsync`, which
  refuses any backup that is not a pre-upgrade one, `BeginHostRestoreAsync`) whose single callers are pinned by
  `MutationGatewayCoverageTests`. `UpdateRiskService` is its own capability (`image.assess_update`, Write, composing
  `image.pull`). The automatic lapse of a pause in `LoopSuspensionService` is not `checks.resume`: it removes only
  the expired entry it read (a newer pause stays), so it acts on nobody's behalf.
- **The first administrator** (R1-48, maintainer option a): the first-run wizard runs before any person exists the
  gateway could decide for. It writes through `IWhitelistService.AddFirstAdminAsync` and
  `IRoleService.SetFirstAdminAsync`, which refuse once any Admin exists — the wizard holds `AccessControlWriteGate`, the
  one write boundary of all role and whitelist writers, across the check and both writes (at most one of concurrent calls
  wins, no other writer runs in between) and the Admin is published only after it was saved — are pinned to `SetupStateService` and are audited (`setup.first_admin`, written first;
  without it nothing is written). `SaveWhitelistAsync`, `SetRoleAsync`, `RemoveRoleAsync` and `SaveRoleDataAsync`
  are gateway operations only.
- **CVE scanners** (S-06, designed exemption): `apt-get update` and Trivy's scanner image, throw-away container and
  cache volume change host caches, not workloads; they are pinned to the CVE monitor (and the update-risk assessment,
  inside its own decision).
- **Key-owned items** are evaluated with the full rule set of the key (tool lists, forbidden arguments,
  confirmation). A `Confirm` on the `Automation` surface is a terminal `Denied` in the gateway, before the approval
  gate (R1-01): no one can approve an unattended run, and no approval is raised that a later occurrence of the same
  task could match. Items a person created or took over, and ownerless items in the grace period, use the PC-D1
  rules. Items an agent created — and owned items whose provenance was never recorded (created before R1-03), until
  an admin takes them over — keep the agent's rules (R1-04).
- **Owner file** (`automation-owners.json`): unreadable, invalid, a directory or not openable → every feature
  `IsBlocked`, AutoPatch and AutoUpdate do not run (fail closed). Taking one feature over clears only its block; the
  others are written to the file as blocked entries, so they stay blocked after a restart. Only a file that is not
  there counts as "no owners yet".
- **Scheduled commands** carry the command itself into the decision (`host.exec`, `scheduler.create`, "Run now"),
  so forbidden-argument patterns apply on every run; the audit row keeps only its length.
- **"Run now"** is decided for the person clicking (the task's own capability, a retention delete, then
  `scheduler.run`) and then runs on the owner's authority, re-checked as on schedule.
- **Refusal reasons** leave the gateway without payload values (`RefuseAsync` → `WithoutPayloadValues`): a rule that
  quotes the matched argument (protected resource) cannot carry a command or SQL text into a log line, a
  notification, the task history or a UI message.
- **Taking a task over**: an Admin can take over any task in Scheduled Tasks; a task whose owner lost its authority
  (resolved as `read`) is flagged and included in "take over all". The previous owner goes into the audit row: the takeover is conditional on the owner the page showed (another admin's takeover meanwhile makes it a no-op with a warning), and AutoPatch / AutoUpdate return the owner they replaced from the store's locked write.
- **AutoUpdate policies** have no UI yet (`SetPolicyAsync` has no caller); the owner is per feature, not per policy.
- **An AutoPatch night** is one `autopatch.run` operation (4-hour deadline) and, inside it, one `container.update`
  decision per container it patches or tries in a dry run (protected resources, kill switch, the owner's authority
  at that moment). `Unknown`, `RolledBack` and `RollbackNotVerified` mark that decision and the night as failed.
- **The MCP preview of AutoPatch** (`preview_auto_patch`, a dry run) runs on the caller's authority
  (`AutomationOwners.OfCaller`): every candidate is a `container.update` decision for that user or key, re-resolved
  per candidate, so a key revoked mid-cycle stops the rest. A key-owned preview under a preset that confirms key
  writes is refused per candidate (no one can approve a background run). A call without a user or key behind it
  is refused.

- **One audit event per attempt** for MCP tools (R1-20, `IToolAuditLog`) and the infrastructure pages (R1-21): the
  gateway's event carries decision, approval, intent, outcome and what the tool or page reports about its work
  (`MutationScope.Record` / `Note`). A page's role check in front of its confirmation dialog records its refusal
  through `IUiMutationRunner.RefuseAsync` → `IMutationGateway.RefuseAsync` (one Denied event, nothing runs). Since
  R1-22 the configuration pages follow the same rule, and a configuration store calls `ConfigChange.Record(section,
  before, after)` after a successful write, under the store's own write lock so each change starts where the one
  before ended (entity stores pass a projection without runtime counters such as trigger counts or deploy results):
  the event gets the section's version before/after (`config.version`, never shortened) and each changed setting
  (`setting`, at most `ConfigChange.MaxChanges` per operation, the rest counted). Comparison uses the complete
  values (a secret by its whole per-process HMAC); the version hashes only a safe projection — secrets as `[set]`,
  URLs as their origin, free text as `[text]` — so a change of those alone keeps the version and still appears as a
  changed setting. What is shown is safe: secrets (also ntfy topics, kubeconfig references and SSH keys, the latter
  as section `ssh-key:<server>`) as `[set #…]`/`[not set]` (a longer fingerprint where two short ones collide), URLs
  as their origin, free text, addresses with user info and URL-named values that are no URL as `[text, N chars #…]`;
  flags and counts named after a secret (`HasToken`, `MaxTokens`) stay readable. A repeated record identity is numbered
  by occurrence (`a@example.com~2`). A scope keeps at most 20 reports and counts the rest (`MutationScope.OmittedAction`).
  Outside a gateway scope `Record` does nothing.
- **Failure reports.** The 25 mutating MCP tools mark their error answers with `MutationScope.Fail`. UI actions are
  classified by `MutationResults`: `false`, a failed `CommandResult`, a `QueryResult` with an error or a tuple whose
  first item is `false`, or a result object whose `bool Success` is `false` (R1-21), is recorded as `Failed` (the page
  still gets the value). A handler whose command answers a failure with exit code 0 says so itself (`MutationScope.Fail`,
  e.g. `ContainerDetail`'s `failedWhen` for a missing migration framework or an unsupported import). A failure
  answered as a plain message string is still recorded as `Executed`.
- **Deadlines** reach a tool through its `CancellationToken` parameter, over MCP and for the agent. A tool that has
  none, or work it starts in the background, is not stopped (INV-40, WP9).
- **Calls from a tool to another capability** are explicit compositions since R1-48 (`MutationCompositions`):
  `deploy_app` pulling an image and creating a container runs inside `app.deploy`. `preview_auto_patch` is its own
  capability on `IAutoPatchService.StartPreview` (`autopatch.preview`); the background cycle it starts decides each
  candidate as `container.update` on the caller's authority and outlives the gateway's deadline and outcome row.
- **Raw host execution, terminals and server records** (R1-48): writes through the host executor use its primitive
  `ExecuteMutationAsync` (`execute_command`, `set_container_env`, `deploy_compose`, scheduled commands, cleanup and
  retention, the container page's `.env` save and Artisan commands, UI and webhook compose deploys);
  `MutationGatewayCoverageTests` pins exactly these callers, and lists every caller of the plain `ExecuteAsync` in every
  namespace as a read, the body of a marked operation of its own type, or a private helper of a guarded service. Every
  write-named interface method is marked or listed with its reason (`Every_write_named_interface_method_is_marked_or_explained`),
  and no constructor takes the concrete implementation of a guarded interface. Server records
  (`IServerConfigService` add/update/SSH key → `server.save`, remove/take → `server.remove`), onboarding, webhook
  management, AI-trigger save, Kubernetes workload actions (the provider from `WorkloadProviderFactory` is guarded),
  cloud power/snapshot and the Hetzner extensions are marked.
- **Compose deploys** are planned before anything is written (R1-07, `ComposePlan`): the effective project
  (`-p` > `COMPOSE_PROJECT_NAME` in the `.env` > top-level `name:` > directory name) and every existing container
  `up` recreates, removes as an orphan or collides with by name. The UI (compose editor, App Store, Deploy page,
  `.env` save, webhook **Test**, Git deploy) decides once per planned container; MCP and agent (`deploy_compose`,
  `set_container_env`) get the planned containers as related resources of their one decision, made by the resolver.
  The `.env`'s compose switches are planned too (`COMPOSE_FILE`, `COMPOSE_PROJECT_NAME`, `COMPOSE_REMOVE_ORPHANS`).
  A project name interpolated from the `.env` or the top-level `name:` is passed into a decision (and so the audit) as
  the expression it came from (`ComposePlan.ProjectNameForAudit`, e.g. `${DB_PASSWORD}`), never as the value; a
  literal name as it is. Existing containers are recorded by their names on the host.
  Execution uses the plan's explicit `-p` / `--project-directory` / `-f` and refuses (`EnsureDecided`) when a plan made
  again right before writing touches a container no enclosing decision covered (UI, MCP, agent, webhooks, Git
  deploy). Not covered: values in the host's own environment, which compose prefers to
  the `.env` (a name using one without default refuses the plan; with a default the `.env` / default is assumed;
  the host's orphan switches are cleared for the command), and a `.env` created between plan and `up` when there was
  none.
- **Git deployments** (R1-08) are decided on the stored app (`git-deploy-app` targets: name, server and revision
  from the store; a per-container decision inside the deployment resolves that container); a webhook Git deploy
  runs through `IAutomationMutationRunner` on the app owner (`GitDeployOwners`).
- **Webhooks** (R1-09): an external call (`/api/webhooks/...`) runs its action through `IAutomationMutationRunner` on
  the webhook's owner (`webhook` targets resolve the stored webhook: its name and server — a removed server does not
  block switching it off or deleting it); every delivery is decided, also one without an owner or with a target that
  cannot be resolved (the resolver refuses it), so it is audited and a lost authority switches the webhook off. The
  UI's **Test** button is decided as the clicking person first and then runs the same way. Management writes are
  `webhook.*` capabilities.
  An owner (webhook, Git deploy app) set by a person on the page is recorded as its provenance too, so the PC-D1
  rules for a person apply.
- **Kubernetes workload actions** (`workload.start` / `stop` / `restart`) are decided on the pod's **live**
  controller: the resolver re-reads the pod (`IWorkloadProvider.GetWorkloadAsync`) instead of trusting the name the
  page rendered (R1-06). The provider resolves the controller again when it acts; a controller change in between is
  not detected.
- **Bulk prune** (`host.prune`) is decided on the stopped containers it removes (R1-16; unlistable containers
  refuse it): those listed in `containerIds` (one per line; one no longer on the server refuses the run), else every
  stopped container of the server. The cleanup task lists them once, keeps its own pre-check, and runs
  `TaskExecutor.CleanupCommandFor(ids)` — `docker rm` of exactly those ids, then image and builder prune — so a
  container that stops after the decision is not removed; networks are not pruned, since which ones are unused
  cannot be decided beforehand; dangling images are not checked against protected patterns.
- **Free text** (`command`, `query`, `yaml`, …) is checked word by word against protected names and the control
  plane (R1-16, `ProtectedResourceMatcher`; commands as the shell reads them); `MutationTargetResolver` adds every
  container a free-text word of twelve or more hex digits (any hex word in a `docker …` command except options,
  their numbers, image references, paths and file names; a single digit only after a subcommand that takes
  containers; the container of a Docker API path) names by id prefix
  (unlistable containers refuse the call). UI pages hand their SQL, command or compose file to the gateway for this;
  the audit keeps only lengths, and an approval raised from the UI stores only lengths too
  (`ApprovalSubject.WithholdPayloads`). A command that runs a script file or pipes into a shell or an interpreter
  is unanalysable.
- **Compose mounts** (R1-16): `ComposePlan.Mounts()` lists every host path and volume the services mount
  (interpolated; relative bind sources resolved against the project directory; project-scoped volumes also as
  `<project>_<key>`, explicit `name:` resolved; a volume with `driver_opts: device: <host path>` also as that path;
  a long `type: bind` entry as a host path even when relative; `<<` merges through any depth; the files of
  file-backed `secrets` / `configs`; `volumes_from` as the other service's mounts (of any file, resolved after all
  files are read) or the container's id and name, looked up on the server —
  `ComposeModel.ResolveContainers`; not found or not unique refuses the plan without quoting the reference — plus
  that container's own mounts from `docker inspect`, unreadable → refused). `ComposePlanner` also has compose
  resolve the deployment on the server (`ComposeConfigEffects`: `docker compose config --format json` with the
  planned files and `.env` written base64 to a temporary directory) and adds every mount compose reports that its own
  model did not (`ComposeModel.WithEffects`, withheld); a failed or unreadable run, a service the files do not show
  or an unknown mount kind refuses the plan. A mount whose source came from a variable is
  decided but listed in `MutationTarget.Withheld`: the decision record stores `(withheld)`, the audit only the length of
  `mounts`.
  A `.env` saved on the container page decides them the same way (`ComposeImpact.RunMountsAsync`). `ComposeImpact`
  decides them under `compose.deploy` (`mounts`, one per line) before the plan's containers, and
  `ComposePlan.EnsureDecided` refuses a re-plan whose mounts were not decided.
- **Free-text arguments** (`query`, `command`, `content`, `env`, …) are kept out of the audit row
  ([`SinkRedaction.IsPayload`](../../Utils/SinkRedaction.cs): name parts such as `command`, `sql`, `compose`,
  `content` in every spelling — `composeContent`, `compose_content`, `docker-compose` — only their length is stored, R1-24); the policy sees them, and so does the
  approval card of a non-UI request (an agent's chat call, whose approver must read what the agent wants to run),
  with secrets masked by `SecretRedactor` — including SQL password literals (`PASSWORD '…'`, `IDENTIFIED BY …`) —
  a UI approval stores only their lengths.
- **A UI action that outlives its deadline** keeps running when the service call ignores the token (most do); the
  page then shows a timeout while the work continues.
- **Approval eligibility.** Approvals are persisted and fully bound since R1-14 (`../Approvals/`); who may approve (no self-approval for unattended requests) and requester-session lifetime follow in R1-15.
- `MutationPrincipal` maps onto `AgentPrincipal` for the policy; WP6 brings structured identity.

## Tests

`MutationGatewayTests` (pipeline), `UiMutationRunnerTests` (UI principal, PC-D1 rules, PC-D3),
`AutomationMutationRunnerTests` (owner re-check per run, keys, grace period, `RequireOwner`, refused task, owner store), `ToolAuthorizationTests` (tool switch and stricter level for every principal,
target resolution, session approval, approval used only after the intent, reported tool failures over MCP and for
the agent), `ApprovalGateTests`, `MutationScopeGuardTests` (an unscoped call throws in production, compositions,
escaped and detached scopes), `CapabilityCatalogTests`, `MutationGatewayBootTests` (real composition root and
`AuditLog` table), `GuardrailModuleIndependenceTests` (Agent module off), `MutationGatewayCoverageTests` (the
inventory of writes by surface, the primitive's callers, the start-up backup paths) and
`MutationBypassArchitectureTests`, which reads method IL and requires every call to a marked method to sit in a
decision that covers it: a UI runner lambda, the type's own marked operation, an MCP tool mapped to a capability, a
`[MutationHandler]` for a capability, or a private helper whose every caller is one of those — each checked against
`MutationCompositions`. The baseline file it used to compare against was removed in R1-48 (104 lines after step 2.0,
75 after 2.1, 29 after 2.2, 23 after 2.3, none since); nothing is allowed by list. `MutationEnforcementBootTests` starts
the app enforcing in each auth mode (disabled, local, OIDC), checks that nothing at start-up writes outside a decision
and completes the first-run wizard.

## Levels and effective requirements (R1-05)

`CapabilityCatalog` is the one level table: every capability states its level, and
`McpPermissionLevels.DefaultToolLevels` takes the level of every mutating tool from
`CapabilityCatalog.McpToolLevels`. Root-equivalent capabilities are Admin. `EffectiveRequirements.cs` holds
`IEffectiveRequirementResolver`; the gateway raises (never lowers) a capability's level with it before the caller
check and before an approval is requested. `SchedulerRequirementResolver` gives a scheduled task the level of what it
runs (CustomCommand → `host.exec`, retention → `*.backup_delete`, SelfBackup → `selfbackup.*`) and fails closed when
the scheduler is unavailable. `CapabilityTableConsistencyTests` and `ApprovalSeparationOfDutyTests` guard both.
