# Services/AuditLog

An append-only **audit trail** of privileged actions, who did what, when, and through which surface (web user or MCP key / agent). Every write/admin action records an actor derived from the request context.

## Files

| File | Purpose |
|---|---|
| `IAuditLogService.cs` / `AuditLogService.cs` | Records audit entries and extracts the actor (name + type, e.g. `("user@example.com", "web")` or `("Claude Code", "mcp")`) from an HTTP context. |
| `TryLogAsync` | Like `LogAsync`, and returns whether the row was stored — for a caller that must not go on without it (the self-backup download, R1-22). |
| `AuditCheckpointService.cs` | Signs the chain's end every minute, at its own stop and once more after every hosted service has stopped (`ApplicationStopped`). |
| `AuditChain.cs` | `IAuditChain`, the tamper-evident trail (R1-23, [ADR-0006](../../../../docs/adr/0006-audit-hash-chain.md)): `WriteAsync` runs a row write and appends each written row's version to the SHA-256 chain in one transaction under one lock; `CheckpointAsync` signs the chain's end (HMAC-SHA-256, key derived from `VAULT_KEY` with HKDF) in `AuditCheckpoints` and `audit-checkpoint.json`; `VerifyAsync` checks links, rows and checkpoints in one snapshot; `PruneAsync` is retention; `ExportAsync` writes the export. `SealExistingAsync` seals the rows of a database from before the chain once (genesis) and refuses when the checkpoint file says a chain was there. |
| `AuditExport.cs` | The export document (`whiskers-audit-export/v1`): verification result, checkpoints, chain, rows. |
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

- **Tamper evidence (R1-23):** every writer — `AuditLogService`, the gateway's intent and outcome rows, the outbox drain
  and settle in [`../Mutations/`](../Mutations/) — writes through `IAuditChain.WriteAsync`, so a row never exists
  without its link. Never write `AuditLog` directly: verification reports such a row as added outside the trail.
  An existing row (an intent turned into its outcome) is changed only through `IAuditChain.UpdateAsync`, which locks
  and checks it against its newest link first and refuses a row changed in the database.
  With `VAULT_KEY` every link and checkpoint is signed (HMAC); a link added outside Whiskers is reported at once. On a
  signed trail a process without `VAULT_KEY` refuses audit writes (fail closed), so mutations are refused until the
  key is back. Limits ([ADR-0006](../../../../docs/adr/0006-audit-hash-chain.md)): a host compromise holding
  `VAULT_KEY` can rewrite everything; without `VAULT_KEY` nothing is signed, so rows and links can be rewritten
  consistently and a cut-off end goes unnoticed; events after the newest checkpoint (≤ 1 min) can be cut off
  unnoticed. Verification findings carry a code (`AuditFinding_*` resources) for the page's language.
- **Retention (R1-23):** `Audit:RetentionDays` (default 365, at least 30; [`AuditSettings`](../../Configuration/AuditSettings.cs)),
  run daily by [`../Metrics/MetricsCollectorService.cs`](../Metrics/MetricsCollectorService.cs): the trail is verified
  first, outside the chain lock (nothing is removed from a trail that does not verify; under the lock only what
  changed since is checked again), then an `audit.retention` event, then the old rows,
  the old MCP call history and the chain prefix naming only removed rows, then a signed `retention` checkpoint as the
  new start.
- **Readers (R1-23):** the Audit log and Agent History pages are Admin only; the Audit log page verifies the trail on
  demand and exports it via `GET /audit/export` (Admin only; `audit.export` is recorded before anything is read, 503
  without it).

## Related

- Actor resolution mirrors [`../Mcp/`](../Mcp/) and the agent's synthetic context ([`../Agent/AgentToolInvoker.cs`](../Agent/AgentToolInvoker.cs))
- UI: [`../../Components/Pages/AuditLog.razor`](../../Components/Pages/AuditLog.razor)
