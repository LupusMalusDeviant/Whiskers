# Services/Guardrails

The **code-enforced guardrail policy**. Core since ADR-0005 step 2.1 (PC-D8): it no longer switches off with the
Agent module, because it governs direct MCP calls too (INV-26). Rules are stateless and fully LLM-independent;
guardrails live in code and data, never in the prompt, so a model cannot talk its way past them.

Who asks the engine:

- **Mutations** (MCP tools and agent calls that map to a capability): the
  [Mutation Gateway](../Mutations/) through `GuardrailMutationPolicy`, for the direct MCP surface
  ([`McpGuardrailFilter`](../../Mcp/McpGuardrailFilter.cs)) and the agent ([`AgentToolInvoker`](../Agent/AgentToolInvoker.cs)).
- **Reads** over MCP: `DirectMcpCallGuard`. Reads by the agent: `AgentToolInvoker`.
- The agent session evaluates each call before it runs, to pause for a human on `Confirm`
  ([`AgentSession`](../Agent/AgentSession.cs)).

The key's scope (`McpPermissionCheck`) is checked inside each tool on top.
Aggregation is **most-restrictive-wins**: any `Deny` beats everything, otherwise a `Confirm` wins, otherwise `Allow`.

**Fail closed (INV-25).** An error while evaluating the policy never allows: a forbidden-argument pattern that is not
a valid regex denies every call, and a pattern or protected-resource glob that runs into its 100 ms timeout counts as
a match.

## Files

| File | Purpose |
|---|---|
| `IAgentGuardrailEngine.cs` | `IAgentGuardrailEngine` (evaluate a request > decision) and `IGuardrailRule` (a single testable rule), plus the `GuardrailRequest` / `GuardrailDecision` / `GuardrailVerdict` types. |
| `GuardrailEngine.cs` | Aggregates all rules with most-restrictive-wins; `CreateDefault()` wires the built-in rule set in evaluation order. |
| `BuiltInGuardrailRules.cs` | The built-in rules: `PrincipalCeilingRule` (≤ trigger rights), `ReadOnlyModeRule` (kill switch), `ToolDenyListRule`, `ToolAllowListRule`, `ProtectedResourceRule` (glob), `ForbiddenArgumentRule` (regex), `ConfirmationRule` (hybrid autonomy). |
| `IGuardrailStore.cs` | `IGuardrailStore` (load/save policy, change event) and `IGuardrailRuleCatalog` (rule descriptors for the UI). |
| `GuardrailStore.cs` | Persists the policy in its own admin-only `guardrails.json` (separate from agent settings, so a compromised settings editor cannot relax it). Warmed up at startup (order 80). Hands out and stores **copies** only; a save is validated and becomes live only after the file is written. An unreadable or invalid file (incl. a missing active preset) enforces `GuardrailPolicy.FailClosed()` until an Admin saves a valid configuration; the file is kept as `guardrails.json.unreadable-<timestamp>` (INV-25, INV-28). |
| `DirectMcpCallGuard.cs` | The gate for direct MCP calls of tools **without** a capability (reads). `Deny` blocks; `Confirm` goes to the [approval gate](../Approvals/) (approve-then-retry). Mutating tools are decided by the gateway instead. |
| `GuardrailRuleCatalog.cs` | Describes the built-in rule types for the settings UI, so the form stays generic and the rules remain extensible. |

Registration: [`Startup/WhiskersMutationGatewayExtensions.AddGuardrailPolicy`](../../Startup/WhiskersMutationGatewayExtensions.cs).

## Related

- Policy model: [`../../Configuration/GuardrailPolicy.cs`](../../Configuration/GuardrailPolicy.cs) (the configurable, restrictive-by-default policy)
- Approvals: [`../Approvals/`](../Approvals/)
- Tests: `GuardrailEngineTests`, `PolicyFailClosedTests`, `PolicyChangeAuditTests`, `DirectMcpCallGuardTests`, `GuardrailModuleIndependenceTests` in [`../../../Whiskers.Tests/`](../../../Whiskers.Tests/)
