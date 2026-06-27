using System.Text.Json;
using ServerWatch.Models;
using ServerWatch.Services.Backup;
using ServerWatch.Services.Database;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Server;

namespace ServerWatch.Services.Scheduler;

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
        ILogger<TaskExecutor> logger)
    {
        _docker = docker;
        _dbService = dbService;
        _backupService = backupService;
        _executor = executor;
        _logger = logger;
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

        // Retention cleanup
        await ApplyRetention(task, config);

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

    private async Task ApplyRetention(ScheduledTaskEntity task, Dictionary<string, string> config)
    {
        try
        {
            var maxBackups = int.TryParse(config.GetValueOrDefault("maxBackups", "0"), out var m) ? m : 0;
            if (maxBackups <= 0) return;

            if (task.TaskType == ScheduledTaskType.VolumeBackup && !string.IsNullOrEmpty(task.TargetId))
            {
                var backups = await _backupService.ListBackupsAsync(task.ServerId, task.TargetId);
                var toDelete = backups.OrderByDescending(b => b.CreatedAt).Skip(maxBackups);
                foreach (var b in toDelete)
                    await _backupService.DeleteBackupAsync(b.BackupId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention cleanup failed for task {Name}", task.Name);
        }
    }

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
