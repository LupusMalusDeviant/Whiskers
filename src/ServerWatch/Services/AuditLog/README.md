# Services/AuditLog

An append-only **audit trail** of privileged actions, who did what, when, and through which surface (web user or MCP key / agent). Every write/admin action records an actor derived from the request context.

## Files

| File | Purpose |
|---|---|
| `IAuditLogService.cs` / `AuditLogService.cs` | Records audit entries and extracts the actor (name + type, e.g. `("user@example.com", "web")` or `("Claude Code", "mcp")`) from an HTTP context. |

## Behaviour notes

- **Fail-safe:** if the audit write fails (DB locked, disk full) the full entry (actor/action/target/server/success/details) is logged at **Error** in the app log — the fact is never silently dropped while the privileged action proceeds.
- **Secret-safety:** entries store only key **names**, `SecretRedactor`-redacted commands, and never secret values, DB passwords, or issued credentials — e.g. `hetzner.rescue_enable` records that a root credential was issued, not the password.
- **Coverage** includes config saves, vault `vault.set`/`vault.delete`, scheduler `scheduler.create`/`run`/`delete`, `command.execute`, and `hetzner.rescue_enable`.

## Related

- Actor resolution mirrors [`../Mcp/`](../Mcp/) and the agent's synthetic context ([`../Agent/AgentToolInvoker.cs`](../Agent/AgentToolInvoker.cs))
- UI: [`../../Components/Pages/AuditLog.razor`](../../Components/Pages/AuditLog.razor)
