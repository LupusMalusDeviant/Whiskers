# Services/Scheduler

Cron-style **scheduled tasks**. A background scheduler fires due tasks, and an executor runs each one (e.g. a command, a container action). Tasks are persisted and manageable from the UI and MCP.

## Files

| File | Purpose |
|---|---|
| `ISchedulerService.cs` / `SchedulerService.cs` | Background service that tracks scheduled tasks and triggers them when due; CRUD for the task list. |
| `ITaskExecutor.cs` / `TaskExecutor.cs` | Executes a single scheduled task and records the outcome. |

## Related

- UI: [`../../Components/Pages/ScheduledTasks.razor`](../../Components/Pages/ScheduledTasks.razor)
- MCP tools: `list_scheduled_tasks`, `create_scheduled_task`, `delete_scheduled_task`, `run_scheduled_task`
