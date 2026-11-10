using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0012 (D9): the decided object is read again right before it is acted on — after the intent and the
/// operation claim, before the approval is used. Vanished → <see cref="MutationReasonCodes.TargetUnknown"/>, changed →
/// <see cref="MutationReasonCodes.TargetChanged"/>, a failing read refuses: the intent ends as Denied (one event), the
/// claim is released, the approval stays usable and the delegate is never called.
/// </summary>
public sealed class TargetConfirmationTests : IDisposable
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private static readonly DateTime Created = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeDocker _docker = new(
        new ContainerInfo { Id = "aaa111local", Name = "web", ServerId = "local", Created = Created })
    {
        DefaultServerId = "local",
    };

    private readonly FakeServerConfig _servers = new(new ServerConfig { Id = "local", Name = "Local", IsDefault = true });
    private readonly ApprovalDatabase _db = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private int _runs;

    public void Dispose() => _db.Dispose();

    /// <summary>The real resolver, with a hook that changes the inventory right before the confirmation reads it.</summary>
    private sealed class Interposed(IMutationTargetResolver inner) : IMutationTargetResolver
    {
        public Action? BeforeConfirm { get; set; }
        public int Confirmations { get; private set; }

        public Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct) =>
            inner.ResolveAsync(request, capability, ct);

        public Task<TargetResolution> ConfirmAsync(MutationRequest request, Capability capability, ResolvedTarget target,
            CancellationToken ct)
        {
            Confirmations++;
            BeforeConfirm?.Invoke();
            BeforeConfirm = null;
            return inner.ConfirmAsync(request, capability, target, ct);
        }
    }

    private (MutationGateway Gateway, Interposed Targets, ApprovalStore Approvals) Build(bool confirm = false)
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = confirm;
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var approvals = new ApprovalStore(policy: store, persistence: _db.Persistence());
        var targets = new Interposed(new MutationTargetResolver(new TestServices(_docker, _servers)));
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: targets, approvals: new ApprovalGate(approvals, store),
            journal: new DbOperationJournal(_db.Scopes));
        return (gateway, targets, approvals);
    }

    private static readonly MutationPrincipal Key =
        new(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin, CredentialId: "k1");

    private static MutationRequest Restart(string? operationId = null, bool viaKey = false) =>
        new("container.restart", viaKey ? Key : Admin, viaKey ? MutationSurface.Mcp : MutationSurface.Ui, new MutationTarget("container", "web", null, null),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"containerId":"web"}""")!,
            OperationId: operationId);

    private Task<MutationOutcome<ResolvedTarget?>> Run(MutationGateway gateway, MutationRequest request) =>
        gateway.ExecuteAsync(request, (context, _) =>
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(context.Target);
        });

    [Fact]
    public async Task An_unchanged_object_runs_and_the_delegate_receives_it_with_the_canonical_server()
    {
        var (gateway, targets, _) = Build();

        var outcome = await Run(gateway, Restart());

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        var container = Assert.IsType<ResolvedContainer>(outcome.Value);
        Assert.Equal("aaa111local", container.Id);
        Assert.Equal("local", container.ServerId);   // the caller passed none
        Assert.Equal(1, targets.Confirmations);
    }

    [Fact]
    public async Task A_vanished_object_is_refused_as_unknown_in_one_event_and_nothing_runs()
    {
        var (gateway, targets, _) = Build();
        targets.BeforeConfirm = () => _docker.Remove("aaa111local");

        var outcome = await Run(gateway, Restart("op-vanished"));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetUnknown, outcome.ReasonCode);
        Assert.Equal(0, _runs);
        Assert.Equal(["begin:container.restart:", "complete:Denied"], _audit.Events);
    }

    [Fact]
    public async Task A_replaced_object_is_refused_as_changed_and_nothing_runs()
    {
        var (gateway, targets, _) = Build();
        targets.BeforeConfirm = () =>
        {
            // Same id, recreated: another creation time.
            _docker.Remove("aaa111local");
            _docker.Add(new ContainerInfo { Id = "aaa111local", Name = "web", ServerId = "local", Created = Created.AddMinutes(5) });
        };

        var outcome = await Run(gateway, Restart());

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetChanged, outcome.ReasonCode);
        Assert.Equal(0, _runs);
        Assert.Equal(["begin:container.restart:", "complete:Denied"], _audit.Events);
    }

    [Fact]
    public async Task A_failing_confirmation_read_refuses_fail_closed()
    {
        var (gateway, targets, _) = Build();
        targets.BeforeConfirm = () => _docker.ListContainersThrows = true;

        var outcome = await Run(gateway, Restart());

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetNotResolved, outcome.ReasonCode);
        Assert.Equal(0, _runs);
        Assert.Equal(["begin:container.restart:", "complete:Denied"], _audit.Events);
    }

    [Fact]
    public async Task The_claim_is_released_so_the_same_operation_id_can_run_once_the_object_is_back()
    {
        var (gateway, targets, _) = Build();
        var original = new ContainerInfo { Id = "aaa111local", Name = "web", ServerId = "local", Created = Created };
        targets.BeforeConfirm = () => _docker.Remove("aaa111local");
        Assert.Equal(MutationStatus.Denied, (await Run(gateway, Restart("op-retry"))).Status);

        _docker.Add(original);
        var retry = await Run(gateway, Restart("op-retry"));

        Assert.Equal(MutationStatus.Executed, retry.Status);
        Assert.Equal(1, _runs);
    }

    [Fact]
    public async Task The_approval_stays_usable_after_a_refused_confirmation()
    {
        var (gateway, targets, approvals) = Build(confirm: true);
        var raised = await Run(gateway, Restart(viaKey: true));
        Assert.Equal(MutationStatus.PendingApproval, raised.Status);
        Assert.True(await approvals.ResolveAsync(raised.ApprovalId!, true, "other-admin@example.com", McpPermissionLevels.Admin));

        // Approved, but the container was recreated meanwhile: refused, the approval is not used up.
        targets.BeforeConfirm = () =>
        {
            _docker.Remove("aaa111local");
            _docker.Add(new ContainerInfo { Id = "aaa111local", Name = "web", ServerId = "local", Created = Created.AddMinutes(5) });
        };
        var refused = await Run(gateway, Restart(viaKey: true));
        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Equal(MutationReasonCodes.TargetChanged, refused.ReasonCode);
        Assert.Equal(0, _runs);

        // The same call against the object as it is now: the approval was bound to its identity, which is unchanged.
        var retry = await Run(gateway, Restart(viaKey: true));
        Assert.Equal(MutationStatus.Executed, retry.Status);
        Assert.Equal(raised.ApprovalId, retry.ApprovalId);
        Assert.Equal(1, _runs);
    }

    [Fact]
    public async Task A_server_capability_is_not_confirmed()
    {
        var (gateway, targets, _) = Build();
        var outcome = await gateway.ExecuteAsync(
            new MutationRequest("host.exec", Admin, MutationSurface.Ui, new MutationTarget("host", "local", null, "local"),
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"serverId":"local","command":"uptime"}""")!),
            (context, _) => Task.FromResult(context.Target));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.IsType<ResolvedHostObject>(outcome.Value);
        Assert.Equal(0, targets.Confirmations);
    }

    [Fact]
    public async Task An_expected_target_that_resolves_differently_is_refused_before_policy()
    {
        var (gateway, targets, _) = Build();
        var request = Restart() with { ExpectedTarget = new MutationTarget("container", "zzz999old", "web", "local") };

        var outcome = await Run(gateway, request);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetChanged, outcome.ReasonCode);
        Assert.Null(outcome.DecisionId);   // no policy decision was made
        Assert.Equal(0, targets.Confirmations);
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task An_expected_target_that_matches_runs()
    {
        var (gateway, _, _) = Build();
        var request = Restart() with { ExpectedTarget = new MutationTarget("container", "aaa111local", "web", "local") };

        Assert.Equal(MutationStatus.Executed, (await Run(gateway, request)).Status);
    }
}
