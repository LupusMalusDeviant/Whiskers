using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Database;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Server;

namespace Whiskers.Services.Scheduler;

/// <summary>
/// Runs one scheduled task. Every mutation runs through the Mutation Gateway on the authority of the task's owner
/// (<see cref="ScheduledTaskEntity.CreatedBy"/>), re-checked on each run (PC-D6, INV-04). A task from before owners
/// were recorded runs during the grace period as "no owner" (<see cref="AutomationOptions.RequireOwner"/>).
/// </summary>
public class TaskExecutor : ITaskExecutor
{
    private readonly IDockerService _docker;
    private readonly IDatabaseService _dbService;
    private readonly IVolumeBackupService _backupService;
    private readonly IBackupService _selfBackupService;
    private readonly IHostCommandExecutor _executor;
    private readonly IAutomationMutationRunner _automation;
    private readonly INotificationService? _notify;
    private readonly ILogger<TaskExecutor> _logger;
    private readonly IScheduledTaskSuspender? _suspender;

    public TaskExecutor(
        IDockerService docker,
        IDatabaseService dbService,
        IVolumeBackupService backupService,
        IBackupService selfBackupService,
        IHostCommandExecutor executor,
        IAutomationMutationRunner automation,
        ILogger<TaskExecutor> logger,
        DataPathOptions? dataPaths = null,
        INotificationService? notify = null,
        Whiskers.Services.Guardrails.IGuardrailStore? guardrails = null,
        IScheduledTaskSuspender? suspender = null)
    {
        _suspender = suspender;
        _automation = automation;
        _notify = notify;
        _guardrails = guardrails;
        _docker = docker;
        _dbService = dbService;
        _backupService = backupService;
        _selfBackupService = selfBackupService;
        _executor = executor;
        _logger = logger;
        _hostBackupDir = (dataPaths ?? DataPathOptions.Default).BackupsDir;
    }

    public async Task<(bool Success, string Output)> ExecuteAsync(ScheduledTaskEntity task)
    {
        _logger.LogInformation("Executing scheduled task: {Name} ({Type})", task.Name, task.TaskType);

        var refusal = new RunRefusal();
        RefusalOfRun.Value = refusal;
        try
        {
            var result = task.TaskType switch
            {
                ScheduledTaskType.ContainerRestart => await ExecuteContainerRestart(task),
                ScheduledTaskType.DbBackup => await ExecuteDbBackup(task),
                ScheduledTaskType.VolumeBackup => await ExecuteVolumeBackup(task),
                ScheduledTaskType.SelfBackup => await ExecuteSelfBackup(task),
                ScheduledTaskType.CustomCommand => await ExecuteCustomCommand(task),
                ScheduledTaskType.Cleanup => await ExecuteCleanup(task),
                // INV-45: a type none of the six is refused before any decision — on record all the same, under its raw
                // name, which the gateway refuses as no capability.
                _ => await RefusedAsync(task, $"scheduler.task_type.{task.TaskType}", new { taskId = task.TaskId },
                    $"Unknown task type: {task.TaskType}")
            };
            if (refusal.Reason is { } reason)
            {
                // The owner lost their authority for this task (PC-D6): suspend it, so it stops failing every night
                // and an admin sees it. Checked for the task's own capability and for every capability the run was
                // refused (retention deletes need more than the backup). A refusal by policy (kill switch, protected
                // resource) leaves it enabled.
                if (refusal.Capabilities.Prepend(CapabilityOf(task.TaskType)).Distinct(StringComparer.Ordinal)
                        .Select(c => _automation.LostAuthority(ActorOf(task), c)).FirstOrDefault(l => l is not null) is { } lost)
                {
                    // Only the assignment this run was started under; a takeover meanwhile wins (no suspension).
                    switch (await SuspendAsync(task))
                    {
                        case Suspension.Suspended:
                            await NotifySuspendedAsync(task, lost);
                            return (false, $"Suspended: {lost}. An admin can take the task over in Scheduled tasks and " +
                                           $"switch it on again. {reason}");
                        case Suspension.Failed:
                            // Still on, and every run is refused: an admin must hear about it all the same.
                            await NotifyRefusedAsync(task, $"{lost}. Switching the task off failed, so it is still on " +
                                                           $"and every run is refused. {reason}");
                            return (false, $"Refused: {lost}; switching the task off failed. {reason}");
                        default:
                            _logger.LogWarning("Scheduled task {Name} was refused ({Reason}); it has a new owner since " +
                                               "the run started, so it stays on", task.Name, lost);
                            return (false, reason);
                    }
                }
                await NotifyRefusedAsync(task, reason);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {Name} failed", task.Name);
            // The backstop (INV-45, review round 11): whatever ended the run before it reached its decision — an
            // exception from a call the task method made, which no written refusal covers — is the gateway's Denied
            // event of the task's capability, once. A run that reached the gateway has its event there, and one already
            // refused has its own; neither is recorded again. A cancellation counts the same: the run takes no
            // caller's token, so it is a timeout of a call it made (or the host stopping under it), and the refusal is
            // written on the gateway's own bounded time, which a stop does not cut off. The reason names the
            // exception's type only — its message may carry what the environment holds (never log secrets).
            if (!refusal.ReachedGateway && !refusal.Recorded)
                return await RefusedAsync(task,
                    CapabilityOf(task.TaskType) is { Length: > 0 } capability ? capability : $"scheduler.task_type.{task.TaskType}",
                    BackstopArgumentsOf(task), $"The run ended before its decision ({ex.GetType().Name}).");
            return (false, ex.Message);
        }
    }

    /// <summary>The arguments the backstop's refusal names: the target the task's own run or refusal would name.</summary>
    private static object BackstopArgumentsOf(ScheduledTaskEntity task)
    {
        var sid = ScheduledTaskServer.Of(task);
        return task.TaskType switch
        {
            ScheduledTaskType.ContainerRestart => new { containerId = task.TargetId ?? "", name = task.TargetName, serverId = sid },
            // As the backup's own refusals before the lookup name it: with the configured database (review round 12).
            ScheduledTaskType.DbBackup => new
            {
                containerId = task.TargetId ?? "", name = task.TargetName, serverId = sid,
                database = ParseConfig(task.Config).GetValueOrDefault("database", ""),
            },
            ScheduledTaskType.VolumeBackup => new { volumeName = task.TargetId ?? "", containerName = task.TargetName, serverId = sid },
            ScheduledTaskType.SelfBackup => new { label = $"scheduled-{task.Name}" },
            ScheduledTaskType.CustomCommand => new { serverId = sid, taskId = task.TaskId, command = ParseConfig(task.Config).GetValueOrDefault("command", "") },
            ScheduledTaskType.Cleanup => new { serverId = sid, taskId = task.TaskId, containerIds = "" },
            _ => new { taskId = task.TaskId },
        };
    }

    /// <summary>Every gateway run of a scheduled task goes through here: it marks the run as having reached its
    /// decision, so the dispatcher's backstop never records a second event for it (INV-45). Nothing in this class
    /// calls <c>_automation.RunAsync</c> directly (TaskExecutorRefusalInventoryTests). It only forwards to the runner, so
    /// it is a <see cref="MutationEntryPointAttribute"/> like the runner itself: a lambda handed to it is gated.</summary>
    [MutationEntryPoint]
    private Task<MutationOutcome<T>> RunGatedAsync<T>(ScheduledTaskEntity task, string capabilityId, object arguments,
        Func<CancellationToken, Task<T>> action)
    {
        // The flag only once the runner has handed the call to the gateway: what it throws synchronously before that
        // (its argument checks) wrote no event and still reaches the backstop (review round 12); an owner that cannot be
        // read does not throw — the runner refuses it as a principal without authority (round 13).
        var pending = _automation.RunAsync(ActorOf(task), capabilityId, arguments, action);
        if (RefusalOfRun.Value is { } run) run.ReachedGateway = true;
        return pending;
    }

    /// <inheritdoc cref="RunGatedAsync{T}"/>
    [MutationEntryPoint]
    private Task<MutationOutcome<bool>> RunGatedAsync(ScheduledTaskEntity task, string capabilityId, object arguments,
        Func<CancellationToken, Task> action)
    {
        // The flag only once the runner has handed the call to the gateway: what it throws synchronously before that
        // (its argument checks) wrote no event and still reaches the backstop (review round 12); an owner that cannot be
        // read does not throw — the runner refuses it as a principal without authority (round 13).
        var pending = _automation.RunAsync(ActorOf(task), capabilityId, arguments, action);
        if (RefusalOfRun.Value is { } run) run.ReachedGateway = true;
        return pending;
    }

    /// <summary>Contract 0012 (D3): the same forwarding wrapper for a lambda that acts on the decided object.</summary>
    [MutationEntryPoint]
    private Task<MutationOutcome<T>> RunGatedAsync<TTarget, T>(ScheduledTaskEntity task, string capabilityId,
        object arguments, Func<TTarget, CancellationToken, Task<T>> action) where TTarget : ResolvedTarget
    {
        // The flag only once the runner has handed the call to the gateway (see RunGatedAsync{T}).
        var pending = _automation.RunAsync(ActorOf(task), capabilityId, arguments, action);
        if (RefusalOfRun.Value is { } run) run.ReachedGateway = true;
        return pending;
    }

    /// <inheritdoc cref="RunGatedAsync{TTarget, T}"/>
    [MutationEntryPoint]
    private Task<MutationOutcome<bool>> RunGatedAsync<TTarget>(ScheduledTaskEntity task, string capabilityId,
        object arguments, Func<TTarget, CancellationToken, Task> action) where TTarget : ResolvedTarget
    {
        // The flag only once the runner has handed the call to the gateway (see RunGatedAsync{T}).
        var pending = _automation.RunAsync(ActorOf(task), capabilityId, arguments, action);
        if (RefusalOfRun.Value is { } run) run.ReachedGateway = true;
        return pending;
    }

    private async Task<(bool, string)> ExecuteContainerRestart(ScheduledTaskEntity task)
    {
        if (string.IsNullOrEmpty(task.TargetId))
            return await RefusedAsync(task, "container.restart",
                new { containerId = "", name = task.TargetName, serverId = ScheduledTaskServer.Of(task) }, "No container ID specified");

        var containerId = task.TargetId;
        var outcome = await RunGatedAsync(task, "container.restart",
            new { containerId, name = task.TargetName, serverId = ScheduledTaskServer.Of(task) },
            _ => _docker.RestartContainerAsync(containerId, ScheduledTaskServer.Of(task)));
        return Problem(outcome, "container.restart") is { } problem
            ? (false, problem)
            : (true, $"Container {task.TargetName ?? task.TargetId} restarted.");
    }

    private async Task<(bool, string)> ExecuteDbBackup(ScheduledTaskEntity task)
    {
        var config = ParseConfig(task.Config);
        var database = config.GetValueOrDefault("database", "");
        var named = new { containerId = task.TargetId ?? "", name = task.TargetName, serverId = ScheduledTaskServer.Of(task), database };
        if (string.IsNullOrEmpty(task.TargetId))
            return await RefusedAsync(task, "db.backup", named, "No container ID specified");

        // Detect DB type and credentials. A lookup that fails, or finds no database, refuses the backup before its
        // decision — on record as the gateway's Denied event (INV-45).
        // A cancellation (a Docker call that timed out) is not caught here: the dispatcher's backstop records it.
        ContainerInfo? container;
        DatabaseCredentials creds;
        try
        {
            var containers = await _docker.ListContainersAsync(serverId: ScheduledTaskServer.Of(task));
            container = containers.FirstOrDefault(c => c.Id == task.TargetId || c.Name == task.TargetId);
            if (container == null) return await RefusedAsync(task, "db.backup", named, $"Container not found: {task.TargetId}");
            if (!container.IsDatabase)
                return await RefusedAsync(task, "db.backup", named, $"Container {container.Name} is not a database.");
            var env = await _docker.GetContainerEnvAsync(container.Id, ScheduledTaskServer.Of(task));
            creds = DatabaseDetector.ExtractCredentials(env, container.DatabaseType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Scheduled task {Name}: the database container could not be looked up", task.Name);
            return await RefusedAsync(task, "db.backup", named,
                $"The database container could not be looked up ({ex.GetType().Name}).");
        }
        if (string.IsNullOrEmpty(database)) database = creds.Database;
        var resolved = new { containerId = container.Id, name = container.Name, serverId = ScheduledTaskServer.Of(task), database };
        // INV-19: the name becomes a dump-tool operand and part of a host file name; a stored task is no exception —
        // nor a default name from the container's environment. Refused before the decision, on record as the gateway's
        // Denied event under the resolved name (INV-45).
        try
        {
            DatabaseService.CheckDatabaseName(database, container.DatabaseType);
        }
        catch (Whiskers.Utils.UnsafeOperandException ex)
        {
            return await RefusedAsync(task, "db.backup", resolved, ex.Message);
        }

        var outcome = await RunGatedAsync(task, "db.backup", resolved,
            _ => _dbService.BackupDatabaseAsync(container.Id, database, container.DatabaseType, creds, ScheduledTaskServer.Of(task), container.Name));
        if (Problem(outcome, "db.backup") is { } problem) return (false, problem);
        var (success, filePath, sizeBytes, error) = outcome.Value;

        if (!success) return (false, error);

        // Retention cleanup (pass the resolved DB name — config["database"] may have been empty)
        await ApplyRetention(task, config, database, DbDumpFiles.ContainerKey(container.Name, container.Id));

        return (true, $"DB backup: {filePath} ({sizeBytes / 1024}KB)");
    }

    private async Task<(bool, string)> ExecuteVolumeBackup(ScheduledTaskEntity task)
    {
        if (string.IsNullOrEmpty(task.TargetId))
            return await RefusedAsync(task, "volume.backup",
                new { volumeName = "", containerName = task.TargetName, serverId = ScheduledTaskServer.Of(task) }, "No volume name specified");

        var volumeName = task.TargetId;
        var outcome = await RunGatedAsync(task, "volume.backup",
            new { volumeName, containerName = task.TargetName, serverId = ScheduledTaskServer.Of(task) },
            _ => _backupService.BackupVolumeAsync(volumeName, task.TargetName ?? "", ScheduledTaskServer.Of(task), $"Scheduled: {task.Name}"));
        if (Problem(outcome, "volume.backup") is { } problem) return (false, problem);
        var backup = outcome.Value!;

        var config = ParseConfig(task.Config);
        await ApplyRetention(task, config);

        return (true, $"Volume backup: {backup.FileName} ({backup.SizeBytes / 1024}KB)");
    }

    private async Task<(bool, string)> ExecuteSelfBackup(ScheduledTaskEntity task)
    {
        // Whiskers self-backup of /app/data (F3). Backs up the app itself, so ServerId/TargetId are ignored.
        var label = $"scheduled-{task.Name}";
        var outcome = await RunGatedAsync(task, "selfbackup.create", new { label },
            _ => _selfBackupService.CreateBackupAsync(label));
        if (Problem(outcome, "selfbackup.create") is { } problem) return (false, problem);
        var info = outcome.Value!;

        var config = ParseConfig(task.Config);
        await ApplyRetention(task, config);

        return (true, $"Whiskers self-backup: {info.FileName} ({info.SizeBytes / 1024}KB, encrypted={info.Encrypted})");
    }

    private async Task<(bool, string)> ExecuteCustomCommand(ScheduledTaskEntity task)
    {
        var config = ParseConfig(task.Config);
        var command = config.GetValueOrDefault("command", "");
        var sid = ScheduledTaskServer.Of(task);
        if (string.IsNullOrEmpty(command))
            return await RefusedAsync(task, "host.exec", new { serverId = sid, taskId = task.TaskId, command }, "No command specified");

        // The raw execution is the executor's primitive (R1-48): it runs only inside this scope; gating the call re-checks the owner, who must
        // hold host.exec (Admin), and applies the kill switch. The command goes to the policy (forbidden arguments as
        // they are today, not when the task was created); the audit row keeps only its length.
        var outcome = await RunGatedAsync(task, "host.exec",
            new { serverId = sid, taskId = task.TaskId, command },
            _ => _executor.ExecuteMutationAsync(sid, command, TimeSpan.FromMinutes(5)));
        if (Problem(outcome, "host.exec") is { } problem) return (false, problem);
        var result = outcome.Value!;

        return (result.Success, result.Success ? result.Output : result.Error);
    }

    private async Task<(bool, string)> ExecuteCleanup(ScheduledTaskEntity task)
    {
        var sid = ScheduledTaskServer.Of(task);

        // The stopped containers are listed once: exactly these are decided (each by id and name) and removed, by
        // id — a container that stops between the decision and the run is not touched (R1-16). A stopped protected
        // one refuses the cleanup (fail closed without policy).
        IReadOnlyList<ContainerInfo> stopped;
        try
        {
            stopped = await StoppedContainersAsync(sid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing can be decided on containers that cannot be listed: refused, on record (INV-45).
            _logger.LogWarning(ex, "Scheduled task {Name}: the stopped containers could not be listed", task.Name);
            return await RefusedAsync(task, "host.prune", new { serverId = sid, taskId = task.TaskId, containerIds = "" },
                $"Cleanup refused: the stopped containers could not be listed ({ex.GetType().Name}).");
        }
        var ids = stopped.Select(c => c.Id).ToList();
        // Kept in front of the gateway, fail closed: without a guardrail store every stopped container counts as
        // protected, and the gateway's own match needs a target resolver. The refusal is recorded as the gateway's
        // Denied event of exactly this cleanup, with the containers it would have removed (INV-45).
        if (StoppedProtectedContainers(stopped) is { Count: > 0 } blocked)
            return await RefusedAsync(task, "host.prune",
                new { serverId = sid, taskId = task.TaskId, containerIds = string.Join('\n', ids) },
                $"Cleanup refused: it would remove stopped protected container(s) {string.Join(", ", blocked)}.");

        // Those containers, dangling images and build cache. Not networks (R1-16): which ones are unused cannot be
        // told before the prune, so a protected network could not be decided on — they stay.
        var outcome = await RunGatedAsync(task, "host.prune",
            new { serverId = sid, taskId = task.TaskId, containerIds = string.Join('\n', ids) },
            _ => _executor.ExecuteMutationAsync(sid, CleanupCommandFor(ids), TimeSpan.FromMinutes(2)));
        if (Problem(outcome, "host.prune") is { } problem) return (false, problem);
        var result = outcome.Value!;

        return (result.Success, result.Success ? $"Cleanup done:\n{result.Output}" : result.Error);
    }

    /// <summary>What a DB-backup retention runs: <c>rm -f</c> of exactly the listed dump files in the backup directory,
    /// each quoted; <c>true</c> when none is due.</summary>
    public static string RetentionCommandFor(string backupDir, IReadOnlyList<string> dumps) =>
        dumps.Count == 0
            ? "true"
            : "rm -f -- " + string.Join(' ', dumps.Select(f => Whiskers.Utils.ShellUtils.Quote(backupDir + "/" + f)));

    /// <summary>What a cleanup task runs: the decided stopped containers by id (<c>docker rm</c> without force: one
    /// that runs again is not removed), dangling images, build cache — no networks, no volumes.</summary>
    public static string CleanupCommandFor(IReadOnlyList<string> containerIds)
    {
        const string rest = "docker image prune -f 2>&1 && docker builder prune -f 2>&1";
        return containerIds.Count == 0
            ? rest
            : $"docker rm {string.Join(' ', containerIds.Select(Whiskers.Utils.ShellUtils.Quote))} 2>&1 && {rest}";
    }

    private readonly Whiskers.Services.Guardrails.IGuardrailStore? _guardrails;

    // The same explicit server the cleanup runs on: null would list the default server instead. Only the states
    // `docker container prune` removes: `docker rm` refuses a paused or restarting one, which would fail every run.
    private async Task<IReadOnlyList<ContainerInfo>> StoppedContainersAsync(string serverId) =>
        (await _docker.ListContainersAsync(all: true, serverId: serverId))
            .Where(c => c.State?.ToLowerInvariant() is "exited" or "created" or "dead")
            .DistinctBy(c => c.Id)
            .ToList();

    /// <summary>The stopped containers that match a protected-resource pattern. Without the guardrail store nobody
    /// can say what is protected, so every stopped container counts.</summary>
    private IReadOnlyList<string> StoppedProtectedContainers(IReadOnlyList<ContainerInfo> stopped)
    {
        if (_guardrails is null) return stopped.Select(c => c.Name).ToList();
        // The policy the run is decided under (INV-23): an AI trigger's pinned preset when the task runs inside its
        // call, else the live active one — the same one the gateway applies to this run's mutations.
        var patterns = Whiskers.Services.Mutations.EffectivePolicy
            .Of(Whiskers.Services.Mutations.MutationScope.Current?.PinnedPolicy, _guardrails).Policy.ProtectedResources
            .Concat(Whiskers.Services.Guardrails.ControlPlaneResources.NamePatterns).ToList();
        return stopped
            .Where(c => patterns.Any(p => Whiskers.Services.Guardrails.GuardrailRuleHelpers.GlobMatch(p, c.Name)
                                          || Whiskers.Services.Guardrails.GuardrailRuleHelpers.GlobMatch(p, c.Id)))
            .Select(c => c.Name)
            .ToList();
    }

    /// <summary>How many backups the task keeps (its retention); 0 when it deletes none.</summary>
    public static int RetentionOf(ScheduledTaskEntity task) =>
        int.TryParse(ParseConfig(task.Config).GetValueOrDefault("maxBackups", "0"), out var m) && m > 0 ? m : 0;

    private async Task ApplyRetention(ScheduledTaskEntity task, Dictionary<string, string> config, string? dbName = null,
        string? containerKey = null)
    {
        try
        {
            var maxBackups = int.TryParse(config.GetValueOrDefault("maxBackups", "0"), out var m) ? m : 0;
            if (maxBackups <= 0) return;

            if (task.TaskType == ScheduledTaskType.VolumeBackup && !string.IsNullOrEmpty(task.TargetId))
            {
                // Scope to this server so retention never touches another host's backups of a same-named volume.
                var backups = await _backupService.ListBackupsAsync(ScheduledTaskServer.Of(task), task.TargetId);
                var toDelete = backups.OrderByDescending(b => b.CreatedAt).Skip(maxBackups);
                foreach (var b in toDelete)
                {
                    var backupId = b.BackupId;
                    var outcome = await RunGatedAsync(task, "volume.backup_delete",
                        new { volumeName = task.TargetId, backupId, fileName = b.FileName, serverId = ScheduledTaskServer.Of(task) },
                        _ => _backupService.DeleteBackupAsync(backupId));
                    if (Problem(outcome, "volume.backup_delete") is { } problem)
                    {
                        // The backup itself succeeded; retention waits until the owner may delete again.
                        _logger.LogWarning("Retention for task {Name} stopped: {Reason}", task.Name, problem);
                        break;
                    }
                }
            }
            else if (task.TaskType == ScheduledTaskType.SelfBackup)
            {
                // Self-backups are tracked by the self-backup service (files + sidecars); keep the newest N.
                var outcome = await RunGatedAsync(task, "selfbackup.prune", new { keep = maxBackups },
                    _ => _selfBackupService.PruneAsync(maxBackups));
                if (Problem(outcome, "selfbackup.prune") is { } problem)
                    _logger.LogWarning("Retention for task {Name} stopped: {Reason}", task.Name, problem);
            }
            else if (task.TaskType == ScheduledTaskType.DbBackup && !string.IsNullOrWhiteSpace(dbName))
            {
                // DB dumps are host files named "{container}@{db}_{timestamp}_{suffix}.sql[.gz]" (DbDumpFiles) and are
                // not tracked in the DB, so they are pruned on the host: exactly this container's dumps of exactly this
                // database, never a same-named database's in another container, never "app_db" for "app". Dumps in the
                // format before ("{db}_{timestamp}.sql") name no container: they are neither counted nor deleted.
                // Both names are checked like every operand (INV-19).
                var sid = ScheduledTaskServer.Of(task);
                if (!Whiskers.Utils.CommandOperands.IsSafeDatabaseName(dbName)
                    || !Whiskers.Utils.CommandOperands.IsSafeContainerRef(containerKey))
                {
                    // Not reached after the backup's own name check today; should it ever be, the skipped deletion is
                    // an attempt refused before its decision — the gateway's Denied event, never none (INV-45).
                    _logger.LogWarning("DB-backup retention skipped for task {Name}: unsafe database '{Db}' or container '{Container}'",
                        task.Name, dbName, containerKey);
                    await _automation.RefuseAsync(ActorOf(task), "db.backup_delete",
                        new { containerId = task.TargetId, dumpContainer = containerKey, database = dbName, keep = maxBackups, serverId = sid },
                        "Unsafe database or container name: retention skipped.");
                    return;
                }
                // Deleting dumps is its own decision, taken now: the owner may have lost the right since the backup.
                var outcome = await RunGatedAsync(task, "db.backup_delete",
                    new { containerId = task.TargetId, dumpContainer = containerKey, database = dbName, keep = maxBackups, serverId = sid },
                    async _ =>
                    {
                        // The listing is a read (ExecuteAsync); only the rm runs on the raw primitive.
                        var listing = await _executor.ExecuteAsync(sid, DbDumpFiles.ListingCommand(_hostBackupDir), TimeSpan.FromSeconds(15));
                        if (!listing.Success) return listing;
                        if (listing.OutputTruncated)
                        {
                            // A cut listing may lack dumps, so which ones are the newest is unknown: prune nothing, and
                            // fail the call so the decision is not recorded as a retention that happened.
                            _logger.LogWarning("DB-backup retention for task {Name} pruned nothing: the listing of {Dir} was truncated",
                                task.Name, _hostBackupDir);
                            return new CommandResult
                            {
                                ExitCode = 1, OutputTruncated = true,
                                Error = "the backup directory's listing was truncated; nothing was pruned.",
                            };
                        }
                        return await _executor.ExecuteMutationAsync(sid,
                            RetentionCommandFor(_hostBackupDir, DbDumpFiles.ToPrune(listing.Output, containerKey!, dbName, maxBackups)),
                            TimeSpan.FromSeconds(15));
                    });
                if (Problem(outcome, "db.backup_delete") is { } problem)
                    _logger.LogWarning("Retention for task {Name} stopped: {Reason}", task.Name, problem);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention cleanup failed for task {Name}", task.Name);
        }
    }

    private readonly string _hostBackupDir;

    // The first gateway refusal of the current run, so the run can tell an admin (PC-D6). One executor instance
    // runs tasks concurrently, so it lives on the async flow, not on the instance.
    // A box, not the value: the run's methods fill it, and the caller reads it after they returned.
    private static readonly AsyncLocal<RunRefusal?> RefusalOfRun = new();

    /// <summary>The first refusal of a run and every capability that was refused in it.</summary>
    private sealed class RunRefusal
    {
        public string? Reason { get; set; }
        public List<string> Capabilities { get; } = [];
        /// <summary>The run asked the gateway (<c>RunGatedAsync</c>): its event is the gateway's.</summary>
        public bool ReachedGateway { get; set; }
        /// <summary>The run was refused before its decision and that refusal was handed to the gateway.</summary>
        public bool Recorded { get; set; }
    }

    internal static AutomationActor ActorOf(ScheduledTaskEntity task) =>
        new("task:" + task.TaskId, $"Scheduled task '{task.Name}'",
            string.IsNullOrWhiteSpace(task.CreatedBy) ? null : task.CreatedBy, task.CreatedVia);

    /// <summary>A run refused before its decision — no target or command, a database container that is missing or is
    /// none, a lookup that failed, a database name INV-19 refuses, a protected container in a cleanup, an unknown type,
    /// and (the dispatcher's backstop) any exception before the gateway — is recorded as the gateway's Denied event of
    /// the capability it would have run, on the task owner's authority (INV-45, as R1-43). Nothing runs. The run is
    /// marked as refused first, so the backstop never records it a second time.</summary>
    private async Task<(bool, string)> RefusedAsync(ScheduledTaskEntity task, string capabilityId, object arguments,
        string reason)
    {
        if (RefusalOfRun.Value is { } run) run.Recorded = true;
        try
        {
            await _automation.RefuseAsync(ActorOf(task), capabilityId, arguments, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The refusal stands either way. What can still throw here: the gateway's audit write, whose failure the
            // gateway reports itself (the audit outbox, the `audit` readiness check), or the runner's argument checks
            // before the gateway — then this log line is the only trace. An owner that cannot be read no longer
            // throws: the runner refuses it as a principal without authority (INV-45 review round 13).
            _logger.LogError(ex, "The refused run of scheduled task {Name} could not be recorded", task.Name);
        }
        return (false, reason);
    }

    /// <summary>Null when the gateway executed the call; otherwise the message for the task result.</summary>
    private string? Problem<T>(MutationOutcome<T> outcome, string capabilityId)
    {
        switch (outcome.Status)
        {
            case MutationStatus.Executed:
                return null;
            case MutationStatus.Failed:
                return outcome.Error?.Message ?? outcome.Reason ?? "The action failed.";
            default:
                var message = $"Refused: {outcome.Reason}";
                if (RefusalOfRun.Value is { } box)
                {
                    box.Reason ??= message;
                    box.Capabilities.Add(capabilityId);
                }
                return message;
        }
    }

    /// <summary>Switches the task off if it still has the owner this run was started under. With a suspender that is
    /// the only write of <c>Enabled</c>: the caller's later save of the run's result never touches it, so a takeover
    /// (and re-enabling) in between is not overwritten. Without one (a caller without a database) the entity is
    /// switched off and the caller persists it.</summary>
    private async Task<Suspension> SuspendAsync(ScheduledTaskEntity task)
    {
        if (_suspender is null)
        {
            task.Enabled = false;
            return Suspension.Suspended;
        }
        try
        {
            return await _suspender.SuspendAsync(task.TaskId, task.CreatedBy)
                ? Suspension.Suspended
                : Suspension.Superseded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not suspended, never unconditionally: the run itself was refused, and the next run tries again.
            _logger.LogError(ex, "Scheduled task {Name} could not be suspended", task.Name);
            return Suspension.Failed;
        }
    }

    /// <summary>What became of a suspension: done, skipped because the task has a new owner since the run started,
    /// or failed (the task is still on).</summary>
    private enum Suspension { Suspended, Superseded, Failed }

    /// <summary>The capability a task type runs; its owner must hold it for the task to run at all.</summary>
    internal static string CapabilityOf(ScheduledTaskType type) => type switch
    {
        ScheduledTaskType.ContainerRestart => "container.restart",
        ScheduledTaskType.DbBackup => "db.backup",
        ScheduledTaskType.VolumeBackup => "volume.backup",
        ScheduledTaskType.SelfBackup => "selfbackup.create",
        ScheduledTaskType.CustomCommand => "host.exec",
        ScheduledTaskType.Cleanup => "host.prune",
        _ => "",
    };

    private async Task NotifySuspendedAsync(ScheduledTaskEntity task, string lost)
    {
        _logger.LogWarning("Scheduled task {Name} was suspended: {Reason}", task.Name, lost);
        if (_notify is null) return;
        try
        {
            await _notify.SendAsync(new NotificationEvent
            {
                EventType = NotificationEventTypes.AutomationSuspended,
                ContainerName = task.TargetName ?? task.Name,
                ImageInfo = $"Scheduled task \"{task.Name}\" was suspended: {lost}. It stays off until an admin takes it over in Scheduled tasks and switches it on again.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification for suspended task {Name} failed", task.Name);
        }
    }

    private async Task NotifyRefusedAsync(ScheduledTaskEntity task, string reason)
    {
        _logger.LogWarning("Scheduled task {Name} was refused: {Reason}", task.Name, reason);
        if (_notify is null) return;
        try
        {
            await _notify.SendAsync(new NotificationEvent
            {
                EventType = NotificationEventTypes.AutomationRefused,
                ContainerName = task.TargetName ?? task.Name,
                ImageInfo = $"Scheduled task \"{task.Name}\" did not run. {reason} An admin can take it over in Scheduled tasks.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification for refused task {Name} failed", task.Name);
        }
    }

    /// <summary>The command of a custom-command task, or <c>null</c>.</summary>
    public static string? CommandOf(ScheduledTaskEntity task) =>
        ParseConfig(task.Config).GetValueOrDefault("command");

    private static Dictionary<string, string> ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { return new(); }
    }
}
