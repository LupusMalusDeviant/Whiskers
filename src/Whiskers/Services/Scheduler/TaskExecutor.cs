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
                _ => (false, $"Unknown task type: {task.TaskType}")
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
                    if (await SuspendAsync(task))
                    {
                        task.Enabled = false;
                        await NotifySuspendedAsync(task, lost);
                        return (false, $"Suspended: {lost}. An admin can take the task over in Scheduled tasks. {reason}");
                    }
                    _logger.LogWarning("Scheduled task {Name} was refused ({Reason}); it has a new owner since the run " +
                                       "started, so it stays on", task.Name, lost);
                    return (false, reason);
                }
                await NotifyRefusedAsync(task, reason);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {Name} failed", task.Name);
            return (false, ex.Message);
        }
    }

    private async Task<(bool, string)> ExecuteContainerRestart(ScheduledTaskEntity task)
    {
        if (string.IsNullOrEmpty(task.TargetId))
            return (false, "No container ID specified");

        var containerId = task.TargetId;
        var outcome = await _automation.RunAsync(ActorOf(task), "container.restart",
            new { containerId, name = task.TargetName, serverId = task.ServerId },
            _ => _docker.RestartContainerAsync(containerId, task.ServerId));
        return Problem(outcome, "container.restart") is { } problem
            ? (false, problem)
            : (true, $"Container {task.TargetName ?? task.TargetId} restarted.");
    }

    private async Task<(bool, string)> ExecuteDbBackup(ScheduledTaskEntity task)
    {
        if (string.IsNullOrEmpty(task.TargetId))
            return (false, "No container ID specified");

        var config = ParseConfig(task.Config);
        var database = config.GetValueOrDefault("database", "");

        // Detect DB type and credentials
        var containers = await _docker.ListContainersAsync(serverId: task.ServerId);
        var container = containers.FirstOrDefault(c => c.Id == task.TargetId || c.Name == task.TargetId);
        if (container == null) return (false, $"Container not found: {task.TargetId}");
        if (!container.IsDatabase) return (false, $"Container {container.Name} is not a database.");

        var env = await _docker.GetContainerEnvAsync(container.Id, task.ServerId);
        var creds = DatabaseDetector.ExtractCredentials(env, container.DatabaseType);
        if (string.IsNullOrEmpty(database)) database = creds.Database;

        var outcome = await _automation.RunAsync(ActorOf(task), "db.backup",
            new { containerId = container.Id, name = container.Name, serverId = task.ServerId, database },
            _ => _dbService.BackupDatabaseAsync(container.Id, database, container.DatabaseType, creds, task.ServerId));
        if (Problem(outcome, "db.backup") is { } problem) return (false, problem);
        var (success, filePath, sizeBytes, error) = outcome.Value;

        if (!success) return (false, error);

        // Retention cleanup (pass the resolved DB name — config["database"] may have been empty)
        await ApplyRetention(task, config, database);

        return (true, $"DB backup: {filePath} ({sizeBytes / 1024}KB)");
    }

    private async Task<(bool, string)> ExecuteVolumeBackup(ScheduledTaskEntity task)
    {
        if (string.IsNullOrEmpty(task.TargetId))
            return (false, "No volume name specified");

        var volumeName = task.TargetId;
        var outcome = await _automation.RunAsync(ActorOf(task), "volume.backup",
            new { volumeName, containerName = task.TargetName, serverId = task.ServerId },
            _ => _backupService.BackupVolumeAsync(volumeName, task.TargetName ?? "", task.ServerId, $"Scheduled: {task.Name}"));
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
        var outcome = await _automation.RunAsync(ActorOf(task), "selfbackup.create", new { label },
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
        if (string.IsNullOrEmpty(command))
            return (false, "No command specified");

        var sid = task.ServerId ?? "local";
        // The executor is not marked yet (it is a primitive); gating the call still re-checks the owner, who must
        // hold host.exec (Admin), and applies the kill switch. The command goes to the policy (forbidden arguments as
        // they are today, not when the task was created); the audit row keeps only its length.
        var outcome = await _automation.RunAsync(ActorOf(task), "host.exec",
            new { serverId = sid, taskId = task.TaskId, command },
            _ => _executor.ExecuteAsync(sid, command, TimeSpan.FromMinutes(5)));
        if (Problem(outcome, "host.exec") is { } problem) return (false, problem);
        var result = outcome.Value!;

        return (result.Success, result.Success ? result.Output : result.Error);
    }

    private async Task<(bool, string)> ExecuteCleanup(ScheduledTaskEntity task)
    {
        var sid = task.ServerId ?? "local";

        // `docker system prune` removes every stopped container. The gateway decided on the host only, so a stopped
        // protected container would go with it: refuse the cleanup while one exists (fail closed without policy).
        if (await StoppedProtectedContainersAsync(sid) is { Count: > 0 } blocked)
            return (false, $"Cleanup refused: it would remove stopped protected container(s) {string.Join(", ", blocked)}.");

        // Docker system prune (remove unused images, containers, networks)
        var outcome = await _automation.RunAsync(ActorOf(task), "host.prune",
            new { serverId = sid, taskId = task.TaskId },
            _ => _executor.ExecuteAsync(sid, "docker system prune -f --volumes=false 2>&1", TimeSpan.FromMinutes(2)));
        if (Problem(outcome, "host.prune") is { } problem) return (false, problem);
        var result = outcome.Value!;

        return (result.Success, result.Success ? $"Cleanup done:\n{result.Output}" : result.Error);
    }

    private readonly Whiskers.Services.Guardrails.IGuardrailStore? _guardrails;

    /// <summary>Stopped containers on the server that match a protected-resource pattern. Without the guardrail
    /// store nobody can say what is protected, so every stopped container counts.</summary>
    private async Task<IReadOnlyList<string>> StoppedProtectedContainersAsync(string serverId)
    {
        // The same explicit server the prune runs on: null would list the default server instead.
        var stopped = (await _docker.ListContainersAsync(all: true, serverId: serverId))
            .Where(c => !string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (_guardrails is null) return stopped.Select(c => c.Name).ToList();
        var patterns = _guardrails.Current.ProtectedResources;
        return stopped
            .Where(c => patterns.Any(p => Whiskers.Services.Guardrails.GuardrailRuleHelpers.GlobMatch(p, c.Name)
                                          || Whiskers.Services.Guardrails.GuardrailRuleHelpers.GlobMatch(p, c.Id)))
            .Select(c => c.Name)
            .ToList();
    }

    /// <summary>How many backups the task keeps (its retention); 0 when it deletes none.</summary>
    public static int RetentionOf(ScheduledTaskEntity task) =>
        int.TryParse(ParseConfig(task.Config).GetValueOrDefault("maxBackups", "0"), out var m) && m > 0 ? m : 0;

    private async Task ApplyRetention(ScheduledTaskEntity task, Dictionary<string, string> config, string? dbName = null)
    {
        try
        {
            var maxBackups = int.TryParse(config.GetValueOrDefault("maxBackups", "0"), out var m) ? m : 0;
            if (maxBackups <= 0) return;

            if (task.TaskType == ScheduledTaskType.VolumeBackup && !string.IsNullOrEmpty(task.TargetId))
            {
                // Scope to this server so retention never touches another host's backups of a same-named volume.
                var backups = await _backupService.ListBackupsAsync(task.ServerId ?? "local", task.TargetId);
                var toDelete = backups.OrderByDescending(b => b.CreatedAt).Skip(maxBackups);
                foreach (var b in toDelete)
                {
                    var backupId = b.BackupId;
                    var outcome = await _automation.RunAsync(ActorOf(task), "volume.backup_delete",
                        new { volumeName = task.TargetId, backupId, fileName = b.FileName, serverId = task.ServerId },
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
                var outcome = await _automation.RunAsync(ActorOf(task), "selfbackup.prune", new { keep = maxBackups },
                    _ => _selfBackupService.PruneAsync(maxBackups));
                if (Problem(outcome, "selfbackup.prune") is { } problem)
                    _logger.LogWarning("Retention for task {Name} stopped: {Reason}", task.Name, problem);
            }
            else if (task.TaskType == ScheduledTaskType.DbBackup && !string.IsNullOrWhiteSpace(dbName))
            {
                // DB dumps are host files named "{db}_{timestamp}.sql[.gz]" and are not tracked in the DB,
                // so prune them on the host. Validate the DB name to a safe charset before it enters the shell.
                if (!SafeDbName.IsMatch(dbName))
                {
                    _logger.LogWarning("DB-backup retention skipped for task {Name}: unsafe database name '{Db}'", task.Name, dbName);
                    return;
                }
                var sid = task.ServerId ?? "local";
                var glob = $"{_hostBackupDir}/{dbName}_*.sql*";
                var script = $"ls -1t {glob} 2>/dev/null | tail -n +{maxBackups + 1} | xargs -r rm -f";
                // Deleting dumps is its own decision, taken now: the owner may have lost the right since the backup.
                var outcome = await _automation.RunAsync(ActorOf(task), "db.backup_delete",
                    new { containerId = task.TargetId, database = dbName, keep = maxBackups, serverId = task.ServerId },
                    _ => _executor.ExecuteAsync(sid, script, TimeSpan.FromSeconds(15)));
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
    }

    internal static AutomationActor ActorOf(ScheduledTaskEntity task) =>
        new("task:" + task.TaskId, $"Scheduled task '{task.Name}'",
            string.IsNullOrWhiteSpace(task.CreatedBy) ? null : task.CreatedBy, task.CreatedVia);

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

    /// <summary>Switches the task off in the database if it still has the owner this run was started under. Without
    /// a suspender (a caller without a database) the caller persists <c>Enabled</c> itself.</summary>
    private async Task<bool> SuspendAsync(ScheduledTaskEntity task)
    {
        if (_suspender is null) return true;
        try
        {
            return await _suspender.SuspendAsync(task.TaskId, task.CreatedBy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: switch it off through the caller's save instead.
            _logger.LogError(ex, "Scheduled task {Name} could not be suspended conditionally", task.Name);
            return true;
        }
    }

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
                EventType = "automation_suspended",
                ContainerName = task.TargetName ?? task.Name,
                ImageInfo = $"Scheduled task \"{task.Name}\" was suspended: {lost}. It stays off until an admin takes it over in Scheduled tasks.",
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
                EventType = "automation_refused",
                ContainerName = task.TargetName ?? task.Name,
                ImageInfo = $"Scheduled task \"{task.Name}\" did not run. {reason} An admin can take it over in Scheduled tasks.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification for refused task {Name} failed", task.Name);
        }
    }

    // DB name embedded into a host shell glob for retention pruning: alphanumeric start, then
    // alphanumerics plus _ . - only. Anything else is rejected (no pruning) rather than risking injection.
    private static readonly System.Text.RegularExpressions.Regex SafeDbName =
        new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
