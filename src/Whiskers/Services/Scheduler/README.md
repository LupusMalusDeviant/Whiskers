# Services/Scheduler

Cron-style **scheduled tasks**. A background scheduler fires due tasks, and an executor runs each one (e.g. a command, a container action). Tasks are persisted and manageable from the UI and MCP.

## Files

| File | Purpose |
|---|---|
| `ISchedulerService.cs` / `SchedulerService.cs` | Background service that tracks scheduled tasks and triggers them when due — fired non-blocking (`Task.Run`) with a per-task in-flight guard and `NextRun` persisted before start, so one slow task can't block or re-trigger others. A task whose cron won't parse is disabled instead of retried every 30s. Cron times are UTC. CRUD for the task list. R1-39: a run is recorded in `TaskRunHistory` (open, no `CompletedAt`) before it starts and closed when it ends, also while Whiskers stops; once stopping began (`IShutdownDrain`) no due task starts and its `NextRun` is not advanced, so it runs once after the restart; a running one is waited for within the drain budget. A run cut off by a forced stop or a crash is marked failed with `InterruptedWorkReconciler.InterruptedResult` at the next start. The in-flight guard is taken first, then the occurrence (`LastRun`/`NextRun`) and the open run are saved in one save; an occurrence that falls due while the task runs is skipped (`NextRun` only). `RunNowAsync` goes through the same drain admission and in-flight guard (refused while stopping or while the task runs, with the stable results `ShuttingDownResult` / `AlreadyRunningResult` that the page shows translated). A run whose start cannot be saved records only the error — `LastRun` stays the last real run's — and keeps the in-flight guard until that error is saved; a failed save while the scheduler does not own the run (a skipped occurrence of a running task) records no result at all — the running run's result is its own. |
| `ITaskExecutor.cs` / `TaskExecutor.cs` | Executes a single scheduled task and records the outcome; a cleanup lists the stopped containers once (`exited`, `created`, `dead` — `docker rm` refuses a paused or restarting one), decides exactly those (`containerIds`) and removes them by id (`CleanupCommandFor`), and refuses while a stopped protected container exists — one of the policy's or of Whiskers' control plane (R1-16; the gateway also decides `host.prune` on every stopped container) — judged by the policy the run is decided under (an AI trigger's pinned preset when the task runs inside its call — INV-23, R1-12 — else the live active one); applies backup retention (`maxBackups`) for `VolumeBackup` **and** `DbBackup` tasks. Every mutation runs through `IAutomationMutationRunner` on the authority of the task's owner (`CreatedBy`, `user:<email>` / `key:<id>`), re-checked per run (ADR-0005 step 2.3, PC-D6); `CreatedVia` (who created it, e.g. `agent:<session>`) goes into the run's principal as `DelegatedBy` (R1-03); a refused run fails with the reason and sends an `automation_refused` notification; when the owner lost their authority (`LostAuthority`, R1-04) the task is switched off (suspended) and an `automation_suspended` notification goes out instead — through `IScheduledTaskSuspender`, so only the owner the run was started under is suspended. Custom commands need an Admin owner (`host.exec`), cleanup `host.prune`, retention deletes `volume.backup_delete`. A run the executor refuses before it asks the gateway — no target or command, a database container that is missing, is none or cannot be read, a protected stopped container in a cleanup, a listing that fails, an unknown task type — is recorded as the gateway's Denied event of the capability it would have run (`RefuseAsync`, INV-45); nothing runs. Retention after a backup is skipped, not refused, when the database name is not safe for the shell or the list of backups cannot be read: it requests no `db.backup_delete` / `volume.backup_delete`, so it is no attempt and has no event (user decision 2026-11-07); the skip is logged and nothing is deleted. |
| `ScheduledTaskServer.cs` | The server a task acts on, one rule for the "Run now" gate, the scheduler's gates and the execution (R1-06): a task saved without a server acts on the server with the id `local`, never on "the default server". |
| `ScheduledTaskSuspender.cs` | `IScheduledTaskSuspender`: switches a task off with one conditional update (`TaskId`, `CreatedBy`, `Enabled`), so a takeover that completed while a refused run was in flight keeps the task on (R1-04). |

## Security & retention notes

- **`CustomCommand` requires Admin.** A `CustomCommand` task runs an arbitrary command through the same host executor as `execute_command` (root on the host for `serverId=local`). Creating or manually running such a task via MCP therefore requires the `execute_command` (Admin) permission, not just the `Write`-level scheduler permission — otherwise the Admin gate on `execute_command` could be bypassed.
- **Backup retention** deletes are scoped to the task's own server (`ServerId ?? "local"`) so a same-named volume on another host is never affected. `DbBackup` retention prunes host dump files `{db}_{timestamp}.sql*` in `/app/data/backups`; the DB name is charset-validated before it enters the shell.

## Wiring

The scheduler is the opt-in **Scheduler module** ([`../../Modules/Scheduler/`](../../Modules/Scheduler/),
toggle `Features:scheduler:Enabled`). Its `ConfigureServices` registers `ITaskExecutor` + the hosted
`SchedulerService`; the module also owns the `tasks` nav entry and the `SchedulerTools` MCP tools. When off,
the background loop doesn't run, the tools/nav disappear and `/tasks` shows a disabled notice (`ModuleGuard`).

## Related

- UI: [`../../Components/Pages/ScheduledTasks.razor`](../../Components/Pages/ScheduledTasks.razor) (thin
  `ModuleGuard` wrapper) → [`../../Components/Pages/ScheduledTasksView.razor`](../../Components/Pages/ScheduledTasksView.razor)
- MCP tools: `list_scheduled_tasks`, `create_scheduled_task`, `delete_scheduled_task`, `run_scheduled_task`

**Stored results (R1-24):** a run's output and error and a task's last result are redacted on save by
[`SinkRedactionInterceptor`](../Persistence/SinkRedactionInterceptor.cs). JSON output is redacted value by value and
re-written compactly; a key named after a secret (`token`, `maxTokens`, `password` …) shows `***`.
The executor cuts a run's output at 1 MB at the last complete line (or word), so no half-cut credential is stored.
