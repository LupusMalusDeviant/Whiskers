# Services/Maintenance

The process-wide **maintenance flag** (F3 self-restore). It is set for the short window between a
restore being committed and the process restarting, so the app answers **503** instead of serving
requests against state that is about to be swapped out from under it.

**In-memory only, by design.** No mirror file, no `IInitializable` warm-up: a committed restore always
ends in a process restart, and the deferred file swap runs in `Program.cs` *before* the host is built —
so a freshly (re)started process is never in maintenance. The only way out without a restart is
`ExitMaintenance()`, used solely by the restore's **abort path**: a restore that fails before its commit
point (e.g. the pre-restore safety backup could not be written) must not strand the app in 503.

## Files

| File | Purpose |
|---|---|
| `IMaintenanceStateService.cs` / `MaintenanceStateService.cs` | Core singleton holding a `volatile bool` + a human-readable reason, and the work admitted while it is off (`TryEnter`, `InFlight`, `WaitForIdleAsync`; entering and admitting are decided under one lock). `EnterMaintenance(reason)` is idempotent; `ExitMaintenance()` exists only for the pre-commit restore abort. |

## Wiring

Registered as a **Core** singleton in [`../../Startup/WhiskersHostingExtensions.cs`](../../Startup/WhiskersHostingExtensions.cs)
— deliberately not a module, since a restore must be gateable in every configuration. Five kinds of consumer:

- **The request gate** — an inline middleware in [`../../Startup/WhiskersPipelineExtensions.cs`](../../Startup/WhiskersPipelineExtensions.cs)
  at the same seam as the W1 setup redirect (after `UseAntiforgery()`, before `UseAuthentication()`, and
  in *both* auth modes). It answers 503 + a self-refreshing page for non-exempt top-level HTML
  navigations; the exempt allowlist lives in [`../../Startup/MaintenancePaths.cs`](../../Startup/MaintenancePaths.cs).
  Additive — it does not touch the auth chain.
- **The readiness drain** — [`../../HealthChecks/MaintenanceReadyCheck.cs`](../../HealthChecks/MaintenanceReadyCheck.cs)
  reports `Unhealthy` while in maintenance. It carries the `ready` tag only, so it gates `/readyz`
  (load balancers/orchestrators drain) but **not** `/healthz`: the container HEALTHCHECK probes
  liveness, so entering maintenance can never make Docker kill the app mid-restore.
- **The Mutation Gateway** — [`../Mutations/MutationGateway.cs`](../Mutations/MutationGateway.cs) admits every
  top-level operation through `TryEnter` and refuses it while the flag is set (`MaintenanceRefusal`, `Denied`), before
  anything is decided, run or written — whatever the path: MCP, the agent, an open UI circuit (`/_blazor` stays
  exempt from the request gate), the AutoUpdate/AutoPatch loops, a webhook run queued before the flag was set. The
  refusal is logged, not audited: the database it would land in is replaced at the restart. An operation nested in
  an admitted one runs on its admission.
- **The early refusals** — the entry points also refuse on their own, with their own answer:
  [`../../Mcp/McpGuardrailFilter.cs`](../../Mcp/McpGuardrailFilter.cs) answers every tool call with
  `MaintenanceRefusal`; [`../Webhooks/WebhookService.cs`](../Webhooks/WebhookService.cs) answers a delivery whose
  signature is valid with 503 `maintenance` + `Retry-After` and records nothing (an unsigned or forged one is rejected
  as always, so a stranger cannot learn of the restore); [`../Scheduler/SchedulerService.cs`](../Scheduler/SchedulerService.cs)
  starts no due task (consuming no occurrence) and refuses "Run now". A scheduled run holds its own `TryEnter`
  entry from start to end, so the restore also waits for the steps between its gateway operations.
- **The writer** — [`../Backup/BackupService.cs`](../Backup/) enters maintenance (nothing new is admitted from then
  on), then waits for the admitted work to end (`WaitForIdleAsync`, at most `QuiesceBudget` = the shutdown drain's
  30 s; the restore's own gateway operation is not waited for) and only then takes the pre-restore safety backup.
  Work that does not end in time refuses the restore: maintenance is left, nothing is staged, and the refusal is the
  restore operation's failed outcome in the audit trail. It also exits maintenance if the restore aborts before its
  commit point for any other reason.

## Related

- [`../Backup/`](../Backup/) — the self-backup/restore that drives this flag.
- [`../../Startup/`](../../Startup/) — the middleware seam and the path allowlist.
- [`../../HealthChecks/`](../../HealthChecks/) — the readiness drain.
