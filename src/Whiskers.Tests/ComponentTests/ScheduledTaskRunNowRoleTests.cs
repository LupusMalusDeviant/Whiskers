using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Whiskers.Services.ServerConfig;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>INV-17/INV-18, job start: "Run now" on the scheduled-tasks page through the real Mutation Gateway. A Viewer
/// is refused by the page's own check, recorded as the gateway's Denied event; an Operator demoted while the page is
/// open passes the page's cached check and is refused by the gateway at the click; neither reaches
/// <see cref="ISchedulerService.RunNowAsync"/>. An Operator's click runs the task inside its decisions.</summary>
public class ScheduledTaskRunNowRoleTests : BunitContext, IAsyncLifetime
{
    private readonly RecordingScheduler _scheduler = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly ServersPauseResumeRoleTests.MutableUser _user = new(AppRole.Viewer);

    public ScheduledTaskRunNowRoleTests()
    {
        _scheduler.Tasks.Add(new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "nightly", TaskType = ScheduledTaskType.ContainerRestart, CronExpression = "0 2 * * *",
            TargetId = "web", TargetName = "web", CreatedBy = "user:ops@example.com",
        });
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<ICapabilityCatalog>(new CapabilityCatalog());
        Services.AddSingleton<IEffectiveRequirementResolver>(sp => new SchedulerRequirementResolver(new CapabilityCatalog(), sp));
        Services.AddSingleton<ISchedulerService>(_scheduler);
        Services.AddSingleton<IDockerService>(new FakeDocker());
        Services.AddSingleton<IServerConfigService>(new FakeServerConfig());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton(NullProxy<Whiskers.Services.AuditLog.IAuditLogService>.Create());
        Services.AddSingleton<IAutomationOwnerResolver>(new WriteOwners());
        var policy = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(policy), _audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), policy));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // MudBlazor services dispose only asynchronously; dispose the container that way.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Fact]
    public async Task A_viewer_cannot_run_a_task_now_and_the_refusal_is_recorded()
    {
        var page = Render<ScheduledTasksView>();   // as a Viewer: no run-now button, the handler runs anyway
        Assert.Empty(page.FindAll("button[title='ScheduledTasksView_Tooltip_RunNow']"));

        await RunNow(page);

        Assert.Empty(_scheduler.Runs);
        Assert.Equal(["denied:Denied"], Events());
    }

    [Fact]
    public void An_operator_demoted_while_the_page_is_open_is_refused_by_the_gateway()
    {
        _user.Role = AppRole.Operator;
        var page = Render<ScheduledTasksView>();
        _user.Role = AppRole.Viewer;   // the page's cached role still says Operator

        page.Find("button[title='ScheduledTasksView_Tooltip_RunNow']").Click();

        page.WaitForAssertion(() => Assert.Equal(["denied:Denied"], Events()));
        Assert.Empty(_scheduler.Runs);
    }

    [Fact]
    public void An_operator_runs_the_task_inside_its_decisions()
    {
        _user.Role = AppRole.Operator;
        var page = Render<ScheduledTasksView>();

        page.Find("button[title='ScheduledTasksView_Tooltip_RunNow']").Click();

        page.WaitForAssertion(() => Assert.Equal(["t1"], _scheduler.Runs));
        Assert.Equal(["begin:container.restart:", "begin:scheduler.run:", "complete:Executed", "complete:Executed"], Events());
    }

    private List<string> Events() { lock (_audit.Events) return _audit.Events.ToList(); }

    private Task RunNow(IRenderedComponent<ScheduledTasksView> page) => page.InvokeAsync(() => (Task)typeof(ScheduledTasksView)
        .GetMethod("RunNow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, [_scheduler.Tasks[0]])!);

    private sealed class RecordingScheduler : ISchedulerService
    {
        public List<ScheduledTaskEntity> Tasks { get; } = [];
        public List<string> Runs { get; } = [];
        public Task<List<ScheduledTaskEntity>> GetTasksAsync() => Task.FromResult(Tasks.ToList());
        public Task<ScheduledTaskEntity> CreateTaskAsync(ScheduledTaskEntity task) => throw new NotSupportedException();
        public Task UpdateTaskAsync(ScheduledTaskEntity task) => throw new NotSupportedException();
        public Task DeleteTaskAsync(string taskId) => throw new NotSupportedException();
        public Task<(bool Success, string Output)> RunNowAsync(string taskId)
        {
            lock (Runs) Runs.Add(taskId);
            return Task.FromResult((true, "ok"));
        }
        public Task<bool> SetOwnerAsync(string taskId, string ownerId, string? expectedOwnerId, string? createdVia = null) =>
            throw new NotSupportedException();
        public Task<List<TaskRunHistoryEntity>> GetHistoryAsync(string? taskId = null, int limit = 50) =>
            Task.FromResult(new List<TaskRunHistoryEntity>());
    }

    private sealed class WriteOwners : IAutomationOwnerResolver
    {
        public MutationPrincipal Resolve(string ownerId) =>
            new(MutationPrincipalKind.Human, ownerId, ownerId, McpPermissionLevels.Write);
    }
}
