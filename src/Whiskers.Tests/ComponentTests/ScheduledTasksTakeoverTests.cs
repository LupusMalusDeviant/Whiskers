using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Docker;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests.ComponentTests;

/// <summary>The test host has no resource assembly, so texts render as their keys. WP2 step 2.3: a task whose owner lost its authority (removed user, deleted or disabled key) is refused on
/// every run; an admin must be able to take it over from the page, not only a task without any owner.</summary>
public class ScheduledTasksTakeoverTests : BunitContext
{
    private const string Admin = "admin@example.com";
    private readonly Scheduler _scheduler = new();

    public ScheduledTasksTakeoverTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(new NoopKeyInterceptor());
        Services.AddSingleton<Whiskers.Services.Mutations.ICapabilityCatalog>(new Whiskers.Services.Mutations.CapabilityCatalog());
        Services.AddSingleton<IEffectiveRequirementResolver>(sp =>
            new SchedulerRequirementResolver(new Whiskers.Services.Mutations.CapabilityCatalog(), sp));
        Services.AddSingleton<ISchedulerService>(_scheduler);
        Services.AddSingleton<IDockerService>(new FakeDocker());
        Services.AddSingleton<IServerConfigService>(new FakeServerConfig());
        Services.AddSingleton<ICurrentUserService>(new User(AppRole.Admin));
        Services.AddSingleton<IAuditLogService>(new NullAudit());
        Services.AddSingleton<IUiMutationRunner>(new PassThrough());
        Services.AddSingleton<IAutomationOwnerResolver>(new Owners());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void A_task_whose_key_was_deleted_can_be_taken_over()
    {
        _scheduler.Tasks.Add(new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "nightly", TaskType = ScheduledTaskType.ContainerRestart, CronExpression = "0 2 * * *",
            TargetId = "web", CreatedBy = "key:gone",
        });

        var cut = Render<ScheduledTasksView>();
        Assert.Contains("ScheduledTasksView_Chip_OwnerRevoked", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.Contains("ScheduledTasksView_Button_AdoptAll")).Click();

        Assert.Equal([("t1", PrincipalIds.User(Admin))], _scheduler.OwnerChanges);
    }

    [Fact]
    public void A_task_whose_owner_still_holds_authority_is_not_flagged()
    {
        _scheduler.Tasks.Add(new ScheduledTaskEntity
        {
            TaskId = "t2", Name = "backup", TaskType = ScheduledTaskType.ContainerRestart, CronExpression = "0 2 * * *",
            TargetId = "web", CreatedBy = "user:ops@example.com",
        });

        var cut = Render<ScheduledTasksView>();

        Assert.DoesNotContain("ScheduledTasksView_Chip_OwnerRevoked", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("ScheduledTasksView_Button_AdoptAll"));
    }

    [Fact]
    public void An_operator_is_offered_run_now_exactly_where_the_gateway_lets_them_run_it()
    {
        // R1-05: the page asks the resolver the gateway asks for scheduler.run.
        Services.AddSingleton<ICurrentUserService>(new User(AppRole.Operator));
        ScheduledTaskEntity Task(string id, ScheduledTaskType type, string? config = null) => new()
        {
            TaskId = id, Name = id, TaskType = type, CronExpression = "0 2 * * *", TargetId = "x",
            CreatedBy = "user:ops@example.com", Config = config,
        };
        _scheduler.Tasks.AddRange(
        [
            Task("restart", ScheduledTaskType.ContainerRestart),
            Task("volume-no-retention", ScheduledTaskType.VolumeBackup),
            Task("volume-retention", ScheduledTaskType.VolumeBackup, "{\"maxBackups\":\"3\"}"),
            Task("db-dump", ScheduledTaskType.DbBackup),
            Task("command", ScheduledTaskType.CustomCommand, "{\"command\":\"id\"}"),
            Task("self", ScheduledTaskType.SelfBackup),
        ]);

        var cut = Render<ScheduledTasksView>();

        Assert.Equal(2, cut.FindAll("button[title='ScheduledTasksView_Tooltip_RunNow']").Count);
    }

    [Theory]
    [InlineData(ScheduledTaskType.VolumeBackup, 0, McpPermissionLevels.Write)]
    [InlineData(ScheduledTaskType.VolumeBackup, 3, McpPermissionLevels.Admin)]
    [InlineData(ScheduledTaskType.DbBackup, 0, McpPermissionLevels.Admin)]
    [InlineData(ScheduledTaskType.ContainerRestart, 0, McpPermissionLevels.Write)]
    [InlineData(ScheduledTaskType.CustomCommand, 0, McpPermissionLevels.Admin)]
    public async Task The_page_creates_a_task_with_the_arguments_its_requirement_depends_on(ScheduledTaskType type,
        int maxBackups, string expected)
    {
        var task = new ScheduledTaskEntity { Name = "n", TaskType = type, TargetId = "x" };
        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            System.Text.Json.JsonSerializer.Serialize(ScheduledTasksView.CreateArgumentsOf(task, maxBackups, null)))!;
        var catalog = new Whiskers.Services.Mutations.CapabilityCatalog();
        var resolver = new SchedulerRequirementResolver(catalog, Services);

        Assert.Equal(expected, await resolver.ResolveAsync(catalog.Find("scheduler.create")!, args, CancellationToken.None));
    }

    private sealed class Scheduler : ISchedulerService
    {
        public List<ScheduledTaskEntity> Tasks { get; } = [];
        public List<(string TaskId, string Owner)> OwnerChanges { get; } = [];
        public Task<List<ScheduledTaskEntity>> GetTasksAsync() => Task.FromResult(Tasks.ToList());
        public Task<ScheduledTaskEntity> CreateTaskAsync(ScheduledTaskEntity task) => throw new NotSupportedException();
        public Task UpdateTaskAsync(ScheduledTaskEntity task) => throw new NotSupportedException();
        public Task DeleteTaskAsync(string taskId) => throw new NotSupportedException();
        public Task<(bool Success, string Output)> RunNowAsync(string taskId) => throw new NotSupportedException();
        public Task<bool> SetOwnerAsync(string taskId, string ownerId, string? createdVia = null)
        {
            OwnerChanges.Add((taskId, ownerId));
            Tasks.First(t => t.TaskId == taskId).CreatedBy = ownerId;
            return Task.FromResult(true);
        }
        public Task<List<TaskRunHistoryEntity>> GetHistoryAsync(string? taskId = null, int limit = 50) =>
            Task.FromResult(new List<TaskRunHistoryEntity>());
    }

    /// <summary>"key:gone" was deleted (read); everyone else still holds write.</summary>
    private sealed class Owners : IAutomationOwnerResolver
    {
        public MutationPrincipal Resolve(string ownerId) => new(MutationPrincipalKind.Human, ownerId, ownerId,
            ownerId == "key:gone" ? McpPermissionLevels.Read : McpPermissionLevels.Write);
    }

    private sealed class User(AppRole role) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Admin);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    private sealed class NullAudit : IAuditLogService
    {
        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.CompletedTask;
        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());
        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    private sealed class PassThrough : IUiMutationRunner
    {
        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default) => action(ct);
        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default) => action(ct);
    }

    private sealed class NoopKeyInterceptor : IKeyInterceptorService, IDisposable
    {
        public Task SubscribeAsync(IKeyInterceptorObserver observer, MudBlazor.Services.KeyInterceptorOptions options) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, IKeyDownObserver? keyDown = null, IKeyUpObserver? keyUp = null) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, Action<KeyboardEventArgs>? keyDown = null, Action<KeyboardEventArgs>? keyUp = null) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, Func<KeyboardEventArgs, Task>? keyDown = null, Func<KeyboardEventArgs, Task>? keyUp = null) => Task.CompletedTask;
        public Task UnsubscribeAsync(IKeyInterceptorObserver observer) => Task.CompletedTask;
        public Task UnsubscribeAsync(string elementId) => Task.CompletedTask;
        public Task UpdateKeyAsync(IKeyInterceptorObserver observer, MudBlazor.Services.KeyOptions option) => Task.CompletedTask;
        public Task UpdateKeyAsync(string elementId, MudBlazor.Services.KeyOptions option) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, Action<MudBlazor.Services.KeyMapBuilder> keyMap) => Task.CompletedTask;
        public Task DispatchAsync(string elementId, MudBlazor.Services.KeyEventKind kind, KeyboardEventArgs args) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
