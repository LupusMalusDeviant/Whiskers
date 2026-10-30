# Services/AuditLog

An append-only **audit trail** of privileged actions, who did what, when, and through which surface (web user or MCP key / agent). Every write/admin action records an actor derived from the request context.

## Files

| File | Purpose |
|---|---|
| `IAuditLogService.cs` / `AuditLogService.cs` | Records audit entries and extracts the actor (name + type, e.g. `("user@example.com", "web")` or `("Claude Code", "mcp")`) from an HTTP context. |
| `TryLogAsync` | Like `LogAsync`, and returns whether the row was stored — for a caller that must not go on without it (the self-backup download, R1-22). |
| `ToolAuditLog.cs` | `IToolAuditLog`, what MCP tools write about their own work (R1-20). Inside a gateway mutation the report enriches the gateway's event (`MutationScope.Record`: the `results` of its outcome) instead of becoming a second security event, and a reported failure fails the mutation; outside one it is an ordinary `LogAsync` row. |

## Behaviour notes

- **Fail-safe:** if the audit write fails (DB locked, disk full) the full entry (actor/action/target/server/success/details) is logged at **Error** in the app log — the fact is never silently dropped while the privileged action proceeds.
- **Secret-safety:** entries store only key **names**, `SecretRedactor`-redacted commands, and never secret values, DB passwords, or issued credentials — e.g. `hetzner.rescue_enable` records that a root credential was issued, not the password.
- **Coverage** includes config saves, vault `vault.set`/`vault.delete`, scheduler `scheduler.create`/`run`/`delete`, `command.execute`, and `hetzner.rescue_enable`.

- **One event per mutation (R1-20):** MCP tools take `IToolAuditLog`, not `IAuditLogService`, so a tool call through the gateway is exactly one audit event — the gateway's — carrying the tool's own details.
- **Gateway writes are different:** the Mutation Gateway writes through its own `AuditLogMutationSink` ([`../Mutations/`](../Mutations/)), which writes the intent *before* execution and **throws** on a failed write, so the mutation is refused (PC-D5).
- **Sign-ins and downloads (R1-22):** `auth.login`, `auth.login_failed`, `auth.logout` are written by
  [`../Auth/AuthAuditLog.cs`](../Auth/AuthAuditLog.cs); `selfbackup.download` by the download endpoint, fail-closed
  through `TryLogAsync` — one row per request, so a browser resuming a large archive with range requests writes
  several. A queued sign-in row keeps the time it happened (`TryLogAtAsync`).
- **A lost row is visible (R1-19):** `LogAsync` still logs and swallows a failed write (the action already happened),
  and tells `IAuditRecovery`, so the `audit` readiness check is Degraded for an hour.

## Related

- Actor resolution mirrors [`../Mcp/`](../Mcp/) and the agent's synthetic context ([`../Agent/AgentToolInvoker.cs`](../Agent/AgentToolInvoker.cs))
- UI: [`../../Components/Pages/AuditLog.razor`](../../Components/Pages/AuditLog.razor)
