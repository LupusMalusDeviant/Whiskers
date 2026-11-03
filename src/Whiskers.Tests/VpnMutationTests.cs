using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;
using Whiskers.Services.Vpn;
using Whiskers.Services.Vpn.Providers;

namespace Whiskers.Tests;

/// <summary>
/// R1-43 (S-07, INV-20/38/45): the in-app VPN bring-up at start is a mutation like any other. It runs through the real
/// Mutation Gateway as <c>vpn.bootstrap</c>, unattended, on the authority of the admin who took it over (a recorded
/// owner, re-read at every start) — never as a synthetic admin, never with an implied confirmation, never in a grace
/// period. Every attempt is exactly one audit event, refusals made before the gateway included, and none of them holds
/// the enrollment key. A start that could not bring the VPN up says so: an error status, and with
/// <c>Vpn:FailStartupOnError</c> a start that stops.
/// </summary>
public class VpnMutationTests
{
    private const string Admin = "admin@example.com";
    private static readonly string AdminId = PrincipalIds.User(Admin);
    private const string Ops = "ops@example.com";
    private static readonly string OpsId = PrincipalIds.User(Ops);

    // ---- Test doubles ----------------------------------------------------------------------------------------------

    /// <summary>One audited attempt: the request as the gateway recorded it and how it ended.</summary>
    private sealed record Event(string Capability, MutationPrincipal Principal, MutationSurface Surface,
        IReadOnlyDictionary<string, JsonElement> Arguments, string Outcome, string? Reason);

    private sealed class RecordingAudit : IMutationAuditSink
    {
        private readonly Dictionary<long, int> _open = [];
        public List<Event> Events { get; } = [];

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            lock (Events)
            {
                Events.Add(new Event(capability.Id, request.Principal, request.Surface, request.Arguments, "begun", null));
                _open[Events.Count] = Events.Count - 1;
                return Task.FromResult((long)Events.Count);
            }
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct)
        {
            lock (Events)
            {
                var i = _open[auditId];
                Events[i] = Events[i] with { Outcome = status.ToString(), Reason = reason };
            }
            return Task.CompletedTask;
        }

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            lock (Events)
                Events.Add(new Event(capability?.Id ?? request.CapabilityId, request.Principal, request.Surface,
                    request.Arguments, status.ToString(), reason));
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryOwnerStore : IAutomationOwnerStore
    {
        private readonly Dictionary<string, AutomationOwnerEntry> _entries = [];
        public List<(string Feature, string? Owner, string Reason)> Suspensions { get; } = [];

        public AutomationOwnerEntry? Get(string feature) =>
            _entries.GetValueOrDefault(feature) is { Blocked: false } e ? e : null;

        public bool IsBlocked(string feature) => _entries.GetValueOrDefault(feature)?.Blocked == true;

        public Task<string?> SetAsync(string feature, string ownerId, CancellationToken ct = default)
        {
            var previous = _entries.GetValueOrDefault(feature)?.OwnerId;
            _entries[feature] = new AutomationOwnerEntry(ownerId, DateTime.UtcNow);
            return Task.FromResult(previous);
        }

        public Task<SuspendResult> SuspendAsync(string feature, string? expectedOwnerId, string reason, CancellationToken ct = default)
        {
            Suspensions.Add((feature, expectedOwnerId, reason));
            _entries[feature] = new AutomationOwnerEntry(expectedOwnerId ?? "", DateTime.UtcNow, Blocked: true, reason);
            return Task.FromResult(SuspendResult.Suspended);
        }

        public void Block(string feature) => _entries[feature] = new AutomationOwnerEntry("", DateTime.UtcNow, Blocked: true);
    }

    /// <summary>A provider that remembers whether, and inside which gateway operation, it was brought up.</summary>
    private sealed class FakeProvider(string id) : IVpnProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public VpnBringUpResult Result { get; set; } = new(VpnBringUpState.Connected, "Connected as whiskers-test.");
        public List<MutationScope?> Calls { get; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<VpnBringUpResult> EnsureUpAsync(CancellationToken ct = default)
        {
            Calls.Add(MutationScope.Current);
            return Task.FromResult(Result);
        }

        public Task<VpnStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(VpnStatus.Disconnected());
        public Task DownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record Fixture(VpnBootstrapHostedService Service, RecordingAudit Audit, MemoryOwnerStore Owners,
        FakeProvider Provider, VpnBootstrapState State, AutomationMutationRunnerTests.Roles Roles,
        AutomationMutationRunnerTests.Whitelist Whitelist, VpnSecretHandlingTests.ListLogger<VpnBootstrapHostedService> Log);

    private static Fixture Build(string provider = "tailscale", bool requireOwner = true, GuardrailPolicy? policy = null,
        Action<VpnSettings>? configure = null)
    {
        var roles = new AutomationMutationRunnerTests.Roles();
        var whitelist = new AutomationMutationRunnerTests.Whitelist();
        whitelist.Allowed.Add(Admin);
        roles.Map[Admin] = AppRole.Admin;
        whitelist.Allowed.Add(Ops);
        roles.Map[Ops] = AppRole.Operator;

        var audit = new RecordingAudit();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), store));
        var runner = new AutomationMutationRunner(gateway, new CapabilityCatalog(),
            new AutomationOwnerResolver(roles, whitelist, new AutomationMutationRunnerTests.Keys(),
                new AutomationMutationRunnerTests.Accounts()),
            new StaticOptionsMonitor<AutomationOptions>(new AutomationOptions { RequireOwner = requireOwner }));

        var settings = new VpnSettings
        {
            Provider = provider,
            Hostname = "whiskers-test",
            Tailscale = { AuthKey = VpnSecretHandlingTests.TailscaleKey, LoginServer = "https://headscale.example.com" },
        };
        configure?.Invoke(settings);
        var fake = new FakeProvider(provider);
        var vpn = new VpnService([fake, new NoopVpnProvider()], Options.Create(settings), NullLogger<VpnService>.Instance);
        var owners = new MemoryOwnerStore();
        var state = new VpnBootstrapState();
        var log = new VpnSecretHandlingTests.ListLogger<VpnBootstrapHostedService>();
        var service = new VpnBootstrapHostedService(vpn, runner, owners, state, Options.Create(settings), log);
        return new Fixture(service, audit, owners, fake, state, roles, whitelist, log);
    }

    private static void AssertNoKeyAnywhere(Fixture f)
    {
        foreach (var e in f.Audit.Events)
        {
            VpnSecretHandlingTests.AssertNoKey(JsonSerializer.Serialize(e.Arguments), VpnSecretHandlingTests.TailscaleKey);
            VpnSecretHandlingTests.AssertNoKey(e.Reason, VpnSecretHandlingTests.TailscaleKey);
        }
        foreach (var line in f.Log.Lines) VpnSecretHandlingTests.AssertNoKey(line, VpnSecretHandlingTests.TailscaleKey);
        VpnSecretHandlingTests.AssertNoKey(f.State.Last.Message, VpnSecretHandlingTests.TailscaleKey);
    }

    // ---- Owned bring-up --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_owned_bring_up_runs_inside_the_gateway_on_the_recorded_admins_authority()
    {
        var f = Build();
        await f.Owners.SetAsync(IAutomationOwnerStore.VpnBootstrap, AdminId);

        await f.Service.StartAsync(CancellationToken.None);

        // Ran once, inside the gateway's operation for exactly this capability.
        var scope = Assert.Single(f.Provider.Calls);
        Assert.NotNull(scope);
        Assert.Equal("vpn.bootstrap", scope!.CapabilityId);
        // One audit event, executed, by the automation acting for the recorded owner — not an admin of its own.
        var e = Assert.Single(f.Audit.Events);
        Assert.Equal(("vpn.bootstrap", "Executed", MutationSurface.Automation), (e.Capability, e.Outcome, e.Surface));
        Assert.Equal(MutationPrincipalKind.Automation, e.Principal.Kind);
        Assert.Equal("vpn-bootstrap", e.Principal.Id);
        Assert.Equal(AdminId, e.Principal.OnBehalfOf?.Id);
        Assert.Equal(MutationPrincipalKind.Human, e.Principal.OnBehalfOf?.Kind);
        Assert.Equal(AdminId, e.Principal.DelegatedBy);
        // What was decided: the provider, the key only as "configured". The mesh hostname (here "whiskers-test", which
        // the control-plane pattern whiskers* would read as a protected container) is a detail, not a target.
        Assert.Equal("tailscale", e.Arguments["provider"].GetString());
        Assert.Equal("configured", e.Arguments["enrollmentKey"].GetString());
        Assert.False(e.Arguments.ContainsKey("hostname"));
        Assert.Equal(VpnBootstrapStatus.Done, f.State.Last.Status);
        Assert.NotNull(f.State.Last.OperationId);
        AssertNoKeyAnywhere(f);
    }

    [Fact]
    public async Task Without_an_owner_nothing_runs_and_the_refusal_is_one_denied_event_with_read_authority()
    {
        var f = Build();

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Empty(f.Provider.Calls);
        var e = Assert.Single(f.Audit.Events);
        Assert.Equal(("vpn.bootstrap", "Denied"), (e.Capability, e.Outcome));
        Assert.Equal("unowned:vpn-bootstrap", e.Principal.Id);
        Assert.Equal(McpPermissionLevels.Read, e.Principal.PermissionLevel);
        Assert.Null(e.Principal.OnBehalfOf);
        Assert.Equal(VpnBootstrapStatus.NoOwner, f.State.Last.Status);
        Assert.False(f.State.Last.Ok);
        Assert.Contains(f.Log.Lines, l => l.Contains("No admin has taken the VPN bring-up over", StringComparison.Ordinal));
        AssertNoKeyAnywhere(f);
    }

    [Fact]
    public async Task The_owner_grace_period_does_not_apply_to_the_vpn_bring_up()
    {
        // Automation:RequireOwner=false lets an ownerless scheduled task run with its capability's level; the VPN
        // bring-up never had an owner to lose, so it never runs without one.
        var f = Build(requireOwner: false);

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Empty(f.Provider.Calls);
        var e = Assert.Single(f.Audit.Events);
        Assert.Equal("Denied", e.Outcome);
        Assert.Equal(McpPermissionLevels.Read, e.Principal.PermissionLevel);
        Assert.Equal(VpnBootstrapStatus.NoOwner, f.State.Last.Status);
    }

    [Fact]
    public async Task A_blocked_owner_entry_refuses_the_bring_up()
    {
        var f = Build();
        f.Owners.Block(IAutomationOwnerStore.VpnBootstrap);

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Empty(f.Provider.Calls);
        Assert.Equal("Denied", Assert.Single(f.Audit.Events).Outcome);
        Assert.Equal(VpnBootstrapStatus.Blocked, f.State.Last.Status);
    }

    [Fact]
    public async Task An_owner_below_admin_is_refused_by_the_gateway_and_the_bring_up_is_suspended()
    {
        var f = Build();
        await f.Owners.SetAsync(IAutomationOwnerStore.VpnBootstrap, OpsId);   // an Operator: write, not admin

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Empty(f.Provider.Calls);
        var e = Assert.Single(f.Audit.Events);
        Assert.Equal("Denied", e.Outcome);
        Assert.Equal(OpsId, e.Principal.OnBehalfOf?.Id);
        Assert.Equal(McpPermissionLevels.Write, e.Principal.PermissionLevel);
        Assert.Equal(VpnBootstrapStatus.Refused, f.State.Last.Status);
        var suspension = Assert.Single(f.Owners.Suspensions);
        Assert.Equal((IAutomationOwnerStore.VpnBootstrap, OpsId), (suspension.Feature, suspension.Owner));
    }

    [Fact]
    public async Task An_owner_who_can_no_longer_sign_in_has_no_authority_left()
    {
        var f = Build();
        await f.Owners.SetAsync(IAutomationOwnerStore.VpnBootstrap, AdminId);
        f.Whitelist.Allowed.Remove(Admin);   // the role entry stays: it must not count

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Empty(f.Provider.Calls);
        Assert.Equal(McpPermissionLevels.Read, Assert.Single(f.Audit.Events).Principal.PermissionLevel);
        Assert.Equal(VpnBootstrapStatus.Refused, f.State.Last.Status);
        Assert.Single(f.Owners.Suspensions);
    }

    [Fact]
    public async Task The_kill_switch_refuses_the_bring_up_without_suspending_it()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;
        var f = Build(policy: policy);
        await f.Owners.SetAsync(IAutomationOwnerStore.VpnBootstrap, AdminId);

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Empty(f.Provider.Calls);
        Assert.Equal("Denied", Assert.Single(f.Audit.Events).Outcome);
        Assert.Equal(VpnBootstrapStatus.Refused, f.State.Last.Status);
        Assert.Empty(f.Owners.Suspensions);   // a policy refusal is not a lost authority
    }

    // ---- Failure is explicit ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_bring_up_is_recorded_as_failed_and_shown_but_does_not_stop_the_start_by_default()
    {
        var f = Build();
        await f.Owners.SetAsync(IAutomationOwnerStore.VpnBootstrap, AdminId);
        f.Provider.Result = new VpnBringUpResult(VpnBringUpState.Failed, "tailscale up failed (exit code 1): denied");

        await f.Service.StartAsync(CancellationToken.None);   // no throw

        var e = Assert.Single(f.Audit.Events);
        Assert.Equal("Failed", e.Outcome);
        Assert.Contains("tailscale up failed", e.Reason);
        Assert.Equal(VpnBootstrapStatus.Failed, f.State.Last.Status);
        Assert.Contains(f.Log.Lines, l => l.Contains("bring-up of 'tailscale' failed", StringComparison.Ordinal));
        AssertNoKeyAnywhere(f);
    }

    [Fact]
    public async Task With_fail_startup_on_error_a_failed_bring_up_stops_the_start()
    {
        var f = Build(configure: s => s.FailStartupOnError = true);
        await f.Owners.SetAsync(IAutomationOwnerStore.VpnBootstrap, AdminId);
        f.Provider.Result = new VpnBringUpResult(VpnBringUpState.NotInstalled, "The tailscale CLI is not installed.");

        var ex = await Assert.ThrowsAsync<VpnBootstrapFailedException>(() => f.Service.StartAsync(CancellationToken.None));

        Assert.Equal(VpnBootstrapStatus.Failed, ex.Report.Status);
        Assert.Equal(VpnBootstrapStatus.Failed, f.State.Last.Status);
        VpnSecretHandlingTests.AssertNoKey(ex.Message, VpnSecretHandlingTests.TailscaleKey);
    }

    [Fact]
    public async Task With_fail_startup_on_error_a_missing_owner_stops_the_start()
    {
        var f = Build(configure: s => s.FailStartupOnError = true);

        var ex = await Assert.ThrowsAsync<VpnBootstrapFailedException>(() => f.Service.StartAsync(CancellationToken.None));

        Assert.Equal(VpnBootstrapStatus.NoOwner, ex.Report.Status);
        Assert.Empty(f.Provider.Calls);
    }

    [Fact]
    public async Task A_host_managed_tailscale_counts_as_up()
    {
        var f = Build(configure: s => s.FailStartupOnError = true);
        await f.Owners.SetAsync(IAutomationOwnerStore.VpnBootstrap, AdminId);
        f.Provider.Result = new VpnBringUpResult(VpnBringUpState.HostManaged, "host-managed");

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal("Executed", Assert.Single(f.Audit.Events).Outcome);
        Assert.Equal(VpnBootstrapStatus.Done, f.State.Last.Status);
    }

    [Fact]
    public async Task Provider_none_runs_nothing_and_records_nothing()
    {
        var f = Build(provider: "none", configure: s => s.FailStartupOnError = true);

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Empty(f.Audit.Events);
        Assert.Equal(VpnBootstrapStatus.NotManaged, f.State.Last.Status);
    }

    // ---- Structure -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_bring_up_is_an_admin_capability_and_the_provider_method_is_marked_as_its_mutation()
    {
        var capability = new CapabilityCatalog().Find("vpn.bootstrap");
        Assert.NotNull(capability);
        Assert.Equal(McpPermissionLevels.Admin, capability!.RequiredLevel);
        Assert.Null(capability.McpTool);   // not reachable from MCP or the agent
        var marked = typeof(IVpnProvider).GetMethod(nameof(IVpnProvider.EnsureUpAsync))!
            .GetCustomAttributes(typeof(MutatingOperationAttribute), false).Cast<MutatingOperationAttribute>().Single();
        Assert.Equal("vpn.bootstrap", marked.CapabilityId);
        Assert.Contains(IAutomationOwnerStore.VpnBootstrap, IAutomationOwnerStore.Features);
    }

    [Fact]
    public void Nothing_but_the_gateway_calls_the_bring_up()
    {
        // INV-38: the architecture scan sees no call to vpn.bootstrap outside a gateway runner lambda.
        var bypasses = MutationCallScanner.FindBypasses(typeof(Program).Assembly);
        Assert.DoesNotContain(bypasses, b => b.EndsWith("-> vpn.bootstrap", StringComparison.Ordinal));
    }

    [Fact]
    public void The_active_provider_is_found_by_id_also_behind_the_scope_guard_proxy()
    {
        var settings = Options.Create(new VpnSettings { Provider = "unknown" });
        var proxied = MutationScopeProxy<IVpnProvider>.Wrap(new NoopVpnProvider(), new NoMonitor());
        var vpn = new VpnService([new FakeProvider("tailscale"), proxied], settings, NullLogger<VpnService>.Instance);
        Assert.Equal("none", vpn.Active.Id);
    }

    private sealed class NoMonitor : IMutationScopeMonitor
    {
        public bool Enforce => false;
        public void ReportOutsideScope(string capabilityId, System.Reflection.MethodInfo method) { }
        public IReadOnlyList<MutationBypassReport> Snapshot() => [];
    }
}
