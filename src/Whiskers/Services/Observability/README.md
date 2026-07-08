# Observability

Governance recording for the agent/MCP layer, **every** tool call Whiskers executes is captured here so it can be reviewed in the **Agent-History** dashboard (`/agent-history`).

## What gets recorded

For each call: timestamp, actor + actor type, tool name, required permission level, secret-redacted parameters, the guardrail verdict (`allow` / `confirm` / `deny`), success, duration, an optional result summary, server id and any error. The entity is [`McpToolCallEntity`](../../Models/McpToolCall.cs); it lives in the `McpToolCalls` table of [`MetricsDbContext`](../Persistence/MetricsDbContext.cs) with the same 90-day retention as the audit log (pruned in [`MetricsCollectorService`](../Metrics/MetricsCollectorService.cs)).

## Two recording points (so nothing is missed)

| Path | Recorded by | Actor type |
|---|---|---|
| In-process agent (web chat, `instruct_agent`, AI triggers) | [`AgentToolInvoker`](../Agent/AgentToolInvoker.cs): at every return path, with full params + verdict + result + duration | `agent-web`, `agent-mcp`, `trigger` |
| External / direct MCP `tools/call` (e.g. Claude Code calling a tool itself) | [`McpCallLogMiddleware`](../../Mcp/McpCallLogMiddleware.cs): sniffs the JSON-RPC body on `POST /mcp` | `mcp-direct` |

## Files

| File | Purpose |
|---|---|
| `McpCallLogStore.cs` | `IMcpCallLogStore` + `McpCallLogStore`, records and queries tool-call entries. Writes through a scoped `MetricsDbContext` (safe to call from singletons). Query filters: actor, tool, verdict, writes-only, since. |

## Related

- Dashboard: [`../../Components/Pages/AgentHistory.razor`](../../Components/Pages/AgentHistory.razor)
- Secret redaction: [`../../Utils/SecretRedactor.cs`](../../Utils/)
- Guardrail verdicts: [`../Agent/Guardrails/`](../Agent/Guardrails/)
