# Services/Mutations

The **Mutation Gateway** ([ADR-0005](../../../../docs/adr/0005-mutation-gateway.md)): the one path every
infrastructure mutation is meant to take. An entry point (MCP tool, agent, UI handler, job, webhook, terminal)
describes the mutation as a `MutationRequest`; the gateway decides, executes the supplied delegate, verifies and
records. This is WP2 **step 2.0** — the gateway exists and is tested, but **no entry point uses it yet**. Steps
2.1–2.5 move the surfaces onto it; step 2.6 makes bypassing it fail.

## Files

| File | Purpose |
|---|---|
| `MutationAttributes.cs` | `[MutatingOperation("<area>.<verb>")]` on service-interface methods that change infrastructure; `[MutationHandler]` on classes allowed to call them. |
| `MutationModels.cs` | `Capability`, `CapabilityRisk`, `MutationPrincipal`, `MutationSurface`, `MutationTarget`, `MutationRequest`, `MutationOutcome<T>`, `MutationContext`. |
| `CapabilityCatalog.cs` | `ICapabilityCatalog` — the single table of mutating capabilities (INV-13). Today it reproduces the existing levels: an MCP-exposed capability takes its tool's level from `McpPermissionLevels.DefaultToolLevels`, a UI-only one the role its handler checks. |
| `MutationGateway.cs` | `IMutationGateway` + `MutationGateway` (the pipeline) and `IMutationVerifier`. |
| `MutationPolicy.cs` | `IMutationPolicy` + `GuardrailMutationPolicy`, an adapter onto the guardrail engine. Without the engine (Agent module off) it refuses everything — fail-closed until step 2.1 moves the engine to core. |
| `MutationAudit.cs` | `IMutationAuditSink` + `AuditLogMutationSink`: writes the intent before execution and turns the same `AuditLog` row into the outcome. Unlike `AuditLogService`, it throws on a failed write. |
| `MutationScope.cs` | The ambient (`AsyncLocal`) scope the gateway opens around a delegate; nested service calls run inside it. |
| `MutationScopeGuard.cs` | `MutationScopeProxy<T>` (a `DispatchProxy` around each service interface with marked methods), `IMutationScopeMonitor` (counts calls outside a scope) and `MutatingMethods`. **Report mode:** the call still runs; the first sighting per capability is logged at Information. |

The registration lives in [`../../Startup/WhiskersMutationGatewayExtensions.cs`](../../Startup/WhiskersMutationGatewayExtensions.cs)
and runs **last** in `Program.cs`, so module registrations are wrapped too.

## Pipeline

1. Unknown capability → **Denied**.
2. Principal level below the capability level → **Denied**.
3. Policy: `Deny` → **Denied**; `Confirm` → **PendingApproval** (no approval is created yet; WP3); an exception → **Denied**.
4. Audit intent written; if that fails → **Denied** (PC-D5).
5. Delegate runs inside a `MutationScope`, cancelled at the capability's deadline; an exception or the deadline → **Failed**.
6. The capability's `IMutationVerifier`, if any → `Verified` / `Failed`; otherwise `NotVerifiable`.
7. The audit row is completed, with `CancellationToken.None` so a cancelled caller still leaves a record.

Refusals (1–4) are recorded as one row each; a failed refusal record is logged and does not change the outcome.

## Known gaps in step 2.0

- **Not yet marked** (so neither the guard nor the architecture test sees them): the generic execution primitives
  `IHostCommandExecutor` and `IDockerService.RunHostShellAsync` (they serve reads too and are split in a later
  step), which carry `host.exec`, `container.env_set` and the compose deploys done through the executor; terminals;
  self-backup restore; onboarding installs.
- A failed **outcome** write is logged and does not change the returned outcome — the mutation has already run, and the intent row remains as evidence.
- The agent invokes MCP tool methods by reflection, so its calls appear in the baseline under the tool types.
- `MutationPrincipal` maps onto `AgentPrincipal` with two kinds only; WP6 brings structured identity.

## Tests

`MutationGatewayTests` (pipeline), `MutationScopeGuardTests` (proxy, scopes, DI wrapping and disposal),
`CapabilityCatalogTests` (catalog ↔ MCP levels ↔ marked methods), `MutationGatewayBootTests` (real composition
root and `AuditLog` table) and `MutationBypassArchitectureTests`, which reads method IL and compares every call to
a marked method outside the gateway with [`docs/v1/mutation-bypass-baseline.txt`](../../../../docs/v1/mutation-bypass-baseline.txt).
That baseline may only shrink.
