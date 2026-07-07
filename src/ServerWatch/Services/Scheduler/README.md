# Services/Scheduler

Cron-style **scheduled tasks**. A background scheduler fires due tasks, and an executor runs each one (e.g. a command, a container action). Tasks are persisted and manageable from the UI and MCP.

## Files

| File | Purpose |
|---|---|
| `ISchedulerService.cs` / `SchedulerService.cs` | Background service that tracks scheduled tasks and triggers them when due; CRUD for the task list. |
| `ITaskExecutor.cs` / `TaskExecutor.cs` | Executes a single scheduled task and records the outcome; applies backup retention (`maxBackups`) for `VolumeBackup` **and** `DbBackup` tasks. |

## Security & retention notes

- **`CustomCommand` requires Admin.** A `CustomCommand` task runs an arbitrary command through the same host executor as `execute_command` (root on the host for `serverId=local`). Creating or manually running such a task via MCP therefore requires the `execute_command` (Admin) permission, not just the `Write`-level scheduler permission — otherwise the Admin gate on `execute_command` could be bypassed.
- **Backup retention** deletes are scoped to the task's own server (`ServerId ?? "local"`) so a same-named volume on another host is never affected. `DbBackup` retention prunes host dump files `{db}_{timestamp}.sql*` in `/app/data/backups`; the DB name is charset-validated before it enters the shell.

## Related

- UI: [`../../Components/Pages/ScheduledTasks.razor`](../../Components/Pages/ScheduledTasks.razor)
- MCP tools: `list_scheduled_tasks`, `create_scheduled_task`, `delete_scheduled_task`, `run_scheduled_task`
