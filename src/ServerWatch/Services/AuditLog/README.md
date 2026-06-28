# Services/AuditLog

An append-only **audit trail** of privileged actions, who did what, when, and through which surface (web user or MCP key / agent). Every write/admin action records an actor derived from the request context.

## Files

| File | Purpose |
|---|---|
| `IAuditLogService.cs` / `AuditLogService.cs` | Records audit entries and extracts the actor (name + type, e.g. `("user@example.com", "web")` or `("Claude Code", "mcp")`) from an HTTP context. |

## Related

- Actor resolution mirrors [`../Mcp/`](../Mcp/) and the agent's synthetic context ([`../Agent/AgentToolInvoker.cs`](../Agent/AgentToolInvoker.cs))
- UI: [`../../Components/Pages/AuditLog.razor`](../../Components/Pages/AuditLog.razor)
