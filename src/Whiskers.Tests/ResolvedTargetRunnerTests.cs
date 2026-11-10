using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0012 (D3): the runners' typed overloads hand the lambda the object the gateway resolved — with the
/// canonical server also when the caller passed none — and refuse a lambda typed for another kind of object before the
/// gateway decides anything, recording the refusal (INV-45).
/// </summary>
public sealed class ResolvedTargetRunnerTests
{
    private const string Ops = "ops@example.com";

    private readonly FakeDocker _docker = new(
        new ContainerInfo { Id = "aaa111local", Name = "web", ServerId = "local" },
        new ContainerInfo { Id = "bbb222edge", Name = "web", ServerId = "edge01" })
    {
        DefaultServerId = "edge01",
    };

    private readonly FakeServerConfig _servers = new(
        new ServerConfig { Id = "local", Name = "Local" }, new ServerConfig { Id = "edge01", Name = "Edge", IsDefault = true });

    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();

    private MutationGateway Gateway()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        return new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), store),
            targets: new MutationTargetResolver(new TestServices(_docker, _servers)));
    }

    private sealed class FixedUser(AppRole role) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Ops);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    private UiMutationRunner Ui() => new(Gateway(), new CapabilityCatalog(), new FixedUser(AppRole.Operator));

    private AutomationMutationRunner Automation()
    {
        var f = AutomationMutationRunnerTests.Build(gateway: Gateway());
        f.Whitelist.Allowed.Add(Ops);
        f.Roles.Map[Ops] = AppRole.Operator;
        return f.Runner;
    }

    private static readonly AutomationActor Task1 = new("task:t1", "Scheduled task 'nightly'", "user:" + Ops, "user:" + Ops);

    [Fact]
    public async Task The_ui_lambda_receives_the_decided_container_on_the_default_server()
    {
        ResolvedContainer? seen = null;

        var result = await Ui().RunAsync("container.restart", new { containerId = "web" },
            (ResolvedContainer c, CancellationToken _) => { seen = c; return Task.FromResult(7); });

        Assert.Equal(7, result);
        Assert.Equal("bbb222edge", seen!.Id);
        Assert.Equal("edge01", seen.ServerId);   // the page passed no server; the gateway decided the default one
    }

    [Fact]
    public async Task The_ui_task_overload_receives_the_object_too()
    {
        string? id = null;

        await Ui().RunAsync("container.restart", new { containerId = "web", serverId = "local" },
            (ResolvedContainer c, CancellationToken _) => { id = c.Id; return Task.CompletedTask; });

        Assert.Equal("aaa111local", id);
    }

    [Fact]
    public async Task A_base_type_lambda_is_accepted()
    {
        string? kind = null;

        await Ui().RunAsync("container.restart", new { containerId = "web" },
            (ResolvedTarget t, CancellationToken _) => { kind = t.Kind; return Task.CompletedTask; });

        Assert.Equal("container", kind);
    }

    [Fact]
    public async Task A_ui_lambda_typed_for_another_kind_is_refused_and_recorded_before_the_gateway_decides()
    {
        var ran = false;

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() =>
            Ui().RunAsync("container.restart", new { containerId = "web" },
                (ResolvedNetwork _, CancellationToken _) => { ran = true; return Task.CompletedTask; }));

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Contains("ResolvedContainer", refused.Message);
        Assert.Equal(["denied:Denied"], _audit.Events);   // one event, no intent
    }

    [Fact]
    public async Task The_automation_lambda_receives_the_decided_container()
    {
        ResolvedContainer? seen = null;

        var outcome = await Automation().RunAsync(Task1, "container.restart", new { containerId = "web", serverId = "local" },
            (ResolvedContainer c, CancellationToken _) => { seen = c; return Task.FromResult(true); });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal("aaa111local", seen!.Id);
        Assert.Equal("local", seen.ServerId);
    }

    [Fact]
    public async Task An_automation_lambda_typed_for_another_kind_is_refused_and_recorded()
    {
        var ran = false;

        var outcome = await Automation().RunAsync(Task1, "container.restart", new { containerId = "web" },
            (ResolvedVolume _, CancellationToken _) => { ran = true; return Task.CompletedTask; });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(["denied:Denied"], _audit.Events);
    }

    [Fact]
    public void A_runner_double_cannot_hand_out_a_resolved_object()
    {
        IUiMutationRunner runner = new UnusedRunner();

        var ex = Record.Exception(() =>
        {
            _ = runner.RunAsync("container.restart", new { containerId = "web" },
                (ResolvedContainer _, CancellationToken _) => Task.CompletedTask);
        });
        Assert.IsType<NotSupportedException>(ex);
    }

    private sealed class UnusedRunner : IUiMutationRunner
    {
        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
