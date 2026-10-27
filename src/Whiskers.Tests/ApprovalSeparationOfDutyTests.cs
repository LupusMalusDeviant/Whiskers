using System.Reflection;
using System.Text.Json;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Xunit;

namespace Whiskers.Tests;

/// <summary>R1-05: a scheduler approval is held to what the task runs, not to the outer tool's level.</summary>
public class ApprovalSeparationOfDutyTests
{
    private static readonly CapabilityCatalog Catalog = new();

    private static SchedulerRequirementResolver Resolver(params ScheduledTaskEntity[] tasks) =>
        new(Catalog, new Services(SchedulerProxy.Create(tasks.ToList())));

    private static Dictionary<string, JsonElement> Args(object args) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!;

    private static Task<string> Create(object args) =>
        Resolver().ResolveAsync(Catalog.Find("scheduler.create")!, Args(args), CancellationToken.None);

    private static Task<string> Run(ScheduledTaskEntity task) =>
        Resolver(task).ResolveAsync(Catalog.Find("scheduler.run")!, Args(new { taskId = task.TaskId }), CancellationToken.None);

    [Fact]
    public async Task Custom_command_requires_an_admin_approver()
    {
        Assert.Equal(McpPermissionLevels.Write, Catalog.Find("scheduler.create")!.RequiredLevel);
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "CustomCommand", command = "id" }));
        Assert.Equal(McpPermissionLevels.Admin,
            await Run(new ScheduledTaskEntity { TaskType = ScheduledTaskType.CustomCommand, Config = "{\"command\":\"id\"}" }));
        // A Write approver cannot resolve an approval at this level (ApprovalStore uses the same comparison).
        Assert.False(McpPermissionLevels.HasAccess(McpPermissionLevels.Write, await Create(new { taskType = "CustomCommand" })));
    }

    [Fact]
    public async Task Retention_and_self_backup_require_admin()
    {
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "DbBackup", targetId = "db" }));
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "VolumeBackup", maxBackups = 3 }));
        Assert.Equal(McpPermissionLevels.Write, await Create(new { taskType = "DbBackup", maxBackups = 0 }));
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "SelfBackup" }));
        Assert.Equal(McpPermissionLevels.Admin,
            await Run(new ScheduledTaskEntity { TaskType = ScheduledTaskType.DbBackup, Config = "{\"maxBackups\":\"7\"}" }));
        Assert.Equal(McpPermissionLevels.Write,
            await Run(new ScheduledTaskEntity { TaskType = ScheduledTaskType.DbBackup, Config = "{\"maxBackups\":\"0\"}" }));
        Assert.Equal(McpPermissionLevels.Admin, await Run(new ScheduledTaskEntity { TaskType = ScheduledTaskType.SelfBackup }));
        // Retention is read the way the executor reads it: the MCP tool and the page store maxBackups as a string.
        Assert.Equal(McpPermissionLevels.Admin,
            await Run(new ScheduledTaskEntity { TaskType = ScheduledTaskType.VolumeBackup, Config = "{\"maxBackups\":\"3\"}" }));
        Assert.Equal(McpPermissionLevels.Write, await Run(new ScheduledTaskEntity { TaskType = ScheduledTaskType.VolumeBackup }));
    }

    [Fact]
    public async Task Plain_tasks_keep_their_capability_level()
    {
        Assert.Equal(McpPermissionLevels.Write, await Create(new { taskType = "ContainerRestart", targetId = "web" }));
        Assert.Equal(McpPermissionLevels.Write, await Create(new { taskType = "Cleanup" }));
    }

    [Fact]
    public async Task Run_without_a_scheduler_fails_closed()
    {
        var resolver = new SchedulerRequirementResolver(Catalog, new Services(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(Catalog.Find("scheduler.run")!, Args(new { taskId = "x" }), CancellationToken.None));
    }

    private sealed class Services(object? scheduler) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ISchedulerService) ? scheduler : null;
    }

    public class SchedulerProxy : DispatchProxy
    {
        private List<ScheduledTaskEntity> _tasks = [];

        public static ISchedulerService Create(List<ScheduledTaskEntity> tasks)
        {
            var proxy = DispatchProxy.Create<ISchedulerService, SchedulerProxy>();
            ((SchedulerProxy)(object)proxy)._tasks = tasks;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ISchedulerService.GetTasksAsync)
                ? Task.FromResult(_tasks)
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
