using ModelContextProtocol.Server;
using System.ComponentModel;
using ServerWatch.Models;
using ServerWatch.Services.Mcp;
using ServerWatch.Services.Scheduler;
using Microsoft.AspNetCore.Http;

namespace ServerWatch.Mcp.Tools;

[McpServerToolType]
public class SchedulerTools
{
    [McpServerTool, Description("List all scheduled tasks with their status, schedule, and last run info.")]
    public static async Task<string> ListScheduledTasks(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISchedulerService scheduler)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_scheduled_tasks");
        if (denied != null) return denied;

        var tasks = await scheduler.GetTasksAsync();
        if (!tasks.Any()) return "No scheduled tasks configured.";

        var lines = tasks.Select(t =>
            $"- [{(t.Enabled ? "ON" : "OFF")}] {t.Name} ({t.TaskType}) | Cron: {t.CronExpression} | Target: {t.TargetName ?? t.TargetId ?? "-"} | Next: {t.NextRun?.ToString("g") ?? "n/a"} | Last: {(t.LastSuccess ? "OK" : "FAIL")} {t.LastRun?.ToString("g") ?? "never"}");
        return $"Scheduled tasks ({tasks.Count}):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Create a new scheduled task (e.g., periodic backup, container restart, cleanup).")]
    public static async Task<string> CreateScheduledTask(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISchedulerService scheduler,
        [Description("Task name")] string name,
        [Description("Cron expression (e.g., '0 2 * * *' for daily at 2am)")] string cronExpression,
        [Description("Task type: ContainerRestart, DbBackup, VolumeBackup, CustomCommand, Cleanup")] string taskType,
        [Description("Target ID (container name for restart/db-backup, volume name for volume-backup)")] string? targetId = null,
        [Description("Custom command (only for CustomCommand type)")] string? command = null,
        [Description("Max backups to retain (only for backup types, 0=unlimited)")] int maxBackups = 7,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "create_scheduled_task");
        if (denied != null) return denied;

        if (!Enum.TryParse<ScheduledTaskType>(taskType, true, out var type))
            return $"Invalid task type: {taskType}. Valid: ContainerRestart, DbBackup, VolumeBackup, CustomCommand, Cleanup";

        // A CustomCommand task runs an arbitrary command through the same host executor as execute_command
        // (root on the host for serverId=local). Creating one must therefore require the same Admin level,
        // otherwise a Write-level key could schedule root commands and bypass the execute_command gate.
        if (type == ScheduledTaskType.CustomCommand)
        {
            var deniedCmd = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "execute_command");
            if (deniedCmd != null) return deniedCmd;
        }

        var config = new Dictionary<string, string>();
        if (maxBackups > 0) config["maxBackups"] = maxBackups.ToString();
        if (!string.IsNullOrEmpty(command)) config["command"] = command;

        var task = await scheduler.CreateTaskAsync(new ScheduledTaskEntity
        {
            Name = name,
            CronExpression = cronExpression,
            TaskType = type,
            TargetId = targetId,
            TargetName = targetId,
            ServerId = serverId,
            Config = config.Any() ? System.Text.Json.JsonSerializer.Serialize(config) : null
        });

        return $"Task created:\n  Name: {task.Name}\n  Type: {task.TaskType}\n  Cron: {task.CronExpression}\n  Next run: {task.NextRun?.ToString("g")}";
    }

    [McpServerTool, Description("Delete a scheduled task by its task ID.")]
    public static async Task<string> DeleteScheduledTask(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISchedulerService scheduler,
        [Description("Task ID to delete")] string taskId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "delete_scheduled_task");
        if (denied != null) return denied;

        await scheduler.DeleteTaskAsync(taskId);
        return $"Task '{taskId}' deleted.";
    }

    [McpServerTool, Description("Run a scheduled task immediately (outside its normal schedule).")]
    public static async Task<string> RunScheduledTask(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISchedulerService scheduler,
        [Description("Task ID to run")] string taskId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "run_scheduled_task");
        if (denied != null) return denied;

        // Running a CustomCommand task executes an arbitrary host command; gate it at the same Admin level
        // as execute_command so run_scheduled_task cannot be used to bypass that boundary.
        var target = (await scheduler.GetTasksAsync()).FirstOrDefault(t => t.TaskId == taskId);
        if (target is null) return $"Task '{taskId}' not found.";
        if (target.TaskType == ScheduledTaskType.CustomCommand)
        {
            var deniedCmd = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "execute_command");
            if (deniedCmd != null) return deniedCmd;
        }

        var (success, output) = await scheduler.RunNowAsync(taskId);
        return success ? $"Task completed successfully:\n{output}" : $"Task failed:\n{output}";
    }
}
