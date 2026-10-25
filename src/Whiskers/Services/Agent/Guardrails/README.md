# Services/Agent/Guardrails

The agent's **code-enforced security policy**. This is the authoritative gate: it evaluates every tool call *before* execution — the in-process agent's from [`AgentToolInvoker`](../AgentToolInvoker.cs) (started from the web UI or via the MCP `instruct_agent` tool), and every direct `tools/call` from an external MCP client from [`McpGuardrailFilter`](../../../Mcp/McpGuardrailFilter.cs) via `DirectMcpCallGuard`. The key's scope (`McpPermissionCheck`) is checked on top. Rules are stateless and fully LLM-independent, guardrails live in code and data, never in the prompt, so the model cannot talk its way past them.

Aggregation is **most-restrictive-wins**: any `Deny` beats everything, otherwise a `Confirm` wins, otherwise `Allow`.

## Files

| File | Purpose |
|---|---|
| `IAgentGuardrailEngine.cs` | `IAgentGuardrailEngine` (evaluate a request > decision) and `IGuardrailRule` (a single testable rule), plus the `GuardrailRequest` / `GuardrailDecision` / `GuardrailVerdict` types. |
| `GuardrailEngine.cs` | Aggregates all rules with most-restrictive-wins; `CreateDefault()` wires the built-in rule set in evaluation order. |
| `BuiltInGuardrailRules.cs` | The built-in rules: `PrincipalCeilingRule` (≤ trigger rights), `ReadOnlyModeRule` (kill switch), `ToolDenyListRule`, `ToolAllowListRule`, `ProtectedResourceRule` (glob), `ForbiddenArgumentRule` (regex), `ConfirmationRule` (hybrid autonomy). |
| `IGuardrailStore.cs` | `IGuardrailStore` (load/save policy, change event) and `IGuardrailRuleCatalog` (rule descriptors for the UI). |
| `GuardrailStore.cs` | Persists the policy in its own admin-only `guardrails.json` (separate from agent settings, so a compromised settings editor cannot relax it). |
| `DirectMcpCallGuard.cs` | The gate for direct MCP tool calls. `Deny` blocks; `Confirm` raises a Human-in-the-Loop approval and, once a human approved it, lets the **identical** call (same caller, tool, arguments — bound by a hash) through **once** within 10 minutes (approve-then-retry, since an HTTP request cannot wait for a human the way an agent session does). |
| `GuardrailRuleCatalog.cs` | Describes the built-in rule types for the settings UI, so the form stays generic and the rules remain extensible. |

## Related

- Policy model: [`../../../Configuration/GuardrailPolicy.cs`](../../../Configuration/GuardrailPolicy.cs) (the configurable, restrictive-by-default policy)
- Tests: `GuardrailEngineTests` in [`../../../../Whiskers.Tests/`](../../../../Whiskers.Tests/)
