# Modules/LogMonitor

Container-log search + the background log-alert monitor — the `/logs` page, full-text/regex search, the hosted
pattern-alert monitor and the log MCP tools. The fourth feature extracted from `AllInOnePseudoModule`
(RoadToSAP Phase 1).

- `LogMonitorModule.cs` — `Id = "logmonitor"`, enabled by default. `ConfigureServices` (moved **verbatim**
  from `Program.cs`) registers `ILogSearchService` and the `LogMonitorService` background service (singleton +
  `ILogMonitorService` forwarder + `IHostedService`). Nav: the `logs` entry ("Log-Suche", group *Übersicht*).
  MCP tools: `LogTools` (`search_logs`, `create_log_alert`, `list_log_alerts`).

**Toggle:** `Features:logmonitor:Enabled` (`Features__logmonitor__Enabled=false`), restart-only. When off: the
hosted monitor doesn't run, the `logs` nav + MCP tools disappear, and `/logs` shows a "module disabled" notice.

**Soft dependency (why a no-op is needed here, unlike Scheduler).** `ILogSearchService` has no Core consumer,
so it moves cleanly. But `ILogMonitorService` (log-alert **rule** management) is consumed by the Core
AI-triggers page ([`AiTriggers.razor`](../../Components/Pages/AiTriggers.razor)), which reads/creates rules to
back log-pattern triggers. Splitting the service isn't allowed (verbatim move), so Core registers a
[`NoopLogMonitorService`](../../Services/LogMonitor/NoopLogMonitorService.cs) default **before** the module
loop; the module's real `LogMonitorService` wins by last-registration when enabled, and the no-op keeps the
AI-triggers page working when off (rules aren't persisted and nothing scans — correct for a disabled monitor).

**DI-safe page guard.** [`LogSearch.razor`](../../Components/Pages/LogSearch.razor) is a thin route wrapper —
`<ModuleGuard ModuleId="logmonitor"><LogSearchView/></ModuleGuard>`. The interactive logic (which injects
`ILogSearchService` — the interface with no no-op — and `ILogMonitorService`) lives in the child
[`LogSearchView.razor`](../../Components/Pages/LogSearchView.razor), so a disabled module never instantiates it
and never hits a missing-service DI error. Service code stays in
[`../../Services/LogMonitor/`](../../Services/LogMonitor/), tools in
[`../../Mcp/Tools/LogTools.cs`](../../Mcp/Tools/LogTools.cs).

**Known limitation (deferred):** as with Scheduler, the in-process agent's tool catalog reflects `LogTools`
even when the module is off (fails at call time, never at boot) — the AgentToolRegistry→ModuleRegistry change
is a separate roadmap item.

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and [`docs/modules/logmonitor.md`](../../../../docs/modules/logmonitor.md).
