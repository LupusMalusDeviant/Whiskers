using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>R1-14 (INV-30/31): an approval is bound to the principal, its credential, the capability and level, the
/// normalized arguments, the resolved target, the policy decision it was given under and its expiry. A call that
/// differs in any of them never inherits it.</summary>
public class ApprovalBindingTests
{
    private static readonly MutationPrincipal Key = new(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Write,
        CredentialId: "k1");

    private static Dictionary<string, JsonElement> Args(string container, string? extra = null)
    {
        var args = new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement(container) };
        if (extra is not null) args["note"] = JsonSerializer.SerializeToElement(extra);
        return args;
    }

    private static MutationRequest Call(MutationPrincipal? principal = null, string container = "web-1", string? server = "srv1",
        string capability = "container.restart", string? extra = null, string? operationId = null, string? sessionId = null) =>
        new(capability, principal ?? Key, MutationSurface.Mcp, new MutationTarget("container", container, null, server),
            Args(container, extra), operationId, sessionId);

    /// <summary>Writes need a human; <paramref name="variant"/> changes the policy's content (a new version), not its
    /// verdict.</summary>
    private static GuardrailPolicy Policy(string? variant = null)
    {
        var policy = GuardrailPolicy.SafeDefault();
        if (variant is not null) policy.ForbiddenArgPatterns.Add(variant);
        return policy;
    }

    private static MutationGateway Gateway(IApprovalStore approvals, GuardrailPolicy? policy = null)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? Policy());
        return new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            approvals: new ApprovalGate(approvals, store));
    }

    /// <summary>Raises an approval for <paramref name="call"/> and has a human approve it.</summary>
    private static async Task<string> ApprovedAsync(ApprovalStore approvals, MutationGateway gateway, MutationRequest call)
    {
        var raised = await gateway.ExecuteAsync(call, (_, _) => Task.FromResult(0));
        Assert.Equal(MutationStatus.PendingApproval, raised.Status);
        Assert.True(await approvals.ResolveAsync(raised.ApprovalId!, true, "admin@example.com", McpPermissionLevels.Admin));
        return raised.ApprovalId!;
    }

    [Fact]
    public async Task A_gateway_approval_records_who_asked_and_the_resolved_target()
    {
        // R1-15: the requester (for separation of duties) and the target as resolved, not as read from the arguments.
        var approvals = new ApprovalStore();
        var raised = await Gateway(approvals).ExecuteAsync(Call(), (_, _) => Task.FromResult(0));
        var approval = approvals.Get(raised.ApprovalId!)!;
        Assert.Equal(["key:k1"], approval.Requesters);
        Assert.Equal(("srv1", "web-1"), (approval.TargetServer, approval.TargetWorkload));
    }

    public static TheoryData<string> Differences() =>
    [
        "principal", "credential", "capability", "arguments", "target", "server",
    ];

    [Theory]
    [MemberData(nameof(Differences))]
    public async Task A_call_that_differs_in_anything_bound_does_not_inherit_the_approval(string difference)
    {
        var approvals = new ApprovalStore();
        var gateway = Gateway(approvals);
        var approvalId = await ApprovedAsync(approvals, gateway, Call());
        var other = difference switch
        {
            "principal" => Call(principal: Key with { Id = "key:k2" }),
            "credential" => Call(principal: Key with { CredentialId = "k2" }),
            "capability" => Call(capability: "container.stop"),
            "arguments" => Call(extra: "changed"),
            "target" => Call(container: "web-2"),
            "server" => Call(server: "srv2"),
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };
        var runs = 0;

        var outcome = await gateway.ExecuteAsync(other, (_, _) => Task.FromResult(++runs));

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.NotEqual(approvalId, outcome.ApprovalId);
        Assert.Equal(0, runs);
        // The original call still has its approval.
        Assert.Equal(MutationStatus.Executed, (await gateway.ExecuteAsync(Call(), (_, _) => Task.FromResult(1))).Status);
    }

    [Fact]
    public async Task A_changed_policy_version_does_not_inherit_the_approval()
    {
        var approvals = new ApprovalStore();
        var approvalId = await ApprovedAsync(approvals, Gateway(approvals), Call());
        var runs = 0;

        // The same call, decided again under a changed policy (still Confirm, but another version).
        var changed = Gateway(approvals, Policy("forbidden-xyz"));
        var outcome = await changed.ExecuteAsync(Call(), (_, _) => Task.FromResult(++runs));

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.NotEqual(approvalId, outcome.ApprovalId);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task An_agent_approval_decided_under_one_policy_is_refused_under_another()
    {
        var approvals = new ApprovalStore();
        var request = Call(principal: Key, operationId: "op-1", sessionId: "s1");
        var decided = await Gateway(approvals).DecideAsync(request);
        var approval = await approvals.CreateAsync(new ApprovalRequest("s1", "call-1", "ci", "agent-mcp",
            "restart_container", McpPermissionLevels.Write, null, "needs a human", CorrelationId: "op-1",
            ArgumentsHash: ApprovalFingerprint.OfArguments(request.Arguments), Binding: decided!.Binding,
            Interactive: true, Requesters: ["key:test-requester"]), _ => Task.CompletedTask);
        await approvals.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin);
        var runs = 0;

        var changed = await Gateway(approvals, Policy("forbidden-xyz")).ExecuteAsync(request, (_, _) => Task.FromResult(++runs));
        var same = await Gateway(approvals).ExecuteAsync(request, (_, _) => Task.FromResult(++runs));

        Assert.NotEqual(MutationStatus.Executed, changed.Status);
        Assert.Contains("policy version", changed.Reason);
        Assert.Equal(MutationStatus.Executed, same.Status);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task An_approval_without_a_binding_is_never_used()
    {
        // An approval raised before R1-14, or by a caller that did not bind it.
        var approvals = new ApprovalStore();
        var request = Call(operationId: "op-1", sessionId: "s1");
        var approval = await approvals.CreateAsync(new ApprovalRequest("s1", "call-1", "ci", "agent-mcp",
            "restart_container", McpPermissionLevels.Write, null, "needs a human", CorrelationId: "op-1",
            ArgumentsHash: ApprovalFingerprint.OfArguments(request.Arguments), Requesters: ["key:test-requester"]), _ => Task.CompletedTask);
        await approvals.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin);

        var outcome = await Gateway(approvals).ExecuteAsync(request, (_, _) => Task.FromResult(1));

        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
    }

    [Fact]
    public async Task A_subject_without_a_binding_neither_raises_nor_uses_an_approval()
    {
        var approvals = new ApprovalStore();
        var unbound = new ApprovalSubject("restart_container", McpPermissionLevels.Write, "key:k1", "ci", "mcp-direct",
            Args("web-1")) { Requesters = ["key:test-requester"] };

        var result = await new ApprovalGate(approvals).CheckAsync(unbound, "needs a human", []);

        Assert.False(result.Approved);
        Assert.Empty(approvals.GetAll());
    }

    [Fact]
    public async Task An_approval_is_usable_only_until_its_grant_expires()
    {
        var clock = new ShiftedClock();
        var approvals = new ApprovalStore();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(Policy());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            approvals: new ApprovalGate(approvals, store, time: clock));
        var approvalId = await ApprovedAsync(approvals, gateway, Call());
        Assert.NotNull(approvals.Get(approvalId)!.GrantExpiresAt);

        clock.Shift = ApprovalStore.GrantLifetime + TimeSpan.FromSeconds(1);
        var outcome = await gateway.ExecuteAsync(Call(), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.NotEqual(approvalId, outcome.ApprovalId);
    }

    [Fact]
    public void The_binding_covers_every_bound_field_and_ignores_argument_order()
    {
        var capability = new CapabilityCatalog().Find("container.restart")!;
        string Of(MutationRequest r, Capability? c = null, string? version = "v1") => ApprovalBinding.Of(r, c ?? capability, version);
        var baseline = Of(Call());
        var reordered = Call() with
        {
            Arguments = new Dictionary<string, JsonElement>(Args("web-1", "x").Reverse()),
        };

        Assert.Equal(baseline, Of(Call()));
        Assert.Equal(Of(Call(extra: "x")), Of(reordered));
        Assert.NotEqual(baseline, Of(Call(), version: "v2"));
        Assert.Null(Of(Call(), version: null));          // no policy version, no binding: nothing to approve against
        Assert.NotEqual(baseline, Of(Call(), capability with { RequiredLevel = McpPermissionLevels.Admin }));
        Assert.NotEqual(baseline, Of(Call() with { Target = Call().Target with { Related = ["db"] } }));
        Assert.NotEqual(baseline, Of(Call(principal: Key with { OnBehalfOf = Key with { Id = "user:x" } })));
    }

    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Shift { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }
}
