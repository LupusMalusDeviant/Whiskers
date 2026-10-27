# Services/Mutations

The **Mutation Gateway** ([ADR-0005](../../../../docs/adr/0005-mutation-gateway.md)): the one path every
infrastructure mutation is meant to take. An entry point describes the mutation as a `MutationRequest`; the gateway
decides, executes the supplied delegate, verifies and records.

**State: WP2 step 2.3.** MCP tools, the agent, the UI and unattended automation run every mutation through the
gateway:

- direct MCP calls in [`McpGuardrailFilter`](../../Mcp/McpGuardrailFilter.cs) (surface `Mcp`);
- agent tool calls in [`AgentToolInvoker`](../Agent/AgentToolInvoker.cs) (surface `Agent`, the principal acting
  `OnBehalfOf` the user, key or trigger that started it);
- UI handlers through [`IUiMutationRunner`](UiMutationRunner.cs) (surface `Ui`, the signed-in user as a `Human`
  principal);
- scheduled tasks, AutoUpdate and AutoPatch through [`IAutomationMutationRunner`](AutomationMutationRunner.cs)
  (surface `Automation`, an `Automation` principal acting `OnBehalfOf` the recorded owner).

A tool counts as mutating when the catalog lists it (`ICapabilityCatalog.FindByMcpTool`). Webhooks / Git deploy
(2.4) and terminals (2.5) follow; step 2.6 makes bypassing the gateway fail.

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
taken over in Scheduled Tasks), AutoPatch and AutoUpdate in [`IAutomationOwnerStore`](AutomationOwnerStore.cs)
(`automation-owners.json`, taken over in Settings → Automation owners). AutoPatch skips its night without an owner.

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
| `MutationAttributes.cs` | `[MutatingOperation("<area>.<verb>")]` on service-interface methods that change infrastructure; `[MutationHandler]` on classes allowed to call them; `[MutationEntryPoint]` on the runner methods whose lambda argument the architecture test counts as gated. |
| `MutationModels.cs` | `Capability`, `CapabilityRisk`, `MutationPrincipal` (incl. `OnBehalfOf`, `AllowedTools`), `MutationSurface`, `MutationTarget`, `MutationRequest`, `MutationOutcome<T>` (incl. `ApprovalId`, `MatchedRules`), `MutationContext`. |
| `CapabilityCatalog.cs` | `ICapabilityCatalog` — the single table of mutating capabilities and their levels (INV-13, R1-05). `McpPermissionLevels.DefaultToolLevels` takes its mutating entries from it; `RoleFor` maps a level to the UI role pages check (`CapabilityTableConsistencyTests` holds hard-coded page roles to it). |
| `CapabilityOverrides.cs` | `ICapabilityOverrides` + `McpToolCapabilityOverrides`: the MCP tool's Enabled switch and a stricter stored level (Settings → MCP), applied for **every** principal kind (INV-15). The catalog level is the floor. |
| `MutationTargets.cs` | `MutationTargets.FromArguments` (target from tool arguments), `MutationTargets.CanonicalArguments` (the arguments a tool runs with: the target's key and `serverId` replaced by the resolved identity, keys the caller did not pass stay absent) and `IMutationTargetResolver` + `MutationTargetResolver` (R1-06, INV-24/57): the server first (empty = the default server; exact id, else one id / name ignoring case), then containers (id, name or unique prefix), networks (id, name or unique prefix; `Related` = the attached containers for a removal, the named container for connect / disconnect), cloud servers (Whiskers id or name), scheduled tasks (id or unique name), DNS zones (normalised, within `Dns:AllowedZones`) and Kubernetes workloads (the pod's **live** controller as `Name`). Unknown or ambiguous targets are refused before policy. Creating capabilities (`network.create`, `scheduler.create`) resolve only the server; `server.onboard` / `server.save` match only that server by its own id (exact, else ignoring case — never the default server or a name) and pass a new one through; volumes, log alerts and the Whiskers-wide kinds pass through. |
| `MutationGateway.cs` | `IMutationGateway` + `MutationGateway` (the pipeline) and `IMutationVerifier`. |
| `AutomationMutationRunner.cs` | `IAutomationMutationRunner` + `AutomationMutationRunner`, `AutomationActor`, `AutomationOptions` (`RequireOwner`), `IAutomationOwnerResolver` + `AutomationOwnerResolver`, `AutomationOwners.IdOf`: the automation entry to the gateway. |
| `PrincipalIdentity.cs` | `PrincipalIds` (the stable id scheme `user:` / `key:` / `trigger:` / `agent:<session>`) and `MutationPrincipals` (gateway principal from an `AgentPrincipal` and back; the agent principal is the session acting on behalf of its user, key or trigger). |
| `OwnerAccounts.cs` | `IOwnerAccounts` + `OwnerAccounts`: whether a user owner still has a way in (R1-04). Local-only sign-in needs the Identity row. With Google/OIDC as well, `known-local-accounts.json` records every email seen with a local account (at startup and when the wizard creates one) and every email seen signed in federated: no local row → *Deleted* if it had one, *Present* if seen federated, otherwise *Unverified* (refused, not suspended, until the owner signs in or an admin takes over). Missing or unreadable record → unverified (fail closed). |
| `FederatedSignInRecorder.cs` | Blazor `CircuitHandler` that records a federated sign-in (`IOwnerAccounts.RememberFederated`) from the circuit's authentication state; only observes, never touches the sign-in itself (R1-04). |
| `AutomationOwnerStore.cs` | `IAutomationOwnerStore` + `AutomationOwnerStore`: owners of AutoPatch / AutoUpdate in `automation-owners.json`, written atomically; an unreadable file blocks every feature until each is taken over. |
| `ComposePlan.cs` | R1-07: `ComposeDeployment` (what is asked: server, files or directory, project directory, explicit project, new `.env`, `--remove-orphans`), `ComposePlan` (the effective project, files, services and `Impact` — every existing container recreated, removed as orphan or colliding by name; `UpCommand` / `BuildCommand` / `DeployScript` with explicit `-p`, `--project-directory`, `-f`, `--env-file`; `EnsureDecided`), `ComposeModel` (pure: project name precedence and normalisation, `.env` parsing, interpolation, file merging, impact) and `ComposePlanGate`. Anything that cannot be worked out throws `ComposePlanException` (fail closed). |
| `ComposePlanner.cs` | `IComposePlanner` + `ComposePlanner` (singleton): reads what the deployment does not bring along from the host (a directory's default compose file + override, the existing `.env`, a container's `config_files` / working directory for a `.env` change) and lists the server's containers. Read-only. |
| `ComposeImpact.cs` | UI side of a plan: `DeployAsync` (plan, decide the capability, then each planned container nested, plan again and run it), `GateFor` (a `ComposePlanGate` for plans made inside an operation, e.g. Git deploy) and `RunForEachAsync`. |
| `UiMutationRunner.cs` | `IUiMutationRunner` + `UiMutationRunner` (scoped, per circuit), `MutationRefusedException`, `MutationFailedException`: the UI's entry to the gateway. A refusal with a `ReasonCode` (`MutationReasonCodes`, R1-05) is shown in the user's language (`Mutation_Refused_<code>`); the audit keeps the English `Reason`. |
| `MutationPolicy.cs` | `IMutationPolicy` + `GuardrailMutationPolicy`, the adapter onto the core guardrail engine. For an agent it evaluates the principal the agent acts for, with that key's tool list; for a human in the UI the PC-D1 rule set. The rules also see the resolved target (`__resolvedTargetId`, `__resolvedTargetName`, `__resolvedRelated<n>`), so a protected container is caught by id prefix, as a container attached to a removed network, and a workload by its live controller. Without engine or store it refuses everything. |
| `MutationAudit.cs` | `IMutationAuditSink` + `AuditLogMutationSink`: writes the intent (with the approval id and approver) before execution and turns the same `AuditLog` row into the outcome. Unlike `AuditLogService`, it throws on a failed write. |
| `MutationScope.cs` | The ambient (`AsyncLocal`) scope the gateway opens around a delegate; nested service calls run inside it. `MutationScope.Fail(reason)` lets code that answers with an error text instead of throwing (most MCP tools) mark the operation as failed. `Covers(serverId, identities)` says whether this or an enclosing decision saw a resource (target, related resources, argument values) on that server — used by `ComposePlan.EnsureDecided` (R1-07). |
| `MutationScopeGuard.cs` | `MutationScopeProxy<T>` (a `DispatchProxy` around each service interface with marked methods), `IMutationScopeMonitor` and `MutatingMethods`. **Report mode:** a call outside a scope still runs; the first sighting per capability is logged at Information. |

Registration: [`../../Startup/WhiskersMutationGatewayExtensions.cs`](../../Startup/WhiskersMutationGatewayExtensions.cs)
(`AddGuardrailPolicy`, `AddMutationGateway`, `AddMutationScopeGuard`), called **last** in `Program.cs` so module
registrations are wrapped too.

## Pipeline

1. Unknown capability → **Denied**.
2. Tool switched off in Settings → MCP → **Denied**. A stricter stored level raises the requirement.
3. Principal level below the capability level → **Denied**.
4. Target cannot be resolved (unknown or ambiguous server, container, network, cloud server, task, zone or workload) → **Denied**. From here on `MutationContext.Request.Target` is canonical; the MCP and agent adapters run the tool with `MutationTargets.CanonicalArguments`, which also passes the resolved `serverId` to every tool that takes one (`McpToolParameters.TakesServerId`, the agent entry's parameters). The refusal carries `MutationReasonCodes.TargetUnknown` / `TargetAmbiguous` / `TargetNotResolved`.
5. Policy: `Deny` → **Denied** ("Blocked by guardrails: …"); an exception → **Denied**.
6. Policy `Confirm` on the `Automation` surface (scheduler, AutoUpdate, AutoPatch) → **Denied**, before the approval
   gate: nobody can confirm an unattended run, and no approval is raised (R1-01).
   Policy `Confirm` otherwise → [`IApprovalGate`](../Approvals/): runs only with an approval a human gave for exactly this call
   (the agent session's own approval for this operation, or approve-then-retry for a direct MCP call); otherwise
   **PendingApproval**.
7. Audit intent written; if that fails → **Denied** (PC-D5). Only now is the approval used up
   (`IApprovalGate.TryConsume`); a concurrent call that already used it turns this attempt's row into **Denied**.
8. Delegate runs inside a `MutationScope`, cancelled at the capability's deadline; an exception, the deadline or a
   reported failure (`MutationScope.Fail`, or an MCP result with `isError`) → **Failed**. A reported failure keeps
   the delegate's value, so the caller still gets the tool's own answer.
9. The capability's `IMutationVerifier`, if any → `Verified` / `Failed`; otherwise `NotVerifiable`.
10. The audit row is completed, with `CancellationToken.None` so a cancelled caller still leaves a record.

Refusals (1–7) are recorded as one row each; a failed refusal record is logged and does not change the outcome.
Over MCP a tool that throws is rethrown, so the client sees the same error as before the gateway; a tool that
reported a failure is answered with its own text, marked `isError`. The agent gets the tool's text as an error.

## Known gaps after step 2.3

- **Grace period (opt-in).** With `Automation:RequireOwner=false`, ownerless tasks and an ownerless AutoUpdate run
  with the level of the capability they run until an Admin takes them over. The default is `true` since R1-04.
- **Gated by convention only:** self-backup tasks (`selfbackup.create` / `selfbackup.prune`) and the DB-backup
  retention (`db.backup_delete`, a host `rm`) run through the gateway, but their services are not marked, so the
  architecture test would not catch a new direct call. `UpdateRiskService` (`image.pull`) and
  `LoopSuspensionService` (`checks.resume`) stay in the baseline.
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

- **Two audit rows per MCP or agent mutation.** The gateway row carries decision, approval, intent and outcome; the
  tool still writes its own row with the domain details (new container id, exit code, backup file). One event per
  attempt (INV-45) needs typed tool outcomes and verifiers.
- **Failure reports.** The 25 mutating MCP tools mark their error answers with `MutationScope.Fail`. UI actions are
  classified by `MutationResults`: `false`, a failed `CommandResult`, a `QueryResult` with an error or a tuple whose
  first item is `false` is recorded as `Failed` (the page still gets the value). A failure answered in another shape
  (an error string from a cloud provider API) is still recorded as `Executed`; the own row has the real result.
- **Deadlines** reach a tool through its `CancellationToken` parameter, over MCP and for the agent. A tool that has
  none, or work it starts in the background, is not stopped (INV-40, WP9).
- **Calls from a tool to another capability** stay in the baseline: `deploy_app` creating a container and pulling an
  image, `preview_auto_patch` starting a background cycle (`autopatch.run`). The gateway decided on the tool's
  capability only (the preview's candidates are decided one by one inside the cycle); the background cycle
  outlives the gateway's deadline and outcome row.
- **Not yet marked** (so neither the guard nor the architecture test sees them): the generic execution primitives
  `IHostCommandExecutor` and `IDockerService.RunHostShellAsync`, terminals and server records. The UI actions built
  on the primitives (container `.env` save + compose up, Artisan migrate/rollback, SQL file import, compose editor
  and App Store deploys, the webhook **Test** button, server save/remove/onboarding, AI-trigger save, self-backup
  create/delete/restore) run through the gateway since the step 2.2 review, but only by convention: a new direct
  call is not caught until the primitives are marked.
- **Compose deploys** are planned before anything is written (R1-07, `ComposePlan`): the effective project
  (`-p` > `COMPOSE_PROJECT_NAME` in the `.env` > top-level `name:` > directory name) and every existing container
  `up` recreates, removes as an orphan or collides with by name. The UI (compose editor, App Store, Deploy page,
  `.env` save, webhook **Test**, Git deploy) decides once per planned container; MCP and agent (`deploy_compose`,
  `set_container_env`) get the planned containers as related resources of their one decision, made by the resolver.
  The `.env`'s compose switches are planned too (`COMPOSE_FILE`, `COMPOSE_PROJECT_NAME`, `COMPOSE_REMOVE_ORPHANS`).
  Execution uses the plan's explicit `-p` / `--project-directory` / `-f` and refuses (`EnsureDecided`) when a plan made
  again right before writing touches a container no enclosing decision covered (UI, MCP, agent, webhook Test, Git
  deploy page). Not covered: values in the host's own environment (a name using one without default refuses the
  plan; with a default the default is assumed), a `.env` created between plan and `up` when there was none, and the
  external webhook and Git-deploy webhook paths, which run the plan undecided until R1-08 / R1-09.
- **External webhook calls** (`/api/webhooks/...`) still run without the gateway; they move in step 2.4. Only the
  UI's **Test** button is decided, as the clicking person.
- **Kubernetes workload actions** (`workload.start` / `stop` / `restart`) are decided on the pod's **live**
  controller: the resolver re-reads the pod (`IWorkloadProvider.GetWorkloadAsync`) instead of trusting the name the
  page rendered (R1-06). The provider resolves the controller again when it acts; a controller change in between is
  not detected.
- **Bulk prune** (`host.prune`) is refused while a stopped protected container exists on the server; images and
  networks it removes are not checked against protected patterns.
- **Free-text arguments** (`query`, `command`, `content`, `env`, …) are kept out of the audit row
  (`AuditLogMutationSink.PayloadArguments`, only their length is stored); the policy and the approval card still see
  them.
- **A UI action that outlives its deadline** keeps running when the service call ignores the token (most do); the
  page then shows a timeout while the work continues.
- **Approvals are in memory.** Persistence, execution-time expiry and no-self-approval follow in WP3.
- `MutationPrincipal` maps onto `AgentPrincipal` for the policy; WP6 brings structured identity.

## Tests

`MutationGatewayTests` (pipeline), `UiMutationRunnerTests` (UI principal, PC-D1 rules, PC-D3),
`AutomationMutationRunnerTests` (owner re-check per run, keys, grace period, `RequireOwner`, refused task, owner store), `ToolAuthorizationTests` (tool switch and stricter level for every principal,
target resolution, session approval, approval used only after the intent, reported tool failures over MCP and for
the agent), `ApprovalGateTests`, `MutationScopeGuardTests`, `CapabilityCatalogTests`,
`MutationGatewayBootTests` (real composition root and `AuditLog` table), `GuardrailModuleIndependenceTests` (Agent
module off) and `MutationBypassArchitectureTests`, which reads method IL and compares every call to a marked method
outside the gateway with [`docs/v1/mutation-bypass-baseline.txt`](../../../../docs/v1/mutation-bypass-baseline.txt).
An MCP tool mapped to a capability counts as inside the gateway, because both dispatchers run it there. The baseline
may only shrink: 104 lines after step 2.0, 75 after step 2.1, 29 after step 2.2, 23 after step 2.3.

## Levels and effective requirements (R1-05)

`CapabilityCatalog` is the one level table: every capability states its level, and
`McpPermissionLevels.DefaultToolLevels` takes the level of every mutating tool from
`CapabilityCatalog.McpToolLevels`. Root-equivalent capabilities are Admin. `EffectiveRequirements.cs` holds
`IEffectiveRequirementResolver`; the gateway raises (never lowers) a capability's level with it before the caller
check and before an approval is requested. `SchedulerRequirementResolver` gives a scheduled task the level of what it
runs (CustomCommand → `host.exec`, retention → `*.backup_delete`, SelfBackup → `selfbackup.*`) and fails closed when
the scheduler is unavailable. `CapabilityTableConsistencyTests` and `ApprovalSeparationOfDutyTests` guard both.
