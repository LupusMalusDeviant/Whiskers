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
  ([`AgentSession`](../Agent/AgentSession.cs)); before it pauses or blocks, the gateway decides and records the call.

**Which policy (INV-23, R1-12).** Every asker uses the live active preset — except a pinned run (an AI trigger's):
its own preset, copied at the start, combined with the global kill switch (`EffectivePolicy`). Everything the run
sets off is decided under that preset with the agent rule set, also a scheduled task a person created and the
cleanup task's own protected-container check (`TaskExecutor`). A fail-closed store refuses pinned runs too.

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
| `BuiltInGuardrailRules.cs` | The built-in rules: `PrincipalCeilingRule` (≤ trigger rights), `ReadOnlyModeRule` (kill switch), `ToolDenyListRule`, `ToolAllowListRule`, `ProtectedResourceRule` (glob over the policy's patterns plus the control plane on every non-read call; Deny, or for an interactive Admin a Confirm that raises an Admin-level approval, R1-16), `ForbiddenArgumentRule` (regex), `ConfirmationRule` (hybrid autonomy). |
| `ControlPlaneResources.cs` | Whiskers' own control plane (INV-59): container name globs (`whiskers*`, `socket-proxy*`, `ghostunnel*`, `step-ca*`, `nginx-proxy-manager*`, …, plus `WHISKERS_SELF_CONTAINERS`) and paths (`/app/data`, mTLS / step-ca / ghostunnel directories, the Docker socket). Always protected, whatever the policy lists. |
| `ProtectedResourceMatcher.cs` | Finds a protected name in arguments: whole values, path-like words of every argument (against the control plane's paths), then the words and path segments of free-text and reference arguments (`command`, `query`, `yaml`, `volumes`, `mounts`, …). Commands are read as the shell reads them (`AnalyseShell`: quotes/escapes removed, quoted commands read again, globs tested); a command the shell completes at run time (`$…`, backticks, brace expansion, `eval`, `xargs`, `python -c`, …) or SQL that builds statements at run time counts as a hit (INV-24). A hit in free text never quotes its word. Its regexes have a 200 ms timeout; a timeout counts as a hit. |
| `IGuardrailStore.cs` | `IGuardrailStore` (load/save policy, change event) and `IGuardrailRuleCatalog` (rule descriptors for the UI). |
| `GuardrailStore.cs` | Persists the policy in its own admin-only `guardrails.json` (separate from agent settings, so a compromised settings editor cannot relax it). Warmed up at startup (order 80). Hands out and stores **copies** only; a save is validated and becomes live only after the file is written. An unreadable or invalid file (incl. a missing active preset) enforces `GuardrailPolicy.FailClosed()` until `guardrails.json` is repaired or removed on the host and Whiskers restarted (no UI save gets past it since R1-11); the file is kept as `guardrails.json.unreadable-<timestamp>` (INV-25, INV-28). Saves go through the gateway (`guardrails.save`); `DisableKillSwitchAsync` (`guardrails.kill_switch_off`, R1-11) only sets the active preset's `ReadOnlyMode` to false and is the one write the kill switch lets through. |
| `DirectMcpCallGuard.cs` | The gate for direct MCP calls of tools **without** a capability (reads). `Deny` blocks; `Confirm` goes to the [approval gate](../Approvals/) (approve-then-retry). Mutating tools are decided by the gateway instead. |
| `GuardrailRuleCatalog.cs` | Describes the built-in rule types for the settings UI, so the form stays generic and the rules remain extensible. |

Registration: [`Startup/WhiskersMutationGatewayExtensions.AddGuardrailPolicy`](../../Startup/WhiskersMutationGatewayExtensions.cs).

## Related

- Policy model: [`../../Configuration/GuardrailPolicy.cs`](../../Configuration/GuardrailPolicy.cs) (the configurable, restrictive-by-default policy)
- Approvals: [`../Approvals/`](../Approvals/)
- Tests: `GuardrailEngineTests`, `PolicyFailClosedTests`, `PolicyChangeAuditTests`, `DirectMcpCallGuardTests`, `GuardrailModuleIndependenceTests` in [`../../../Whiskers.Tests/`](../../../Whiskers.Tests/)
