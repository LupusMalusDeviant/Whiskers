using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Services.Mcp;
using System.Reflection;
using System.Text.Json;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Xunit;

namespace Whiskers.Tests;

/// <summary>R1-05: a scheduler approval is held to what the task runs, not to the outer tool's level. R1-15 adds who
/// may decide it (ApprovalSeparationOfDutyTests.Eligibility.cs).</summary>
public partial class ApprovalSeparationOfDutyTests
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
    public async Task A_task_named_by_its_name_needs_the_same_level_as_by_its_id()
    {
        // The gateway resolves run_scheduled_task's taskId by id, else by a unique name (MutationTargets); the
        // requirement must find the same task either way, never a different level for the same run.
        var volume = new ScheduledTaskEntity { TaskId = "t-vol", Name = "Nightly volumes", TaskType = ScheduledTaskType.VolumeBackup };
        var command = new ScheduledTaskEntity { TaskId = "t-cmd", Name = "Cleanup script", TaskType = ScheduledTaskType.CustomCommand };
        var run = Catalog.Find("scheduler.run")!;
        Task<string> By(string idOrName, params ScheduledTaskEntity[] tasks) =>
            Resolver(tasks).ResolveAsync(run, Args(new { taskId = idOrName }), CancellationToken.None);

        Assert.Equal(McpPermissionLevels.Write, await By("t-vol", volume, command));
        Assert.Equal(McpPermissionLevels.Write, await By("Nightly volumes", volume, command));
        Assert.Equal(McpPermissionLevels.Admin, await By("t-cmd", volume, command));
        Assert.Equal(McpPermissionLevels.Admin, await By("Cleanup script", volume, command));
        // An id wins over another task's equal name; an ambiguous name finds no task and stays at Admin.
        var shadow = new ScheduledTaskEntity { TaskId = "x", Name = "t-vol", TaskType = ScheduledTaskType.CustomCommand };
        Assert.Equal(McpPermissionLevels.Write, await By("t-vol", shadow, volume));
        var twin = new ScheduledTaskEntity { TaskId = "t-vol2", Name = "Nightly volumes", TaskType = ScheduledTaskType.VolumeBackup };
        Assert.Equal(McpPermissionLevels.Admin, await By("Nightly volumes", volume, twin));
    }

    /// <summary>The run is held to the task it actually runs: a task whose type, retention or name changed after the
    /// gateway decided on it at Write is refused when it now needs more, for every task type.</summary>
    [Theory]
    [InlineData(ScheduledTaskType.CustomCommand, "{\"command\":\"id\"}")]
    [InlineData(ScheduledTaskType.VolumeBackup, "{\"maxBackups\":\"3\"}")]
    [InlineData(ScheduledTaskType.DbBackup, "{}")]
    [InlineData(ScheduledTaskType.SelfBackup, "{}")]
    public async Task A_task_changed_after_the_decision_is_not_run_above_the_decided_level(ScheduledTaskType now, string config)
    {
        // Decided as a VolumeBackup without retention (Write) under the name "Nightly"; meanwhile renamed and changed.
        var task = new ScheduledTaskEntity { TaskId = "t1", Name = "Renamed", TaskType = now, Config = config };
        var (http, permissions) = await AdminCallerAsync(task);
        // Set here: the accessor keeps the context in an AsyncLocal, which an awaited helper would not hand back.
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var scope = MutationScope.Begin("scheduler.run", "op-" + Guid.NewGuid().ToString("N"));
        scope.DecidedLevel = McpPermissionLevels.Write;

        var answer = await Whiskers.Mcp.Tools.SchedulerTools.RunScheduledTask(accessor, permissions,
            SchedulerProxy.Create([task]), new NullToolAudit(), "t1");

        Assert.Contains("was not run", answer);
        Assert.Contains("was not run", scope.FailureReason);
    }

    [Fact]
    public async Task An_unchanged_task_runs_at_its_decided_level()
    {
        var task = new ScheduledTaskEntity { TaskId = "t1", Name = "Nightly", TaskType = ScheduledTaskType.Cleanup };
        var (http, permissions) = await AdminCallerAsync(task);
        // Set here: the accessor keeps the context in an AsyncLocal, which an awaited helper would not hand back.
        var accessor = new HttpContextAccessor { HttpContext = http };
        using var scope = MutationScope.Begin("scheduler.run", "op-" + Guid.NewGuid().ToString("N"));
        scope.DecidedLevel = McpPermissionLevels.Write;

        // Past the check the proxy is asked to run it (it knows only GetTasksAsync).
        await Assert.ThrowsAsync<NotSupportedException>(() => Whiskers.Mcp.Tools.SchedulerTools.RunScheduledTask(accessor,
            permissions, SchedulerProxy.Create([task]), new NullToolAudit(), "t1"));
    }

    [Fact]
    public async Task A_run_without_a_gateway_decision_is_refused()
    {
        var task = new ScheduledTaskEntity { TaskId = "t1", Name = "Nightly", TaskType = ScheduledTaskType.Cleanup };
        var (http, permissions) = await AdminCallerAsync(task);
        // Set here: the accessor keeps the context in an AsyncLocal, which an awaited helper would not hand back.
        var accessor = new HttpContextAccessor { HttpContext = http };

        // No scope with a decided level: the proxy would throw if the run were attempted.
        var answer = await Whiskers.Mcp.Tools.SchedulerTools.RunScheduledTask(accessor, permissions,
            SchedulerProxy.Create([task]), new NullToolAudit(), "t1");

        Assert.Contains("was not run", answer);
    }

    private static async Task<(HttpContext, IMcpPermissionService)> AdminCallerAsync(ScheduledTaskEntity task)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sod-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var permissions = new McpPermissionService(NullLogger<McpPermissionService>.Instance, new DataPathOptions(dir));
        await permissions.InitializeAsync();
        var key = await permissions.AddApiKeyAsync("admin", McpPermissionLevels.Admin);
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IEffectiveRequirementResolver>(Resolver(task))
                .BuildServiceProvider(),
        };
        http.Request.Headers.Authorization = "Bearer " + key.Key;
        return (http, permissions);
    }

    private sealed class NullToolAudit : Whiskers.Services.AuditLog.IToolAuditLog
    {
        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.CompletedTask;
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
