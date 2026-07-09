using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Database;
using Whiskers.Services.Docker;
using Whiskers.Services.Server;

namespace Whiskers.Services.Scheduler;

public class TaskExecutor : ITaskExecutor
{
    private readonly IDockerService _docker;
    private readonly IDatabaseService _dbService;
    private readonly IVolumeBackupService _backupService;
    private readonly IHostCommandExecutor _executor;
    private readonly ILogger<TaskExecutor> _logger;

    public TaskExecutor(
        IDockerService docker,
        IDatabaseService dbService,
        IVolumeBackupService backupService,
        IHostCommandExecutor executor,
        ILogger<TaskExecutor> logger,
        DataPathOptions? dataPaths = null)
    {
        _docker = docker;
        _dbService = dbService;
        _backupService = backupService;
        _executor = executor;
        _logger = logger;
        _hostBackupDir = (dataPaths ?? DataPathOptions.Default).BackupsDir;
    }

    public async Task<(bool Success, string Output)> ExecuteAsync(ScheduledTaskEntity task)
    {
        _logger.LogInformation("Executing scheduled task: {Name} ({Type})", task.Name, task.TaskType);

        try
        {
            return task.TaskType switch
            {
                ScheduledTaskType.ContainerRestart => await ExecuteContainerRestart(task),
                ScheduledTaskType.DbBackup => await ExecuteDbBackup(task),
                ScheduledTaskType.VolumeBackup => await ExecuteVolumeBackup(task),
                ScheduledTaskType.CustomCommand => await ExecuteCustomCommand(task),
                ScheduledTaskType.Cleanup => await ExecuteCleanup(task),
                _ => (false, $"Unknown task type: {task.TaskType}")
            };
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

        await _docker.RestartContainerAsync(task.TargetId, task.ServerId);
        return (true, $"Container {task.TargetName ?? task.TargetId} restarted.");
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

        var (success, filePath, sizeBytes, error) = await _dbService.BackupDatabaseAsync(
            container.Id, database, container.DatabaseType, creds, task.ServerId);

        if (!success) return (false, error);

        // Retention cleanup (pass the resolved DB name — config["database"] may have been empty)
        await ApplyRetention(task, config, database);

        return (true, $"DB backup: {filePath} ({sizeBytes / 1024}KB)");
    }

    private async Task<(bool, string)> ExecuteVolumeBackup(ScheduledTaskEntity task)
    {
        if (string.IsNullOrEmpty(task.TargetId))
            return (false, "No volume name specified");

        var backup = await _backupService.BackupVolumeAsync(
            task.TargetId, task.TargetName ?? "", task.ServerId, $"Scheduled: {task.Name}");

        var config = ParseConfig(task.Config);
        await ApplyRetention(task, config);

        return (true, $"Volume backup: {backup.FileName} ({backup.SizeBytes / 1024}KB)");
    }

    private async Task<(bool, string)> ExecuteCustomCommand(ScheduledTaskEntity task)
    {
        var config = ParseConfig(task.Config);
        var command = config.GetValueOrDefault("command", "");
        if (string.IsNullOrEmpty(command))
            return (false, "No command specified");

        var sid = task.ServerId ?? "local";
        var result = await _executor.ExecuteAsync(sid, command, TimeSpan.FromMinutes(5));

        return (result.Success, result.Success ? result.Output : result.Error);
    }

    private async Task<(bool, string)> ExecuteCleanup(ScheduledTaskEntity task)
    {
        var sid = task.ServerId ?? "local";

        // Docker system prune (remove unused images, containers, networks)
        var result = await _executor.ExecuteAsync(sid,
            "docker system prune -f --volumes=false 2>&1", TimeSpan.FromMinutes(2));

        return (result.Success, result.Success ? $"Cleanup done:\n{result.Output}" : result.Error);
    }

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
                    await _backupService.DeleteBackupAsync(b.BackupId);
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
                await _executor.ExecuteAsync(sid, script, TimeSpan.FromSeconds(15));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention cleanup failed for task {Name}", task.Name);
        }
    }

    private readonly string _hostBackupDir;

    // DB name embedded into a host shell glob for retention pruning: alphanumeric start, then
    // alphanumerics plus _ . - only. Anything else is rejected (no pruning) rather than risking injection.
    private static readonly System.Text.RegularExpressions.Regex SafeDbName =
        new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
