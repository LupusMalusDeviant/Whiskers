# Services/Mutations

The **Mutation Gateway** ([ADR-0005](../../../../docs/adr/0005-mutation-gateway.md)): the one path every
infrastructure mutation is meant to take. An entry point describes the mutation as a `MutationRequest`; the gateway
decides, executes the supplied delegate, verifies and records.

**State: WP2 step 2.2.** MCP tools, the agent and the UI run every mutation through the gateway:

- direct MCP calls in [`McpGuardrailFilter`](../../Mcp/McpGuardrailFilter.cs) (surface `Mcp`);
- agent tool calls in [`AgentToolInvoker`](../Agent/AgentToolInvoker.cs) (surface `Agent`, the principal acting
  `OnBehalfOf` the user, key or trigger that started it);
- UI handlers through [`IUiMutationRunner`](UiMutationRunner.cs) (surface `Ui`, the signed-in user as a `Human`
  principal).

A tool counts as mutating when the catalog lists it (`ICapabilityCatalog.FindByMcpTool`). Scheduler / AutoUpdate /
AutoPatch (2.3), webhooks / Git deploy (2.4) and terminals (2.5) follow; step 2.6 makes bypassing the gateway fail.

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

## Files

| File | Purpose |
|---|---|
| `MutationAttributes.cs` | `[MutatingOperation("<area>.<verb>")]` on service-interface methods that change infrastructure; `[MutationHandler]` on classes allowed to call them. |
| `MutationModels.cs` | `Capability`, `CapabilityRisk`, `MutationPrincipal` (incl. `OnBehalfOf`, `AllowedTools`), `MutationSurface`, `MutationTarget`, `MutationRequest`, `MutationOutcome<T>` (incl. `ApprovalId`, `MatchedRules`), `MutationContext`. |
| `CapabilityCatalog.cs` | `ICapabilityCatalog` — the single table of mutating capabilities (INV-13). An MCP-exposed capability takes its tool's level from `McpPermissionLevels.DefaultToolLevels`, a UI-only one the role its handler checks. |
| `CapabilityOverrides.cs` | `ICapabilityOverrides` + `McpToolCapabilityOverrides`: the MCP tool's Enabled switch and a stricter stored level (Settings → MCP), applied for **every** principal kind (INV-15). The catalog level is the floor. |
| `MutationTargets.cs` | `MutationTargets.FromArguments` (target from tool arguments) and `IMutationTargetResolver` + `MutationTargetResolver`: containers resolve by id, name or unique prefix to their full id and name; an unknown or ambiguous container is refused before policy (INV-57). Other kinds pass through for now. |
| `MutationGateway.cs` | `IMutationGateway` + `MutationGateway` (the pipeline) and `IMutationVerifier`. |
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

## Known gaps after step 2.2

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
  capability only; the background cycle also outlives the gateway's deadline and outcome row.
- **Not yet marked** (so neither the guard nor the architecture test sees them): the generic execution primitives
  `IHostCommandExecutor` and `IDockerService.RunHostShellAsync`, terminals, self-backup restore, onboarding installs
  and server records. The UI actions built on the primitives (container `.env` save + compose up, Artisan
  migrate/rollback, SQL file import, compose editor and App Store deploys) run through the gateway since the step 2.2
  review, but only by convention: a new direct call is not caught until the primitives are marked.
- **Kubernetes workload actions** (`workload.start` / `stop` / `restart`) are decided on the workload id and name as
  given; there is no Kubernetes target resolver yet, so a protected pattern must match the workload name.
- **Free-text arguments** (`query`, `command`, `content`, `env`, …) are kept out of the audit row
  (`AuditLogMutationSink.PayloadArguments`, only their length is stored); the policy and the approval card still see
  them.
- **A UI action that outlives its deadline** keeps running when the service call ignores the token (most do); the
  page then shows a timeout while the work continues.
- **Approvals are in memory.** Persistence, execution-time expiry and no-self-approval follow in WP3.
- `MutationPrincipal` maps onto `AgentPrincipal` for the policy; WP6 brings structured identity.

## Tests

`MutationGatewayTests` (pipeline), `UiMutationRunnerTests` (UI principal, PC-D1 rules, PC-D3), `ToolAuthorizationTests` (tool switch and stricter level for every principal,
target resolution, session approval, approval used only after the intent, reported tool failures over MCP and for
the agent), `ApprovalGateTests`, `MutationScopeGuardTests`, `CapabilityCatalogTests`,
`MutationGatewayBootTests` (real composition root and `AuditLog` table), `GuardrailModuleIndependenceTests` (Agent
module off) and `MutationBypassArchitectureTests`, which reads method IL and compares every call to a marked method
outside the gateway with [`docs/v1/mutation-bypass-baseline.txt`](../../../../docs/v1/mutation-bypass-baseline.txt).
An MCP tool mapped to a capability counts as inside the gateway, because both dispatchers run it there. The baseline
may only shrink: 104 lines after step 2.0, 75 after step 2.1, 29 after step 2.2.
