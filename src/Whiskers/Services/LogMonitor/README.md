# Services/LogMonitor

Log search and **pattern-based log alerts**. Search container logs on demand, and define alert rules that fire a notification when a matching line appears.

## Files

| File | Purpose |
|---|---|
| `ILogSearchService.cs` / `LogSearchService.cs` | Full-text / regex search across container logs. A long matched line is shortened at a complete word (`ShellUtils.CutAtBoundary`, also for an alert's matched line), never inside a credential (R1-24). |
| `ILogMonitorService.cs` / `LogMonitorService.cs` | Background log-pattern monitor; manages the alert rules and raises notifications on matches. |
| `NoopLogMonitorService.cs` | Core default `ILogMonitorService` (no rules, no monitor). Registered before the module loop so the AI-triggers page still resolves it when the **LogMonitor module** is off; the real `LogMonitorService` wins by last-registration when on (RoadToSAP Phase 1). |

## Scan scope

The monitor scans **every enabled Docker server**, not just the default one (`ListAllContainersAsync`, the
same fleet-wide list `ContainerHealthMonitor` uses). Consequences worth knowing:

- Hosts are scanned in parallel, the containers of one host sequentially; each log fetch is bounded by a
  15 s timeout so one wedged connection can't stall the cycle.
- Watermarks (`_lastLogCheck`) and cooldowns are keyed by `{serverId}:{containerId}` — container ids are
  unique per host only.
- A rule counts as "all containers" only when **both** `ContainerId` and `ContainerName` are null. A
  name-only filter (what the UI dialog and `create_log_alert` produce) matches that name on **every**
  server; there is no per-server rule scope yet — that would need a new column on `LogAlertRuleEntity`.
- The self-log guard (`WHISKERS_SELF_CONTAINERS`, legacy `SERVERWATCH_SELF_CONTAINERS`; default `whiskers,serverwatch`) only applies to the server
  Whiskers itself runs on (`ConnectionType.Local`, else the default server) — a namesake on a remote host
  is a different process and stays monitored.

`LogSearchService` follows the same rule: without an explicit `serverId` it searches every server and
fetches each container's logs from its own host; results carry `ServerId`/`ServerName` because container
names repeat across a fleet.

Per cycle a container transfers at most `TailLines` (200) lines: the Docker call applies the tail limit
even when a `since` watermark is set, so one very chatty container can no longer pull its whole burst over
a remote connection every minute.

## Wiring

This is the opt-in **LogMonitor module** ([`../../Modules/LogMonitor/`](../../Modules/LogMonitor/), toggle
`Features:logmonitor:Enabled`): its `ConfigureServices` registers `ILogSearchService` + the hosted
`LogMonitorService`, and the module owns the `logs` nav entry and the `LogTools` MCP tools. `ILogSearchService`
has no Core consumer; `ILogMonitorService` does (the AI-triggers page), so Core keeps the
`NoopLogMonitorService` default above for when the module is off.

## Related

- Notification dispatch: [`../Notifications/`](../Notifications/)
- UI: [`../../Components/Pages/LogSearch.razor`](../../Components/Pages/LogSearch.razor) (thin `ModuleGuard`
  wrapper) → [`../../Components/Pages/LogSearchView.razor`](../../Components/Pages/LogSearchView.razor)
- MCP tools: `search_logs`, `list_log_alerts`, `create_log_alert`
- Audit (R1-22): create, toggle and delete run under one lock and record the rule's versioned diff ([`../Mutations/ConfigChange.cs`](../Mutations/ConfigChange.cs)); the pattern
  shows as text only, the trigger counters are not part of it.
