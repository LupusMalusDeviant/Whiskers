using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 part C (INV-63): replacing a pinned SSH host key is a control-plane change exactly as INV-59 — only an
/// interactive Admin, only with an Admin approval whose grant is used once; every other principal is denied, in both
/// rule sets. The replace itself refuses when either fingerprint is not the one the person compared.
/// </summary>
public sealed class HostKeyReplaceRuleTests : IDisposable
{
    private const string Capability = "server.hostkey_replace";

    private static readonly MutationPrincipal UiAdmin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);
    private static readonly MutationPrincipal UiOperator =
        new(MutationPrincipalKind.Human, "user:op@example.com", "op@example.com", McpPermissionLevels.Write);
    private static readonly MutationPrincipal AdminKey =
        new(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin, CredentialId: "k1");
    private static readonly MutationPrincipal Agent =
        new MutationPrincipal(MutationPrincipalKind.Agent, "agent:s1", "agent", McpPermissionLevels.Admin) with { OnBehalfOf = AdminKey };
    private static readonly MutationPrincipal Automation =
        new(MutationPrincipalKind.Automation, "automation:t1", "trigger", McpPermissionLevels.Admin);

    private readonly TempServerStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Replace_is_a_control_plane_capability() =>
        Assert.Contains(Capability, ControlPlaneResources.Capabilities);

    private sealed class Harness
    {
        public ApprovalStore Approvals { get; }
        public MutationGateway Gateway { get; }
        public int Runs { get; private set; }

        public Harness()
        {
            var policy = GuardrailPolicy.SafeDefault();
            policy.ProtectedResources = [];
            policy.ForbiddenArgPatterns = [];
            policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
            policy.RequireConfirmationForWrites = false;
            var guardrails = new AgentToolTestHelpers.FixedGuardrailStore(policy);
            Approvals = new ApprovalStore(policy: guardrails);
            var server = new ServerConfig { Id = "web-new", Name = "web-new", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.20" };
            Gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(guardrails),
                new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
                targets: new MutationTargetResolver(new TestServices(new FakeServerConfig(server))),
                approvals: new ApprovalGate(Approvals, guardrails));
        }

        public Task<MutationOutcome<int>> ReplaceAsync(MutationPrincipal who, MutationSurface surface)
        {
            var capability = new CapabilityCatalog().Find(Capability)!;
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
            {
                serverId = "web-new", name = "web-new", oldFingerprint = "SHA256:old", newFingerprint = "SHA256:new",
            }))!;
            var request = new MutationRequest(Capability, who, surface, MutationTargets.FromArguments(capability, args), args);
            return Gateway.ExecuteAsync(request, (_, _) => Task.FromResult(++Runs));
        }
    }

    public static TheoryData<string> Others => ["api-key", "agent", "automation", "operator"];

    [Theory]
    [MemberData(nameof(Others))]
    public async Task Every_principal_but_an_interactive_Admin_is_denied(string who)
    {
        var h = new Harness();
        var (principal, surface) = who switch
        {
            "api-key" => (AdminKey, MutationSurface.Mcp),
            "agent" => (Agent, MutationSurface.Agent),
            "automation" => (Automation, MutationSurface.Automation),
            _ => (UiOperator, MutationSurface.Ui),
        };

        var outcome = await h.ReplaceAsync(principal, surface);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, h.Runs);
        Assert.Empty(h.Approvals.GetPending());
    }

    [Fact]
    public async Task The_replace_runs_only_with_a_used_grant()
    {
        var h = new Harness();

        var asked = await h.ReplaceAsync(UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.PendingApproval, asked.Status);
        Assert.Equal(McpPermissionLevels.Admin, Assert.Single(h.Approvals.GetPending()).Level);
        Assert.Equal(0, h.Runs);

        Assert.Equal(ApprovalDecisionResult.Decided, await h.Approvals.DecideAsync(asked.ApprovalId!, true,
            ApprovalResolver.User("second@example.com", AppRole.Admin)));
        var ran = await h.ReplaceAsync(UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Executed, ran.Status);
        Assert.Equal(1, h.Runs);

        // The grant is used: the next replace needs a new approval.
        var again = await h.ReplaceAsync(UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.PendingApproval, again.Status);
        Assert.Equal(1, h.Runs);
    }

    private static GuardrailRequest Request(AgentPrincipalKind kind, AgentOrigin origin, string level) =>
        new(Capability, McpPermissionLevels.Admin,
            new Dictionary<string, JsonElement> { ["serverId"] = JsonSerializer.SerializeToElement("web-new") },
            new AgentContext("s", new AgentPrincipal(kind, "who", level, null), origin, GuardrailPolicy.SafeDefault()));

    [Fact]
    public void Both_rule_sets_confirm_only_for_an_interactive_Admin()
    {
        foreach (var engine in new[] { GuardrailEngine.CreateDefault(), GuardrailEngine.CreateForHumans() })
        {
            Assert.Equal(GuardrailVerdict.Confirm,
                engine.Evaluate(Request(AgentPrincipalKind.WebUser, AgentOrigin.WebUi, McpPermissionLevels.Admin)).Verdict);
            Assert.Equal(GuardrailVerdict.Deny,
                engine.Evaluate(Request(AgentPrincipalKind.McpKey, AgentOrigin.McpDirect, McpPermissionLevels.Admin)).Verdict);
            Assert.Equal(GuardrailVerdict.Deny,
                engine.Evaluate(Request(AgentPrincipalKind.Trigger, AgentOrigin.Trigger, McpPermissionLevels.Admin)).Verdict);
        }
    }

    // ---- the replace itself ----

    private static readonly string Old = "ssh-ed25519 " + FakeSshToolchain.KeyText('o');
    private static readonly string New = "ssh-ed25519 " + FakeSshToolchain.KeyText('n');

    private async Task<(SshHostKeyPins Pins, string KnownHosts)> PinnedAsync(FakeSshToolchain ssh)
    {
        await _store.Servers.AddServerAsync(new ServerConfig
        {
            Id = "web-new", Name = "web-new", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.20", SshUser = "root", SshPort = 22,
        });
        var pins = new SshHostKeyPins(ssh, _store.Servers, NullLogger<SshHostKeyPins>.Instance, _store.Paths);
        ssh.LastScan = [Old];
        await pins.PinFirstContactAsync(_store.Servers.GetServer("web-new")!, default);
        ssh.LastScan = [New];   // reinstalled meanwhile
        return (pins, SshHostKeyPolicy.KnownHostsFile(_store.Paths));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("new")]
    public async Task Wrong_fingerprints_are_refused(string wrong)
    {
        var (pins, knownHosts) = await PinnedAsync(new FakeSshToolchain());
        var before = File.ReadAllText(knownHosts);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pins.ReplaceAsync("web-new",
            wrong == "old" ? "SHA256:not-the-pinned-one" : FakeSshToolchain.FingerprintOf(Old),
            wrong == "new" ? "SHA256:not-the-served-one" : FakeSshToolchain.FingerprintOf(New), default));

        Assert.Equal(before, File.ReadAllText(knownHosts));
        Assert.Equal(FakeSshToolchain.FingerprintOf(Old), _store.Servers.GetServer("web-new")!.SshHostKeyFingerprint);
    }

    [Fact]
    public async Task Confirmed_fingerprints_replace_the_pin_and_audit_both()
    {
        var (pins, knownHosts) = await PinnedAsync(new FakeSshToolchain());
        using var scope = MutationScope.Begin(Capability, "op-r");

        var pin = await pins.ReplaceAsync("web-new", FakeSshToolchain.FingerprintOf(Old), FakeSshToolchain.FingerprintOf(New), default);

        Assert.Equal(SshHostKeySource.Operator, pin.Source);
        Assert.Equal(["203.0.113.20 " + New], File.ReadAllLines(knownHosts));
        var audit = Assert.Single(scope.Results, r => r.Action == "hostkey.replace");
        Assert.Contains(FakeSshToolchain.FingerprintOf(Old), audit.Details);
        Assert.Contains(FakeSshToolchain.FingerprintOf(New), audit.Details);
    }
}
