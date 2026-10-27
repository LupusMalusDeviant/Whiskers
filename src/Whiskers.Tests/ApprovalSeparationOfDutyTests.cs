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
        Assert.Equal(McpPermissionLevels.Write, await Create(new { taskType = "VolumeBackup", maxBackups = 0 }));
        // A database dump holds every row: Admin even without retention.
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "DbBackup", maxBackups = 0 }));
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "SelfBackup" }));
        Assert.Equal(McpPermissionLevels.Admin,
            await Run(new ScheduledTaskEntity { TaskType = ScheduledTaskType.DbBackup, Config = "{\"maxBackups\":\"7\"}" }));
        Assert.Equal(McpPermissionLevels.Write,
            await Run(new ScheduledTaskEntity { TaskType = ScheduledTaskType.VolumeBackup, Config = "{\"maxBackups\":\"0\"}" }));
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

    private sealed class Services(object? scheduler, ICapabilityOverrides? overrides = null) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISchedulerService) ? scheduler
            : serviceType == typeof(ICapabilityOverrides) ? overrides : null;
    }

    private sealed class Overrides(string id, bool enabled, string level) : ICapabilityOverrides
    {
        public CapabilityAvailability Resolve(Capability capability) =>
            capability.Id == id ? new(enabled, level) : new(true, capability.RequiredLevel);

        public CapabilityAvailability ResolveTool(string toolName, string codeLevel) => new(true, codeLevel);
    }

    [Theory]
    [InlineData("CustomCommand")]
    [InlineData("customcommand")]
    [InlineData("3")]
    [InlineData("SelfBackup")]
    [InlineData("6")]
    public async Task Every_accepted_task_type_form_is_resolved(string form)
    {
        var expected = Resolver().LevelFor(Enum.Parse<ScheduledTaskType>(form, true), retention: true);
        Assert.Equal(expected, await Create(new { taskType = form }));
        if (int.TryParse(form, out var n))
            Assert.Equal(expected, await Create(new { taskType = n }));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("NoSuchType")]
    [InlineData("")]
    public async Task An_unrecognized_task_type_fails_closed(string form)
    {
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = form }));
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = true }));
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { name = "no type" }));
    }

    [Fact]
    public async Task A_task_id_is_read_like_the_agent_binder_hands_it_to_the_tool()
    {
        // A JSON number reaches the tool as its text (AgentArgumentBinder); the requirement must find the same task.
        var task = new ScheduledTaskEntity { TaskId = "123456789012", TaskType = ScheduledTaskType.CustomCommand };
        var args = Args(new { taskId = 123456789012L });
        Assert.Equal("123456789012",
            Whiskers.Services.Agent.AgentArgumentBinder.ConvertJson(args["taskId"], typeof(string)));
        Assert.Equal(McpPermissionLevels.Admin,
            await Resolver(task).ResolveAsync(Catalog.Find("scheduler.run")!, args, CancellationToken.None));
        // No id, or an id of no task: fail closed.
        Assert.Equal(McpPermissionLevels.Admin,
            await Resolver(task).ResolveAsync(Catalog.Find("scheduler.run")!, Args(new { }), CancellationToken.None));
        Assert.Equal(McpPermissionLevels.Admin,
            await Resolver(task).ResolveAsync(Catalog.Find("scheduler.run")!, Args(new { taskId = "nope" }), CancellationToken.None));
    }

    [Fact]
    public async Task Retention_is_read_like_the_tool_reads_maxBackups()
    {
        Assert.Equal(McpPermissionLevels.Write, await Create(new { taskType = "VolumeBackup", maxBackups = "0" }));
        Assert.Equal(McpPermissionLevels.Write, await Create(new { taskType = "VolumeBackup", maxBackups = -1 }));
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "DbBackup", maxBackups = "5" }));
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "DbBackup", maxBackups = (int?)null }));
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "DbBackup" }));
        Assert.Equal(McpPermissionLevels.Admin, await Create(new { taskType = "DbBackup", maxBackups = "many" }));
    }

    [Fact]
    public async Task A_raised_level_of_the_underlying_capability_applies()
    {
        var resolver = new SchedulerRequirementResolver(Catalog,
            new Services(null, new Overrides("container.restart", true, McpPermissionLevels.Admin)));
        Assert.Equal(McpPermissionLevels.Admin, await resolver.ResolveAsync(Catalog.Find("scheduler.create")!,
            Args(new { taskType = "ContainerRestart", targetId = "web" }), CancellationToken.None));
    }

    [Fact]
    public async Task A_disabled_underlying_capability_refuses()
    {
        var resolver = new SchedulerRequirementResolver(Catalog,
            new Services(null, new Overrides("container.restart", false, McpPermissionLevels.Write)));
        await Assert.ThrowsAsync<CapabilityDisabledException>(() => resolver.ResolveAsync(Catalog.Find("scheduler.create")!,
            Args(new { taskType = "ContainerRestart", targetId = "web" }), CancellationToken.None));
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
