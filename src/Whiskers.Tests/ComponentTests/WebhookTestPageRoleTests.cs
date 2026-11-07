using System.Reflection;
using Bunit;
using Microsoft.EntityFrameworkCore;
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
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Webhooks;
using static Whiskers.Tests.ComponentTests.UiCreatedResourceAuditTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// INV-13 on the webhook test: the page requires the catalog role of the action the test would run. An Operator may
/// test a restart or rebuild webhook (Write); a compose deploy, Git deploy or any other webhook needs Admin, so the
/// page refuses an Operator itself — one Denied event for that capability, and the signed test never fires.
/// </summary>
public sealed class WebhookTestPageRoleTests : BunitContext, IAsyncLifetime
{
    private readonly ApprovalDatabase _db = new();
    private readonly List<string> _fired = [];

    public WebhookTestPageRoleTests()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AuditLogMutationSink(_db.Scopes), [], NullLogger<MutationGateway>.Instance,
            decisions: new DbPolicyDecisionStore(_db.Scopes),
            approvals: new ApprovalGate(new ApprovalStore(policy: store, persistence: _db.Persistence()), store),
            journal: new DbOperationJournal(_db.Scopes));
        var user = new OperatorUser();

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(new Stub<IKeyInterceptorService>().Object);
        Services.AddSingleton<ICurrentUserService>(user);
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), user));
        Services.AddSingleton(new Stub<IWebhookService>()
            .On(nameof(IWebhookService.TriggerSignedTestAsync), a =>
            {
                _fired.Add((string)a[0]!);
                return Task.FromResult((true, "ok"));
            }).Object);
        Services.AddSingleton(new Stub<IDockerService>()
            .On(nameof(IDockerService.ListContainersAsync), _ => Task.FromResult<IList<ContainerInfo>>(
                [new ContainerInfo { Id = "c0ffee", Name = "web", ServerId = "local" }])).Object);
        Services.AddSingleton(new Stub<IGitDeployService>().Object);
        Services.AddSingleton(new Stub<IServerConfigService>().Object);
        Services.AddSingleton(new Stub<IComposePlanner>().Object);
        Services.AddSingleton(new Stub<IAutomationOwnerResolver>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _db.Dispose();
    }

    [Theory]
    [InlineData("deploy", "/srv/app", "compose.deploy")]
    [InlineData("git-deploy", "app-1", "gitdeploy.deploy")]
    [InlineData("something-else", "web", "webhook.save")]
    public async Task An_operator_testing_an_admin_webhook_is_refused_at_the_page_and_nothing_fires(
        string action, string target, string capability)
    {
        await TestAsync(new WebhookEntity { WebhookId = "wh1", Name = "hook", Action = action, TargetId = target, ServerId = "local" });

        Assert.Empty(_fired);
        var row = Assert.Single(await OutcomesAsync());
        Assert.Equal(capability, row.Action);
        Assert.Equal("Denied", row.Outcome);
        Assert.Contains("Requires the Admin role", row.Details);
    }

    [Theory]
    [InlineData("restart", "container.restart")]
    [InlineData("rebuild", "container.update")]
    public async Task An_operator_testing_a_restart_or_rebuild_webhook_runs_it(string action, string capability)
    {
        await TestAsync(new WebhookEntity { WebhookId = "wh2", Name = "hook", Action = action, TargetId = "web", ServerId = "local" });

        Assert.Equal(["wh2"], _fired);
        var row = Assert.Single(await OutcomesAsync());
        Assert.Equal(capability, row.Action);
        Assert.Equal("Executed", row.Outcome);
    }

    private Task TestAsync(WebhookEntity webhook)
    {
        var page = Render<WebhooksView>();
        var handler = typeof(WebhooksView).GetMethod("TestWebhook", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return page.InvokeAsync(() => (Task)handler.Invoke(page.Instance, [webhook])!);
    }

    private async Task<List<AuditLogEntity>> OutcomesAsync()
    {
        using var scope = _db.Scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(r => r.Phase == "outcome").ToListAsync();
    }

    private sealed class OperatorUser : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("ops@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Operator);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(AppRole.Operator >= required);
    }
}
