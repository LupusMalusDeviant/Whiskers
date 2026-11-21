using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Whiskers.Mcp;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>While a restore is staged (maintenance), nothing changes state: the Mutation Gateway refuses every mutation
/// whatever its path (UI circuit, AutoUpdate/AutoPatch, queued webhook runs), MCP refuses first, and the operations that
/// were already running are counted so the restore waits for them before its safety backup (review 1.0.0, Data). The
/// scheduler's half is in <see cref="MutationShutdownTests"/>, the webhook endpoint's in <see cref="WebhookReplayTests"/>,
/// the restore's wait in <see cref="RestoreCustomSqlitePathTests"/>.</summary>
public sealed class RestoreMaintenanceTests
{
    private static IServiceProvider Services(bool maintenance)
    {
        var state = new MaintenanceStateService();
        if (maintenance) state.EnterMaintenance("test restore");
        return new ServiceCollection().AddSingleton<IMaintenanceStateService>(state).BuildServiceProvider();
    }

    private static MutationGateway Gateway(IMaintenanceStateService maintenance, AgentToolTestHelpers.NullMutationAudit audit)
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        return new MutationGateway(new CapabilityCatalog(),
            AgentToolTestHelpers.Policy(new AgentToolTestHelpers.FixedGuardrailStore(policy)), audit, [],
            NullLogger<MutationGateway>.Instance, maintenance: maintenance);
    }

    private static MutationRequest UiRestart() => new("container.restart",
        new MutationPrincipal(MutationPrincipalKind.Human, "user:admin@example.com", "Admin", McpPermissionLevels.Admin),
        MutationSurface.Ui, new MutationTarget("container", "web-1"), new Dictionary<string, System.Text.Json.JsonElement>());

    [Fact]
    public async Task An_mcp_tool_call_during_a_restore_is_refused_before_anything_runs()
    {
        var ran = false;
        var result = await McpGuardrailFilter.HandleAsync(Services(maintenance: true),
            new CallToolRequestParams { Name = "restart_container" }, "1",
            _ => { ran = true; return ValueTask.FromResult(new CallToolResult()); }, CancellationToken.None);

        Assert.False(ran);
        Assert.True(result.IsError);
        Assert.Contains("restore is in progress", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task A_mutation_from_the_ui_during_a_restore_is_refused_by_the_gateway_and_writes_nothing()
    {
        var maintenance = new MaintenanceStateService();
        maintenance.EnterMaintenance("test restore");
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var ran = false;

        var outcome = await Gateway(maintenance, audit).ExecuteAsync(UiRestart(), (_, _) =>
        {
            ran = true;
            return Task.FromResult(true);
        });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationGateway.MaintenanceRefusal, outcome.Reason);
        Assert.Empty(audit.Events);                      // nothing written into the database the restore replaces
    }

    [Fact]
    public async Task An_operation_running_when_the_restore_begins_is_waited_for()
    {
        var maintenance = new MaintenanceStateService();
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Gateway(maintenance, audit).ExecuteAsync(UiRestart(), async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        maintenance.EnterMaintenance("test restore");
        Assert.Equal(["operation container.restart"], maintenance.InFlight);
        Assert.False(await maintenance.WaitForIdleAsync(TimeSpan.FromMilliseconds(100)));   // still running

        release.SetResult();
        Assert.True(await maintenance.WaitForIdleAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(MutationStatus.Executed, (await running).Status);
    }
}
