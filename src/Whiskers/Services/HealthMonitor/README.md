# Services/HealthMonitor

Background **container health watching**. Continuously tracks container health state across the fleet, raises notifications on transitions (unhealthy / stopped / OOM / restart loops), watches whether the servers themselves still answer, and keeps a short health history for the UI's health reports.

## Files

| File | Purpose |
|---|---|
| `ContainerHealthMonitor.cs` | The background service: polls container health, detects state transitions and restart loops, and fires notifications (the per-container mute/prefs are applied centrally in the composite notification service). R1-41: a failed cycle is logged and the next one runs; only the host's stop ends the loop. The cycle is owned to its end (no `WaitAsync` abandoning it): the stop reaches the fleet listing, every state inspect and every notification, and the cycle checks it before each effect, so a listing that answers after the stop records no cycle metric, no health record, no notification and no broadcast. The interval is the shared `EffectiveInterval<HealthMonitorSettings>` (5–86400 s): read live, so a saved value applies from the next cycle; a value out of range is clamped at start (with a warning) and ignored after a reload (the last valid one stays); only zero or a negative value refuses the start. |
| `ServerReachabilityTracker.cs` | Turns the per-cycle "which servers answered" signal into `server_unreachable` / `server_recovered` events, with a consecutive-failure threshold (`HealthMonitor:ServerUnreachableCycles`, default 2) so a single slow cycle is not an outage, and a longer cold-start grace (`ServerUnreachableColdStartCycles`, default 10) for servers that have not answered once since startup. R1-41: a server marked `NotAsked` (the listing ran out of time in Whiskers' own budget queue, so the server was never reached) neither extends nor resets its streak. |
| `IHealthStore.cs` / `InMemoryHealthStore.cs` | Stores recent health state/history per container for reports and the dashboard. |

## Why reachability lives here

A host that stops answering used to be silent: the dashboard marked it unreachable, but nothing was sent —
and every container alert and log-alert rule covering that host quietly stopped producing anything, which
is indistinguishable from "all quiet". The monitor already lists the whole fleet each cycle, so it gets the
signal for free via `ListAllContainersDetailedAsync`.

The cold-start grace is not cosmetic: remote connections (SSH tunnels, mTLS sessions) are not up in the
first cycles after a restart, so without it a six-server fleet produced ten notifications on every deploy
(five down, five back up) — which is precisely how an alert becomes noise people ignore. An outage is a
transition; a host that has never answered yet has not transitioned.

The same signal guards the pruning of the per-container maps: state belonging to a server that did **not**
answer is kept, otherwise a recovered host looks brand new and its outage window is never evaluated.

## Related

- Notification dispatch + per-container prefs: [`../Notifications/`](../Notifications/)
- UI: [`../../Components/Pages/HealthReports.razor`](../../Components/Pages/HealthReports.razor), [`Dashboard.razor`](../../Components/Pages/Dashboard.razor)
- MCP tool: `get_health_summary`
