using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>R1-15 (INV-33/34/37, PC-D4): who may decide an approval — the requester, the level, approving and
/// rejecting alike.</summary>
public partial class ApprovalSeparationOfDutyTests
{
    private const string Alice = "alice@example.com";
    private const string Bob = "bob@example.com";

    private sealed class Policy(bool requireSecondApprover) : IGuardrailStore
    {
        public GuardrailPolicy Current { get; set; } = new() { RequireSecondApprover = requireSecondApprover };
        public GuardrailConfig Config => new();
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class BrokenPolicy : IGuardrailStore
    {
        public GuardrailPolicy Current => throw new InvalidOperationException("unreadable");
        public GuardrailConfig Config => new();
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    private static ApprovalRequest Request(string actorType, string level = McpPermissionLevels.Write,
        params string[] requesters) =>
        new("s1", "call-1", requesters.FirstOrDefault() ?? "someone", actorType, "stop_container", level, null,
            "needs a human", Requesters: requesters);

    private static ApprovalResolver User(string email, string level = McpPermissionLevels.Admin) =>
        new(PrincipalIds.User(email), level, email);

    private static async Task<(ApprovalStore Store, Approval Approval, List<bool> Decisions)> Raise(ApprovalRequest request,
        IGuardrailStore? policy = null, IApprovalPersistence? persistence = null)
    {
        var store = new ApprovalStore(policy: policy, persistence: persistence);
        var decisions = new List<bool>();
        var approval = await store.CreateAsync(request, d => { decisions.Add(d); return Task.CompletedTask; });
        return (store, approval, decisions);
    }

    // ---- PC-D4: the requester of a non-interactive request never decides it ---------------------------------------

    [Theory]
    [InlineData("mcp-direct")]
    [InlineData("automation")]
    [InlineData("webhook")]
    [InlineData("trigger")]
    [InlineData("agent")]
    [InlineData("agent-mcp")]
    [InlineData("something-new")]   // unknown kinds count as non-interactive
    public async Task The_requester_cannot_decide_a_non_interactive_request(string actorType)
    {
        foreach (var approve in new[] { true, false })
        {
            var (store, approval, decisions) = await Raise(Request(actorType, McpPermissionLevels.Write, PrincipalIds.User(Alice)));

            Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, await store.DecideAsync(approval.Id, approve, User(Alice)));
            Assert.Equal(ApprovalStatus.Pending, approval.Status);
            Assert.Empty(decisions);

            // Somebody else can.
            Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(approval.Id, approve, User(Bob)));
            Assert.Equal([approve], decisions);
            Assert.False(approval.SelfApproved);
        }
    }

    [Fact]
    public async Task Whoever_a_caller_acts_for_counts_as_requester_too()
    {
        // An automation acting for its owner (R1-08/09/13), started by an agent.
        var owner = new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(Alice), Alice, McpPermissionLevels.Admin);
        var automation = new MutationPrincipal(MutationPrincipalKind.Automation, "task:t1", "nightly (owner alice)",
            McpPermissionLevels.Admin, OnBehalfOf: owner, DelegatedBy: "agent:s9");
        var requesters = ApprovalEligibility.RequestersOf(automation);
        Assert.Equal(["task:t1", PrincipalIds.User(Alice)], requesters);

        var (store, approval, _) = await Raise(Request("automation", McpPermissionLevels.Write, [.. requesters]));
        Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, await store.DecideAsync(approval.Id, true, User(Alice)));
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(approval.Id, true, User(Bob)));
    }

    [Fact]
    public async Task The_requester_is_matched_by_principal_id_not_by_spelling_of_the_email()
    {
        var (store, approval, _) = await Raise(Request("mcp-direct", McpPermissionLevels.Write, PrincipalIds.User(Alice)));
        Assert.False(await store.ResolveAsync(approval.Id, true, "  ALICE@Example.com ", McpPermissionLevels.Admin));
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
    }

    // ---- interactive requests: self-confirm unless a second approver is required -----------------------------------

    [Theory]
    [InlineData("agent-web")]
    [InlineData("web")]
    [InlineData("terminal")]
    public async Task An_interactive_request_may_be_decided_by_its_requester_and_is_recorded_as_such(string actorType)
    {
        var persistence = new InMemoryApprovalPersistence();
        var (store, approval, decisions) = await Raise(
            Request(actorType, McpPermissionLevels.Write, PrincipalIds.User(Alice)), persistence: persistence);

        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(approval.Id, true, User(Alice)));
        Assert.Equal([true], decisions);
        Assert.True(approval.SelfApproved);

        // On record, with who asked.
        var stored = Assert.Single(await persistence.LoadHistoryAsync(0, 10));
        Assert.True(stored.SelfApproved);
        Assert.Equal([PrincipalIds.User(Alice)], stored.Requesters);
    }

    [Fact]
    public async Task A_second_approver_policy_stops_self_confirm_in_the_chat_both_ways()
    {
        foreach (var approve in new[] { true, false })
        {
            var (store, approval, decisions) = await Raise(
                Request("agent-web", McpPermissionLevels.Write, PrincipalIds.User(Alice)), new Policy(requireSecondApprover: true));

            Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, await store.DecideAsync(approval.Id, approve, User(Alice)));
            Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, store.CanDecide(approval.Id, User(Alice)));
            Assert.Empty(decisions);
            Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(approval.Id, approve, User(Bob)));
        }
    }

    [Fact]
    public async Task The_second_approver_policy_is_off_by_default_and_unreadable_means_required()
    {
        Assert.False(GuardrailPolicy.SafeDefault().RequireSecondApprover);
        Assert.True(GuardrailPolicy.FailClosed().RequireSecondApprover);

        var (open, a1, _) = await Raise(Request("agent-web", McpPermissionLevels.Write, PrincipalIds.User(Alice)), new Policy(false));
        Assert.Null(open.CanDecide(a1.Id, User(Alice)));

        var (closed, a2, _) = await Raise(Request("agent-web", McpPermissionLevels.Write, PrincipalIds.User(Alice)), new BrokenPolicy());
        Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, await closed.DecideAsync(a2.Id, true, User(Alice)));
    }

    [Fact]
    public void The_flag_is_left_out_of_a_stored_policy_while_off_so_existing_policy_versions_stay_the_same()
    {
        var off = PolicySnapshot.Of("Standard", GuardrailPolicy.SafeDefault());
        Assert.DoesNotContain(nameof(GuardrailPolicy.RequireSecondApprover), off.Json);

        var on = GuardrailPolicy.SafeDefault();
        on.RequireSecondApprover = true;
        Assert.NotEqual(off.VersionHash, PolicySnapshot.Of("Standard", on).VersionHash);
        Assert.True(on.Clone().RequireSecondApprover);
    }

    // ---- INV-34/37: the effective level, for approving and rejecting alike ---------------------------------------

    [Theory]
    [InlineData(McpPermissionLevels.Admin, McpPermissionLevels.Write)]
    [InlineData(McpPermissionLevels.Write, McpPermissionLevels.Read)]
    [InlineData(McpPermissionLevels.Admin, McpPermissionLevels.Read)]
    public async Task Rejecting_needs_the_same_level_as_approving(string approvalLevel, string resolverLevel)
    {
        foreach (var approve in new[] { true, false })
        {
            var (store, approval, decisions) = await Raise(Request("mcp-direct", approvalLevel, "key:k1"));

            Assert.Equal(ApprovalDecisionResult.InsufficientLevel, await store.DecideAsync(approval.Id, approve, User(Bob, resolverLevel)));
            Assert.False(await store.ResolveAsync(approval.Id, approve, Bob, resolverLevel));
            Assert.Equal(ApprovalStatus.Pending, approval.Status);
            Assert.Empty(decisions);

            Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(approval.Id, approve, User(Bob, approvalLevel)));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("superuser")]
    [InlineData("  ")]
    public async Task An_approval_with_an_unreadable_level_needs_an_admin(string level)
    {
        var (store, approval, _) = await Raise(Request("mcp-direct", level, "key:k1"));
        Assert.Equal(ApprovalDecisionResult.InsufficientLevel, await store.DecideAsync(approval.Id, true, User(Bob, McpPermissionLevels.Write)));
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(approval.Id, true, User(Bob, McpPermissionLevels.Admin)));
    }

    [Theory]
    [InlineData(null, McpPermissionLevels.Admin)]
    [InlineData("", McpPermissionLevels.Admin)]
    [InlineData(Bob, null)]
    [InlineData(Bob, "")]
    public async Task A_decision_without_a_resolver_or_a_level_decides_nothing(string? by, string? level)
    {
        foreach (var approve in new[] { true, false })
        {
            var (store, approval, decisions) = await Raise(Request("mcp-direct", McpPermissionLevels.Read, "key:k1"));
            Assert.False(await store.ResolveAsync(approval.Id, approve, by, level));
            Assert.Equal(ApprovalStatus.Pending, approval.Status);
            Assert.Empty(decisions);
        }
    }

    [Theory]
    [InlineData(AppRole.Admin, McpPermissionLevels.Admin)]
    [InlineData(AppRole.Operator, McpPermissionLevels.Write)]
    [InlineData(AppRole.Viewer, McpPermissionLevels.Read)]
    public void A_web_user_decides_at_the_level_of_their_current_role(AppRole role, string level)
    {
        var resolver = ApprovalResolver.User(" Alice@Example.com ", role);
        Assert.Equal(level, resolver.Level);
        Assert.Equal(PrincipalIds.User(Alice), resolver.PrincipalId);
    }

    [Fact]
    public async Task A_viewer_can_neither_approve_nor_reject_anything()
    {
        var (store, approval, _) = await Raise(Request("agent-web", McpPermissionLevels.Write, PrincipalIds.User(Alice)));
        var viewer = ApprovalResolver.User(Bob, AppRole.Viewer);
        Assert.Equal(ApprovalDecisionResult.InsufficientLevel, await store.DecideAsync(approval.Id, false, viewer));
        Assert.Equal(ApprovalDecisionResult.InsufficientLevel, await store.DecideAsync(approval.Id, true, viewer));
    }
}
