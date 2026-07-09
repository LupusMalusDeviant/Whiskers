# Module: scheduler

Cron-style scheduled tasks: a background scheduler that fires due tasks (container restarts, DB/volume backups,
cleanup, custom commands), an executor that runs each one, the `/tasks` management page and the scheduler MCP
tools.

| | |
|---|---|
| **Id** | `scheduler` |
| **Enabled by default** | yes |
| **Toggle** | `Features:scheduler:Enabled` (env `Features__scheduler__Enabled=false`) — restart required |
| **Depends on** | — |
| **Nav** | `tasks` — "Geplante Tasks" (group *Automatisierung*) |
| **MCP tools** | `list_scheduled_tasks`, `create_scheduled_task`, `delete_scheduled_task`, `run_scheduled_task` |
| **Services** | `ISchedulerService` (hosted `SchedulerService`) + `ITaskExecutor` |

The first module to contribute both navigation and MCP tools. When **disabled**: the hosted scheduler doesn't
run, the `tasks` nav entry and the four MCP tools disappear from the sidebar and the MCP surface, and `/tasks`
shows a "module disabled" notice (via `ModuleGuard`). No Core service depends on `ISchedulerService`, so
nothing else is affected — no no-op default is needed.

No settings section (tasks are created/managed on the `/tasks` page, not in *Settings*).

Code: [`src/Whiskers/Modules/Scheduler/`](../../src/Whiskers/Modules/Scheduler/) · service in
[`src/Whiskers/Services/Scheduler/`](../../src/Whiskers/Services/Scheduler/) · tools in
[`src/Whiskers/Mcp/Tools/SchedulerTools.cs`](../../src/Whiskers/Mcp/Tools/SchedulerTools.cs).
