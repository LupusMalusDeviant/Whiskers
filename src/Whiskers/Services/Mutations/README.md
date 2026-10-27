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
a background loop has nobody to show an exception to. The owner id is an email or `key:<id>`
(`AutomationOwners.IdOf`); `IAutomationOwnerResolver` reads its authority **on every run** (INV-04): a user's role
only while the whitelist still lets them sign in, a key's level only while it exists and is enabled — otherwise
`read`, so the run is refused. The owner's level is the ceiling; the policy applies the PC-D1 rules (authorization,
kill switch, protected resources) without asking for approval — the owner approved by scheduling.

Where the owner is kept: scheduled tasks in `ScheduledTaskEntity.CreatedBy` (set by the UI and `create_scheduled_task`;
taken over in Scheduled Tasks), AutoPatch and AutoUpdate in [`IAutomationOwnerStore`](AutomationOwnerStore.cs)
(`automation-owners.json`, taken over in Settings → Automation owners). AutoPatch skips its night without an owner.

**Grace period.** An item without an owner (created before 2.3) runs as `unowned:<runner>` with exactly the level
of the capability it runs, audited as "(no owner)". `Automation:RequireOwner=true` refuses it instead.

## Files

| File | Purpose |
|---|---|
| `MutationAttributes.cs` | `[MutatingOperation("<area>.<verb>")]` on service-interface methods that change infrastructure; `[MutationHandler]` on classes allowed to call them; `[MutationEntryPoint]` on the runner methods whose lambda argument the architecture test counts as gated. |
| `MutationModels.cs` | `Capability`, `CapabilityRisk`, `MutationPrincipal` (incl. `OnBehalfOf`, `AllowedTools`), `MutationSurface`, `MutationTarget`, `MutationRequest`, `MutationOutcome<T>` (incl. `ApprovalId`, `MatchedRules`), `MutationContext`. |
| `CapabilityCatalog.cs` | `ICapabilityCatalog` — the single table of mutating capabilities (INV-13). An MCP-exposed capability takes its tool's level from `McpPermissionLevels.DefaultToolLevels`, a UI-only one the role its handler checks. |
| `CapabilityOverrides.cs` | `ICapabilityOverrides` + `McpToolCapabilityOverrides`: the MCP tool's Enabled switch and a stricter stored level (Settings → MCP), applied for **every** principal kind (INV-15). The catalog level is the floor. |
| `MutationTargets.cs` | `MutationTargets.FromArguments` (target from tool arguments) and `IMutationTargetResolver` + `MutationTargetResolver`: containers resolve by id, name or unique prefix to their full id and name; an unknown or ambiguous container is refused before policy (INV-57). Other kinds pass through for now. |
| `MutationGateway.cs` | `IMutationGateway` + `MutationGateway` (the pipeline) and `IMutationVerifier`. |
| `AutomationMutationRunner.cs` | `IAutomationMutationRunner` + `AutomationMutationRunner`, `AutomationActor`, `AutomationOptions` (`RequireOwner`), `IAutomationOwnerResolver` + `AutomationOwnerResolver`, `AutomationOwners.IdOf`: the automation entry to the gateway. |
| `AutomationOwnerStore.cs` | `IAutomationOwnerStore` + `AutomationOwnerStore`: owners of AutoPatch / AutoUpdate in `automation-owners.json`, written atomically; an unreadable file blocks every feature until each is taken over. |
| `ComposeImpact.cs` | Existing containers a `docker compose up` would touch (project label or working directory) and `RunForEachAsync`, one nested UI decision per container. |
| `UiMutationRunner.cs` | `IUiMutationRunner` + `UiMutationRunner` (scoped, per circuit), `MutationRefusedException`, `MutationFailedException`: the UI's entry to the gateway. |
| `MutationPolicy.cs` | `IMutationPolicy` + `GuardrailMutationPolicy`, the adapter onto the core guardrail engine. For an agent it evaluates the principal the agent acts for, with that key's tool list; for a human in the UI the PC-D1 rule set. The rules also see the resolved target (`__resolvedTargetId`, `__resolvedTargetName`), so a protected container is caught by id prefix too. Without engine or store it refuses everything. |
| `MutationAudit.cs` | `IMutationAuditSink` + `AuditLogMutationSink`: writes the intent (with the approval id and approver) before execution and turns the same `AuditLog` row into the outcome. Unlike `AuditLogService`, it throws on a failed write. |
| `MutationScope.cs` | The ambient (`AsyncLocal`) scope the gateway opens around a delegate; nested service calls run inside it. `MutationScope.Fail(reason)` lets code that answers with an error text instead of throwing (most MCP tools) mark the operation as failed. |
| `MutationScopeGuard.cs` | `MutationScopeProxy<T>` (a `DispatchProxy` around each service interface with marked methods), `IMutationScopeMonitor` and `MutatingMethods`. **Report mode:** a call outside a scope still runs; the first sighting per capability is logged at Information. |

Registration: [`../../Startup/WhiskersMutationGatewayExtensions.cs`](../../Startup/WhiskersMutationGatewayExtensions.cs)
(`AddGuardrailPolicy`, `AddMutationGateway`, `AddMutationScopeGuard`), called **last** in `Program.cs` so module
registrations are wrapped too.

## Pipeline

1. Unknown capability → **Denied**.
2. Tool switched off in Settings → MCP → **Denied**. A stricter stored level raises the requirement.
3. Principal level below the capability level → **Denied**.
4. Target cannot be resolved (unknown or ambiguous container) → **Denied**.
5. Policy: `Deny` → **Denied** ("Blocked by guardrails: …"); an exception → **Denied**.
6. Policy `Confirm` → [`IApprovalGate`](../Approvals/): runs only with an approval a human gave for exactly this call
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

- **Grace period.** Ownerless tasks and an ownerless AutoUpdate run with the level of the capability they run until
  an Admin takes them over or `Automation:RequireOwner` is set (INV-04 stays ⚠️ until 1.0 makes that the default).
- **Gated by convention only:** self-backup tasks (`selfbackup.create` / `selfbackup.prune`) and the DB-backup
  retention (`db.backup_delete`, a host `rm`) run through the gateway, but their services are not marked, so the
  architecture test would not catch a new direct call. `UpdateRiskService` (`image.pull`) and
  `LoopSuspensionService` (`checks.resume`) stay in the baseline.
- **Key-owned items** are evaluated with the full rule set of the key (tool lists, forbidden arguments,
  confirmation); a `Confirm` is refused, since no one can approve an unattended run. Items owned by a person, and
  ownerless items in the grace period, use the PC-D1 rules.
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
  (resolved as `read`) is flagged and included in "take over all". The previous owner goes into the audit row.
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
- **Compose deploys** (compose editor, App Store, webhook test) are decided on the project and then once per
  existing container of the project (`ComposeImpact`: compose project label or working directory). A service the
  new file adds is not an existing resource and is not decided; nor is a container of another project that a
  `container_name` in the file would collide with.
- **External webhook calls** (`/api/webhooks/...`) still run without the gateway; they move in step 2.4. Only the
  UI's **Test** button is decided, as the clicking person.
- **Kubernetes workload actions** (`workload.start` / `stop` / `restart`) are decided on the controller name the
  provider reports for the pod (and the pod name as an extra argument); there is no Kubernetes target resolver that
  reads the live owner yet.
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
