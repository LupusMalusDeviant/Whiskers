# Services/Mutations

The **Mutation Gateway** ([ADR-0005](../../../../docs/adr/0005-mutation-gateway.md)): the one path every
infrastructure mutation is meant to take. An entry point describes the mutation as a `MutationRequest`; the gateway
decides, executes the supplied delegate, verifies and records.

**State: WP2 step 2.1.** MCP tools and the agent run every mutating tool through the gateway:

- direct MCP calls in [`McpGuardrailFilter`](../../Mcp/McpGuardrailFilter.cs) (surface `Mcp`);
- agent tool calls in [`AgentToolInvoker`](../Agent/AgentToolInvoker.cs) (surface `Agent`, the principal acting
  `OnBehalfOf` the user, key or trigger that started it).

A tool counts as mutating when the catalog lists it (`ICapabilityCatalog.FindByMcpTool`). UI handlers (2.2),
scheduler / AutoUpdate / AutoPatch (2.3), webhooks / Git deploy (2.4) and terminals (2.5) follow; step 2.6 makes
bypassing the gateway fail.

## Files

| File | Purpose |
|---|---|
| `MutationAttributes.cs` | `[MutatingOperation("<area>.<verb>")]` on service-interface methods that change infrastructure; `[MutationHandler]` on classes allowed to call them. |
| `MutationModels.cs` | `Capability`, `CapabilityRisk`, `MutationPrincipal` (incl. `OnBehalfOf`, `AllowedTools`), `MutationSurface`, `MutationTarget`, `MutationRequest`, `MutationOutcome<T>` (incl. `ApprovalId`, `MatchedRules`), `MutationContext`. |
| `CapabilityCatalog.cs` | `ICapabilityCatalog` — the single table of mutating capabilities (INV-13). An MCP-exposed capability takes its tool's level from `McpPermissionLevels.DefaultToolLevels`, a UI-only one the role its handler checks. |
| `CapabilityOverrides.cs` | `ICapabilityOverrides` + `McpToolCapabilityOverrides`: the MCP tool's Enabled switch and a stricter stored level (Settings → MCP), applied for **every** principal kind (INV-15). The catalog level is the floor. |
| `MutationTargets.cs` | `MutationTargets.FromArguments` (target from tool arguments) and `IMutationTargetResolver` + `MutationTargetResolver`: containers resolve by id, name or unique prefix to their full id and name; an unknown or ambiguous container is refused before policy (INV-57). Other kinds pass through for now. |
| `MutationGateway.cs` | `IMutationGateway` + `MutationGateway` (the pipeline) and `IMutationVerifier`. |
| `MutationPolicy.cs` | `IMutationPolicy` + `GuardrailMutationPolicy`, the adapter onto the core guardrail engine. For an agent it evaluates the principal the agent acts for, with that key's tool list. The rules also see the resolved target (`__resolvedTargetId`, `__resolvedTargetName`), so a protected container is caught by id prefix too. Without engine or store it refuses everything. |
| `MutationAudit.cs` | `IMutationAuditSink` + `AuditLogMutationSink`: writes the intent (with the approval id and approver) before execution and turns the same `AuditLog` row into the outcome. Unlike `AuditLogService`, it throws on a failed write. |
| `MutationScope.cs` | The ambient (`AsyncLocal`) scope the gateway opens around a delegate; nested service calls run inside it. |
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
7. Audit intent written; if that fails → **Denied** (PC-D5).
8. Delegate runs inside a `MutationScope`, cancelled at the capability's deadline; an exception or the deadline → **Failed**.
9. The capability's `IMutationVerifier`, if any → `Verified` / `Failed`; otherwise `NotVerifiable`.
10. The audit row is completed, with `CancellationToken.None` so a cancelled caller still leaves a record.

Refusals (1–7) are recorded as one row each; a failed refusal record is logged and does not change the outcome.
Over MCP a tool that throws is rethrown, so the client sees the same error as before the gateway.

## Known gaps after step 2.1

- **Two audit rows per MCP or agent mutation.** The gateway row carries decision, approval, intent and outcome; the
  tool still writes its own row with the domain result (many tools report a failure as text, not as an exception, so
  only the tool knows it). One event per attempt (INV-45) needs typed tool outcomes and verifiers.
- **Executed means "the tool returned".** A tool that returns an error text is recorded as `Executed` by the gateway;
  the tool's own row has the real result.
- **Deadlines** reach a tool through its `CancellationToken` parameter, over MCP and for the agent. A tool that has
  none, or work it starts in the background, is not stopped (INV-40, WP9).
- **Calls from a tool to another capability** stay in the baseline: `deploy_app` creating a container and pulling an
  image, `preview_auto_patch` starting a background cycle (`autopatch.run`). The gateway decided on the tool's
  capability only; the background cycle also outlives the gateway's deadline and outcome row.
- **Approvals:** an approval is used before the audit intent is written, so a failed intent spends it (the mutation
  is still refused). Two concurrent first calls raise two approvals of which only one can be used. The agent session
  pauses on the code level, so a stricter stored level makes the gateway ask for an approval the session did not —
  the agent is told to repeat the call.
- **Not yet marked** (so neither the guard nor the architecture test sees them): the generic execution primitives
  `IHostCommandExecutor` and `IDockerService.RunHostShellAsync`, terminals, self-backup restore, onboarding installs.
- **Approvals are in memory.** Persistence, execution-time expiry and no-self-approval follow in WP3.
- `MutationPrincipal` maps onto `AgentPrincipal` for the policy; WP6 brings structured identity.

## Tests

`MutationGatewayTests` (pipeline), `ToolAuthorizationTests` (tool switch and stricter level for every principal,
target resolution, session approval), `ApprovalGateTests`, `MutationScopeGuardTests`, `CapabilityCatalogTests`,
`MutationGatewayBootTests` (real composition root and `AuditLog` table), `GuardrailModuleIndependenceTests` (Agent
module off) and `MutationBypassArchitectureTests`, which reads method IL and compares every call to a marked method
outside the gateway with [`docs/v1/mutation-bypass-baseline.txt`](../../../../docs/v1/mutation-bypass-baseline.txt).
An MCP tool mapped to a capability counts as inside the gateway, because both dispatchers run it there. The baseline
may only shrink: 104 lines after step 2.0, 75 after step 2.1.
