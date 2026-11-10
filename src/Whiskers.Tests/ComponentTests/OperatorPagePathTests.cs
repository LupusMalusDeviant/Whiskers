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
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using static Whiskers.Tests.ComponentTests.UiCreatedResourceAuditTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// INV-13, the other direction: taking the page role from the catalog keeps a Write capability open to an Operator.
/// An Operator creates a log alert rule from the log search page — the page lets it through, the gateway runs it and
/// records one Executed event — while a Viewer is refused at the page with one Denied event and nothing is created.
/// </summary>
public sealed class OperatorPagePathTests : BunitContext, IAsyncLifetime
{
    private readonly ApprovalDatabase _db = new();
    private readonly List<string> _created = [];

    private void Setup(AppRole role)
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AuditLogMutationSink(_db.Scopes), [], NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(),
            decisions: new DbPolicyDecisionStore(_db.Scopes),
            approvals: new ApprovalGate(new ApprovalStore(policy: store, persistence: _db.Persistence()), store),
            journal: new DbOperationJournal(_db.Scopes));
        var user = new User(role);

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(new Stub<IKeyInterceptorService>().Object);
        Services.AddSingleton<ICurrentUserService>(user);
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), user));
        Services.AddSingleton(new Stub<ILogSearchService>().Object);
        Services.AddSingleton(new Stub<ILogMonitorService>()
            .On(nameof(ILogMonitorService.CreateRuleAsync), a =>
            {
                var rule = (LogAlertRuleEntity)a[0]!;
                _created.Add(rule.Name);
                return Task.FromResult(rule);
            }).Object);
        Services.AddSingleton(new Stub<IDockerService>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _db.Dispose();
    }

    [Fact]
    public async Task An_operator_creates_a_log_alert_rule_from_the_page()
    {
        Setup(AppRole.Operator);
        Assert.Equal(AppRole.Operator, CapabilityCatalog.RoleOf("logalert.create"));

        await CreateRuleAsync("disk errors");

        Assert.Equal(["disk errors"], _created);
        var row = Assert.Single(await OutcomesAsync());
        Assert.Equal("logalert.create", row.Action);
        Assert.Equal("Executed", row.Outcome);
    }

    [Fact]
    public async Task A_viewer_is_refused_at_the_page_and_nothing_is_created()
    {
        Setup(AppRole.Viewer);

        await CreateRuleAsync("disk errors");

        Assert.Empty(_created);
        var row = Assert.Single(await OutcomesAsync());
        Assert.Equal("logalert.create", row.Action);
        Assert.Equal("Denied", row.Outcome);
        Assert.Contains("Requires the Operator role", row.Details);
    }

    private Task CreateRuleAsync(string name)
    {
        var page = Render<LogSearchView>();
        var field = typeof(LogSearchView).GetField("_ruleName", BindingFlags.NonPublic | BindingFlags.Instance);
        var handler = typeof(LogSearchView).GetMethod("CreateRule", BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.True(field is not null && handler is not null, "LogSearchView no longer has _ruleName / CreateRule(): update this test");
        field!.SetValue(page.Instance, name);
        return page.InvokeAsync(() => (Task)handler!.Invoke(page.Instance, null)!);
    }

    private async Task<List<AuditLogEntity>> OutcomesAsync()
    {
        using var scope = _db.Scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(r => r.Phase == "outcome").ToListAsync();
    }

    private sealed class User(AppRole role) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("ops@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }
}
