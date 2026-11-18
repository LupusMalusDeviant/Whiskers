using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Certificates;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// INV-59 with contract 0018 part E: <c>server.onboard</c> is now also the MCP tool <c>onboard_server</c>, and the policy
/// sees it by that name. Re-onboarding (or abandoning on) a host that holds mTLS state stays a control-plane change: an
/// API key is refused whatever the preset, no approval is raised, and an abandon's refusal names the UI. A host without
/// mTLS state onboards over MCP as the preset decides.
/// </summary>
public sealed class ControlPlaneOnboardMcpTests
{
    private static readonly MutationPrincipal AdminKey =
        new(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin, CredentialId: "k1");

    private static ServerConfig Edge(bool tls) => new()
    {
        Id = "edge", Name = "Edge", ConnectionType = tls ? ConnectionType.TCP : ConnectionType.SSH,
        SshHost = "203.0.113.30", SshUser = "root", TcpHost = "203.0.113.30", TcpPort = 2376, TcpUseTls = tls,
    };

    private static async Task<(MutationOutcome<int> Outcome, int Runs, ApprovalStore Approvals)> OnboardOverMcpAsync(
        ServerConfig server, bool abandon)
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        var guardrails = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var approvals = new ApprovalStore(policy: guardrails);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(guardrails),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            targets: new MutationTargetResolver(new TestServices(new FakeServerConfig(server))),
            approvals: new ApprovalGate(approvals, guardrails));
        var capability = new CapabilityCatalog().FindByMcpTool("onboard_server")!;
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new { server = server.Id, bootstrap = "none", abandon }))!;
        var runs = 0;
        var outcome = await gateway.ExecuteAsync(
            new MutationRequest(capability.Id, AdminKey, MutationSurface.Mcp, MutationTargets.FromArguments(capability, args), args),
            (_, _) => Task.FromResult(++runs));
        return (outcome, runs, approvals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Onboard_server_over_mcp_on_a_host_with_mtls_state_is_denied(bool abandon)
    {
        var (outcome, runs, approvals) = await OnboardOverMcpAsync(Edge(tls: true), abandon);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, runs);
        Assert.Empty(approvals.GetPending());
        Assert.Contains(ProtectedResourceRule.RuleId, outcome.MatchedRules);
        if (abandon) Assert.Contains("Servers → Remove bootstrap access", outcome.Reason);
        else Assert.DoesNotContain("Remove bootstrap access", outcome.Reason);
    }

    [Fact]
    public async Task Onboard_server_over_mcp_on_a_fresh_host_runs_as_the_preset_decides()
    {
        var (outcome, runs, _) = await OnboardOverMcpAsync(Edge(tls: false), abandon: false);

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void The_tool_name_is_a_control_plane_capability_when_state_exists()
    {
        var withState = new Dictionary<string, JsonElement>
        {
            [ControlPlaneResources.ResolvedStateKey] = JsonSerializer.SerializeToElement(true),
        };
        Assert.Equal("onboard_server", new CapabilityCatalog().Find("server.onboard")!.McpTool);
        Assert.True(ControlPlaneResources.ChangesControlPlane("onboard_server", withState));
        Assert.False(ControlPlaneResources.ChangesControlPlane("onboard_server", new Dictionary<string, JsonElement>()));
    }
}
