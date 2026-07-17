# HealthChecks

Liveness and readiness probes for orchestrators (the container `HEALTHCHECK`, Kubernetes probes, and
the guided installer). Registered via `AddHealthChecks()` in
[`../Startup/WhiskersHostingExtensions.cs`](../Startup/WhiskersHostingExtensions.cs) and exposed as two
anonymous HTTP endpoints.

## Endpoints

| Endpoint | Purpose | Checks |
|---|---|---|
| `/healthz` | **Liveness** — the process is up and serving. Never gates on dependencies, so a transient DB/registry outage does not get the container killed and restarted. | none (`Predicate = _ => false`) |
| `/readyz` | **Readiness** — safe to route traffic to. | every check tagged `ready` |

Both are anonymous, **additive** endpoint mappings — they do **not** change the auth middleware order —
and write only the aggregate status word (`Healthy` / `Degraded` / `Unhealthy`), never check names,
descriptions or exceptions, so nothing internal leaks. Note `/health` (no `z`) is the Blazor UI page.

## Files

| File | Purpose |
|---|---|
| `DbReadyCheck.cs` | Readiness: `MetricsDbContext.Database.CanConnectAsync` (a fresh context is resolved per probe through a scope). Tag `ready`. |
| `ServerConfigReadyCheck.cs` | Readiness: `IServerConfigService.IsInitialized` — the fleet registry finished loading at startup. Tag `ready`. |
| `MaintenanceReadyCheck.cs` | Readiness: `Unhealthy` while [`IMaintenanceStateService`](../Services/Maintenance/) is in maintenance (an F3 self-restore is staged and the process is about to restart), so load balancers drain. Tag `ready`. Deliberately readiness-only: `/healthz` is what the container `HEALTHCHECK` probes, so entering maintenance must never make Docker kill the app mid-restore. |

To add a readiness check: implement `IHealthCheck` and register it in
[`../Startup/WhiskersHostingExtensions.cs`](../Startup/WhiskersHostingExtensions.cs) with
`AddCheck<T>("name", tags: new[] { "ready" })`. Leave the tag off to make it liveness-only (rare).
