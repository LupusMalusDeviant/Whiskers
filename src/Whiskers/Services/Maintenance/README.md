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
| `IMaintenanceStateService.cs` / `MaintenanceStateService.cs` | Core singleton holding a `volatile bool` + a human-readable reason. `EnterMaintenance(reason)` is idempotent; `ExitMaintenance()` exists only for the pre-commit restore abort. |

## Wiring

Registered as a **Core** singleton in [`../../Startup/WhiskersHostingExtensions.cs`](../../Startup/WhiskersHostingExtensions.cs)
— deliberately not a module, since a restore must be gateable in every configuration. Three consumers:

- **The request gate** — an inline middleware in [`../../Startup/WhiskersPipelineExtensions.cs`](../../Startup/WhiskersPipelineExtensions.cs)
  at the same seam as the W1 setup redirect (after `UseAntiforgery()`, before `UseAuthentication()`, and
  in *both* auth modes). It answers 503 + a self-refreshing page for non-exempt top-level HTML
  navigations; the exempt allowlist lives in [`../../Startup/MaintenancePaths.cs`](../../Startup/MaintenancePaths.cs).
  Additive — it does not touch the auth chain.
- **The readiness drain** — [`../../HealthChecks/MaintenanceReadyCheck.cs`](../../HealthChecks/MaintenanceReadyCheck.cs)
  reports `Unhealthy` while in maintenance. It carries the `ready` tag only, so it gates `/readyz`
  (load balancers/orchestrators drain) but **not** `/healthz`: the container HEALTHCHECK probes
  liveness, so entering maintenance can never make Docker kill the app mid-restore.
- **The writer** — [`../Backup/BackupService.cs`](../Backup/) enters maintenance before taking the
  pre-restore safety backup and staging the restore, and exits it again if the restore aborts before
  its commit point.

## Related

- [`../Backup/`](../Backup/) — the self-backup/restore that drives this flag.
- [`../../Startup/`](../../Startup/) — the middleware seam and the path allowlist.
- [`../../HealthChecks/`](../../HealthChecks/) — the readiness drain.
