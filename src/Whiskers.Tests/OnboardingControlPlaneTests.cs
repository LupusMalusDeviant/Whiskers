using System.Reflection;
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
/// INV-59, #187 (option 2): onboarding a host that holds no mTLS state yet creates it and runs as before. Re-onboarding
/// a host that already holds mTLS state — client material in its record or an identity in the per-host credential
/// store — replaces it, so it is a control-plane change: an interactive Admin needs an Admin approval (four-eyes when
/// a second approver is required), everyone else is refused. A store that cannot be read counts as state.
/// </summary>
public class OnboardingControlPlaneTests
{
    private static readonly MutationPrincipal UiAdmin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private static readonly MutationPrincipal AdminKey =
        new(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin, CredentialId: "k1");

    private sealed class Harness
    {
        public ApprovalStore Approvals { get; }
        public MutationGateway Gateway { get; }
        public int Runs { get; private set; }
        public MutationTarget? Seen { get; private set; }

        public Harness(ServerConfig server, IMtlsClientCredentialStore? store = null, bool secondApprover = false,
            params ServerConfig[] others)
        {
            var policy = GuardrailPolicy.SafeDefault();
            policy.ProtectedResources = [];
            policy.ForbiddenArgPatterns = [];
            policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
            policy.RequireConfirmationForWrites = false;
            policy.RequireSecondApprover = secondApprover;
            var guardrails = new AgentToolTestHelpers.FixedGuardrailStore(policy);
            Approvals = new ApprovalStore(policy: guardrails);
            var services = store is null
                ? new TestServices(new FakeServerConfig([server, .. others]))
                : new TestServices(new FakeServerConfig([server, .. others]), store);
            Gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(guardrails),
                new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
                targets: new MutationTargetResolver(services), approvals: new ApprovalGate(Approvals, guardrails));
        }

        public Task<MutationOutcome<int>> OnboardAsync(string serverId, MutationPrincipal who, MutationSurface surface)
        {
            var capability = new CapabilityCatalog().Find("server.onboard")!;
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new { serverId, name = "Edge" }))!;
            var request = new MutationRequest("server.onboard", who, surface,
                MutationTargets.FromArguments(capability, args), args);
            return Gateway.ExecuteAsync(request, (context, _) => { Seen = context.Target?.Record; return Task.FromResult(++Runs); });
        }
    }

    /// <summary>A credential store that answers <see cref="IMtlsClientCredentialStore.HoldsState"/> only.</summary>
    private class StoreProxy : DispatchProxy
    {
        public Func<string, bool> State { get; set; } = _ => false;

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IMtlsClientCredentialStore.HoldsState)
                ? State((string)args![0]!)
                : throw new NotSupportedException(method?.Name);

        public static IMtlsClientCredentialStore With(Func<string, bool> state)
        {
            var store = Create<IMtlsClientCredentialStore, StoreProxy>();
            ((StoreProxy)(object)store).State = state;
            return store;
        }
    }

    private static ServerConfig Plain() => new() { Id = "edge01", Name = "Edge", ConnectionType = ConnectionType.SSH, SshHost = "10.0.0.5" };

    [Fact]
    public async Task A_first_onboarding_runs_without_an_approval()
    {
        var h = new Harness(Plain(), StoreProxy.With(_ => false));

        var outcome = await h.OnboardAsync("edge01", UiAdmin, MutationSurface.Ui);

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Empty(h.Approvals.GetPending());
    }

    public static TheoryData<string> StateKinds => ["tls-record", "client-cert-path", "store-identity", "unreadable-store"];

    private static Harness WithState(string kind, bool secondApprover = false)
    {
        var server = Plain();
        IMtlsClientCredentialStore store = StoreProxy.With(_ => false);
        switch (kind)
        {
            case "tls-record":
                server.ConnectionType = ConnectionType.TCP;
                server.TcpUseTls = true;
                break;
            case "client-cert-path":
                server.TcpClientCertPath = "/app/data/mtls/clients/edge01/1/client.crt";
                break;
            case "store-identity":
                store = StoreProxy.With(_ => true);
                break;
            case "unreadable-store":
                store = StoreProxy.With(_ => throw new IOException("state file is damaged"));
                break;
        }
        return new Harness(server, store, secondApprover);
    }

    [Theory]
    [MemberData(nameof(StateKinds))]
    public async Task Re_onboarding_a_host_with_mtls_state_needs_an_Admin_approval(string kind)
    {
        var h = WithState(kind);

        var asked = await h.OnboardAsync("edge01", UiAdmin, MutationSurface.Ui);

        Assert.Equal(MutationStatus.PendingApproval, asked.Status);
        Assert.Equal(0, h.Runs);
        Assert.Equal(McpPermissionLevels.Admin, Assert.Single(h.Approvals.GetPending()).Level);
    }

    [Theory]
    [MemberData(nameof(StateKinds))]
    public async Task Re_onboarding_is_refused_for_a_key(string kind)
    {
        var h = WithState(kind);

        var outcome = await h.OnboardAsync("edge01", AdminKey, MutationSurface.Mcp);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, h.Runs);
        Assert.Empty(h.Approvals.GetPending());
    }

    [Fact]
    public async Task Re_onboarding_runs_once_after_a_second_Admin_approves_it()
    {
        var h = WithState("store-identity", secondApprover: true);

        var asked = await h.OnboardAsync("edge01", UiAdmin, MutationSurface.Ui);
        Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, await h.Approvals.DecideAsync(asked.ApprovalId!, true,
            ApprovalResolver.User("admin@example.com", AppRole.Admin)));
        Assert.Equal(ApprovalDecisionResult.Decided, await h.Approvals.DecideAsync(asked.ApprovalId!, true,
            ApprovalResolver.User("second@example.com", AppRole.Admin)));

        var ran = await h.OnboardAsync("edge01", UiAdmin, MutationSurface.Ui);
        var again = await h.OnboardAsync("edge01", UiAdmin, MutationSurface.Ui);

        Assert.Equal(MutationStatus.Executed, ran.Status);
        Assert.Equal(1, h.Runs);
        Assert.Equal(MutationStatus.PendingApproval, again.Status);   // single use
    }

    [Fact]
    public void A_caller_cannot_claim_or_hide_the_state()
    {
        // The key is the gateway's own: a caller's "__…" arguments never reach the rules.
        var target = new MutationTarget("server", "edge01", "Edge", "edge01");
        var request = new MutationRequest("server.onboard", UiAdmin, MutationSurface.Ui, target,
            new Dictionary<string, JsonElement>
            {
                [ControlPlaneResources.ResolvedStateKey] = JsonSerializer.SerializeToElement(true),
            });

        Assert.False(GuardrailMutationPolicy.WithResolvedTarget(request).ContainsKey(ControlPlaneResources.ResolvedStateKey));
        Assert.True(GuardrailMutationPolicy.WithResolvedTarget(request with { Target = target with { HasControlPlaneState = true } })
            .ContainsKey(ControlPlaneResources.ResolvedStateKey));
    }

    [Fact]
    public void Only_onboarding_is_decided_on_existing_state()
    {
        var withState = new Dictionary<string, JsonElement>
        {
            [ControlPlaneResources.ResolvedStateKey] = JsonSerializer.SerializeToElement(true),
        };

        Assert.True(ControlPlaneResources.ChangesControlPlane("server.onboard", withState));
        Assert.False(ControlPlaneResources.ChangesControlPlane("server.onboard", new Dictionary<string, JsonElement>()));
        Assert.False(ControlPlaneResources.ChangesControlPlane("server.save", withState));
        Assert.True(ControlPlaneResources.ChangesControlPlane("certificate.issue_mtls", new Dictionary<string, JsonElement>()));
    }

    [Theory]
    [InlineData("gone")]      // a stale row of a removed server
    [InlineData("")]          // no server named: never the default server
    public async Task An_unknown_server_is_refused_and_nothing_runs(string serverId)
    {
        var h = new Harness(Plain(), StoreProxy.With(_ => false));

        var outcome = await h.OnboardAsync(serverId, UiAdmin, MutationSurface.Ui);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public async Task A_display_name_never_selects_another_server()
    {
        // Codex review of #187: a removed id "edge" while another, protected server is displayed as "edge". The decision
        // must not take "edge" for a new server while execution reaches the protected one by its name.
        var protectedHost = new ServerConfig { Id = "prod7", Name = "edge", ConnectionType = ConnectionType.TCP, TcpUseTls = true };
        var h = new Harness(Plain(), StoreProxy.With(_ => false), others: protectedHost);

        var outcome = await h.OnboardAsync("edge", UiAdmin, MutationSurface.Ui);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, h.Runs);
        Assert.Empty(h.Approvals.GetPending());
    }

    [Fact]
    public async Task The_onboarding_runs_on_the_canonical_id_with_the_state_it_was_decided_on()
    {
        var h = new Harness(Plain(), StoreProxy.With(_ => false));

        var outcome = await h.OnboardAsync("EDGE01", UiAdmin, MutationSurface.Ui);

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal("edge01", h.Seen!.Id);
        Assert.Equal("edge01", h.Seen.ServerId);
        Assert.False(h.Seen.HasControlPlaneState);
    }

    [Fact]
    public async Task Two_ids_differing_only_in_case_are_ambiguous()
    {
        var twin = new ServerConfig { Id = "EDGE01", Name = "Edge twin" };
        var h = new Harness(Plain(), StoreProxy.With(_ => false), others: twin);

        var outcome = await h.OnboardAsync("Edge01", UiAdmin, MutationSurface.Ui);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, h.Runs);
    }
}
