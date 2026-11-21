using ModelContextProtocol.Server;
using System.ComponentModel;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Scheduler;
using Whiskers.Services.AuditLog;
using Whiskers.Utils;
using Microsoft.AspNetCore.Http;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp.Tools;

[McpServerToolType]
public class SchedulerTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
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
            $"- [{(t.Enabled ? "ON" : "OFF")}] {t.Name} ({t.TaskType}) | Cron: {t.CronExpression} | Target: {t.TargetName ?? t.TargetId ?? "-"} | Next: {(t.NextRun is null ? "n/a" : SchedulerService.FormatUtc(t.NextRun))} | Last: {(t.LastSuccess ? "OK" : "FAIL")} {(t.LastRun is null ? "never" : SchedulerService.FormatUtc(t.LastRun))}");
        return $"Scheduled tasks ({tasks.Count}):\n{string.Join('\n', lines)}";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Create a new scheduled task (e.g., periodic backup, container restart, cleanup).")]
    public static async Task<string> CreateScheduledTask(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISchedulerService scheduler,
        IToolAuditLog auditLog,
        [Description("Task name")] string name,
        [Description("Cron expression (e.g., '0 2 * * *' for daily at 2am)")] string cronExpression,
        [Description("Task type: ContainerRestart, DbBackup, VolumeBackup, CustomCommand, Cleanup")] string taskType,
        [Description("Target ID (container name for restart/db-backup, volume name for volume-backup)")] string? targetId = null,
        [Description("Custom command (only for CustomCommand type)")] string? command = null,
        [Description("Max backups to retain (only for backup types, 0=unlimited)")] int maxBackups = 7,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "create_scheduled_task");
        if (denied != null) return MutationScope.Fail(denied);

        if (!Enum.TryParse<ScheduledTaskType>(taskType, true, out var type))
            return MutationScope.Fail($"Invalid task type: {taskType}. Valid: ContainerRestart, DbBackup, VolumeBackup, CustomCommand, Cleanup");

        // A CustomCommand task runs an arbitrary command through the same host executor as execute_command
        // (root on the host for serverId=local). Creating one must therefore require the same Admin level,
        // otherwise a Write-level key could schedule root commands and bypass the execute_command gate.
        if (type == ScheduledTaskType.CustomCommand)
        {
            var deniedCmd = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "execute_command");
            if (deniedCmd != null) return MutationScope.Fail(deniedCmd);
        }

        // The caller owns the task — for the agent, the user or key it acts for — and every run re-checks that
        // owner's authority (PC-D6). Without one the task would land in the grace period meant for tasks from
        // before owners were recorded, so refuse instead (an AI trigger has no human or key behind it).
        var owner = AutomationOwners.OfCaller(MutationScope.Current?.Principal);
        if (owner is null)
            return MutationScope.Fail("A scheduled task needs an owner, and this call has no user or API key to own it.");

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
            Config = config.Any() ? System.Text.Json.JsonSerializer.Serialize(config) : null,
            CreatedBy = owner,
            CreatedVia = AutomationOwners.ViaOf(MutationScope.Current?.Principal),
        });

        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        // A CustomCommand is a payload: the audit log keeps only its length (R1-24).
        var auditDetails = type == ScheduledTaskType.CustomCommand && !string.IsNullOrEmpty(command)
            ? $"type={type}, cron={cronExpression}, command={SinkRedaction.Withheld(command)}"
            : $"type={type}, cron={cronExpression}";
        await auditLog.LogAsync(actor, actorType, "scheduler.create", "scheduled-task", task.TaskId, task.Name, auditDetails, serverId);

        return $"Task created:\n  Name: {task.Name}\n  Type: {task.TaskType}\n  Cron: {task.CronExpression}\n  Next run: {SchedulerService.FormatUtc(task.NextRun)}";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Delete a scheduled task by its task ID.")]
    public static async Task<string> DeleteScheduledTask(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISchedulerService scheduler,
        IToolAuditLog auditLog,
        [Description("Task ID to delete")] string taskId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "delete_scheduled_task");
        if (denied != null) return MutationScope.Fail(denied);

        var target = (await scheduler.GetTasksAsync()).FirstOrDefault(t => t.TaskId == taskId);
        await scheduler.DeleteTaskAsync(taskId);
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        await auditLog.LogAsync(actor, actorType, "scheduler.delete", "scheduled-task", taskId, target?.Name ?? taskId);
        return $"Task '{taskId}' deleted.";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Run a scheduled task immediately (outside its normal schedule).")]
    public static async Task<string> RunScheduledTask(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISchedulerService scheduler,
        IToolAuditLog auditLog,
        [Description("Task ID to run")] string taskId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "run_scheduled_task");
        if (denied != null) return MutationScope.Fail(denied);

        // Running a CustomCommand task executes an arbitrary host command; gate it at the same Admin level
        // as execute_command so run_scheduled_task cannot be used to bypass that boundary.
        var target = (await scheduler.GetTasksAsync()).FirstOrDefault(t => t.TaskId == taskId);
        if (target is null) return MutationScope.Fail($"Task '{taskId}' not found.");
        // The gateway decided on the task as it was then (R1-05); its type or retention may have changed since. The run is
        // held to what the task it now runs needs, for every task type: more than the decided level refuses it.
        // Without a gateway decision there is nothing to hold the run to: refused (fail-closed).
        if (MutationScope.Current?.DecidedLevel is not { } decided)
            return MutationScope.Fail($"Task '{taskId}' was not run: no gateway decision to check it against.");
        {
            if (httpContextAccessor.HttpContext?.RequestServices?.GetService<IEffectiveRequirementResolver>()
                is not SchedulerRequirementResolver requirements)
                return MutationScope.Fail($"Task '{taskId}' was not run: its requirement cannot be checked.");
            string now;
            try
            {
                now = requirements.LevelToRun(target);
            }
            catch (CapabilityDisabledException ex)
            {
                return MutationScope.Fail($"Task '{taskId}' was not run: '{ex.Capability.McpTool ?? ex.Capability.Id}' is disabled in Settings → MCP.");
            }
            if (!McpPermissionLevels.HasAccess(decided, now))
                return MutationScope.Fail($"Task '{taskId}' was not run: it now needs '{now}', more than the '{decided}' it was decided at.");
        }
        // The caller must also be allowed what the task does (tool switch, level, key tool list): running a task is
        // not a way around restart_container, backup_database or execute_command.
        var underlying = target.TaskType switch
        {
            ScheduledTaskType.CustomCommand => "execute_command",
            ScheduledTaskType.ContainerRestart => "restart_container",
            ScheduledTaskType.DbBackup => "backup_database",
            _ => null,
        };
        if (underlying is not null)
        {
            var deniedTool = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, underlying);
            if (deniedTool != null) return MutationScope.Fail(deniedTool);
        }

        var (success, output) = await scheduler.RunNowAsync(taskId);
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        await auditLog.LogAsync(actor, actorType, "scheduler.run", "scheduled-task", taskId, target.Name, $"success={success}", success: success);
        if (success)
            return $"Task completed successfully:\n{output}";
        MutationScope.Fail("Task failed.");
        return $"Task failed:\n{output}";
    }
}
