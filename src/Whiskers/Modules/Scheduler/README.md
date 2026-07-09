# Modules/Scheduler

Cron-style scheduled tasks — the background scheduler + task executor, the `/tasks` page and the scheduler
MCP tools. The third feature extracted from `AllInOnePseudoModule` (RoadToSAP Phase 1), and the **first module
with both a nav entry and MCP tools**.

- `SchedulerModule.cs` — `Id = "scheduler"`, enabled by default. `ConfigureServices` (moved **verbatim** from
  `Program.cs`) registers `ITaskExecutor` and the `SchedulerService` background service (singleton +
  `ISchedulerService` forwarder + `IHostedService`, via `AddSingletonWithInterfaceAndHostedService`). Nav:
  the `tasks` entry ("Geplante Tasks", group *Automatisierung*). MCP tools: `SchedulerTools`
  (`list/create/delete/run_scheduled_task`).

**Toggle:** `Features:scheduler:Enabled` (`Features__scheduler__Enabled=false`), restart-only. When off: the
hosted scheduler doesn't run, the `tasks` nav entry and the MCP tools disappear, and `/tasks` shows a "module
disabled" notice.

**Why no no-op default (unlike Notifications):** no Core service consumes `ISchedulerService` — only the
`/tasks` page and the MCP tools do. So nothing in Core breaks when the module is off; there's no consumer that
needs a fallback.

**DI-safe page guard.** [`ScheduledTasks.razor`](../../Components/Pages/ScheduledTasks.razor) is a thin route
wrapper — `<ModuleGuard ModuleId="scheduler"><ScheduledTasksView/></ModuleGuard>`. The interactive logic that
injects `ISchedulerService` lives in the child [`ScheduledTasksView.razor`](../../Components/Pages/ScheduledTasksView.razor),
so a disabled module never instantiates it and never hits a missing-service DI error. No per-page
`@rendermode` (the app renders `<Routes>` globally interactive). The service code stays in
[`../../Services/Scheduler/`](../../Services/Scheduler/) and the tools in
[`../../Mcp/Tools/SchedulerTools.cs`](../../Mcp/Tools/SchedulerTools.cs).

**Known limitation (deferred):** the in-process agent's tool catalog (`AgentToolRegistry`) still discovers the
scheduler tools by assembly reflection, so it lists them even when the module is off — calling one then fails
cleanly at call time (never at boot). Filtering the catalog by `IModuleRegistry` is a separate roadmap item
(RoadToSAP §2.3), not this PR.

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and [`docs/modules/scheduler.md`](../../../../docs/modules/scheduler.md).
