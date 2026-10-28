using System.Text.Json;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;

namespace Whiskers.Tests;

/// <summary>The approval gate used by the Mutation Gateway and the direct-MCP guard: an approval counts only for
/// exactly the call a human saw, once, and within its lifetime.</summary>
public class ApprovalGateTests
{
    /// <summary>The binding the gateway computes for these calls (R1-14); the gate only compares it.</summary>
    private const string Bound = "binding-1";

    private static Dictionary<string, JsonElement> Args(string container) =>
        new() { ["containerId"] = JsonSerializer.SerializeToElement(container) };

    /// <summary>The approval an agent session raises through ApprovalCoordinator for one call.</summary>
    private static Task<Approval> RaiseForSession(ApprovalStore store, string session, string operation, string container) =>
        store.CreateAsync(new ApprovalRequest(session, "call-1", "ops@example.com", "agent-web", "restart_container",
            McpPermissionLevels.Write, null, "needs a human", CorrelationId: operation,
            ArgumentsHash: ApprovalFingerprint.OfArguments(Args(container)), Binding: Bound, Interactive: true),
            _ => Task.CompletedTask);

    private static ApprovalSubject SessionCall(string session, string operation, string container) =>
        new("restart_container", McpPermissionLevels.Write, "Agent:agent:ops", "agent:ops", "agent", Args(container),
            operation, session) { Binding = Bound };

    [Fact]
    public async Task A_session_approval_lets_its_own_operation_run_exactly_once()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var approval = await RaiseForSession(store, "s1", "op-1", "web-1");
        Assert.True(await store.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin));

        var first = await gate.CheckAsync(SessionCall("s1", "op-1", "web-1"), "needs a human", []);
        var consumed = await gate.TryConsumeAsync(first.ApprovalId!, "op");
        var second = await gate.CheckAsync(SessionCall("s1", "op-1", "web-1"), "needs a human", []);

        Assert.True(first.Approved);
        Assert.Equal(approval.Id, first.ApprovalId);
        Assert.Equal("admin@example.com", first.ApprovedBy);
        Assert.True(consumed);
        Assert.False(await gate.TryConsumeAsync(approval.Id, "op"));
        Assert.False(second.Approved);
        Assert.Contains("already used", second.Message);
    }

    [Fact]
    public async Task Checking_does_not_use_an_approval_up_only_consuming_does()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var approval = await RaiseForSession(store, "s1", "op-1", "web-1");
        await store.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin);

        // A refusal between the check and the run (a failed audit intent) must leave the approval usable.
        var first = await gate.CheckAsync(SessionCall("s1", "op-1", "web-1"), "needs a human", []);
        var second = await gate.CheckAsync(SessionCall("s1", "op-1", "web-1"), "needs a human", []);

        Assert.True(first.Approved);
        Assert.True(second.Approved);
        Assert.True(await gate.TryConsumeAsync(approval.Id, "op"));
    }

    [Fact]
    public async Task An_undecided_or_unknown_approval_cannot_be_consumed()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var pending = await RaiseForSession(store, "s1", "op-1", "web-1");

        Assert.False(await gate.TryConsumeAsync(pending.Id, "op"));
        Assert.False(await gate.TryConsumeAsync("no-such-approval", "op"));
    }

    [Fact]
    public async Task Concurrent_identical_first_calls_raise_one_approval()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var direct = new ApprovalSubject("restart_container", McpPermissionLevels.Write, "key:k1", "key:k1",
            "mcp-direct", Args("web-1")) { Binding = Bound };

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => gate.CheckAsync(direct, "needs a human", []))));

        Assert.All(results, r => Assert.False(r.Approved));
        Assert.Single(results.Select(r => r.ApprovalId).Distinct());
        Assert.Single(store.GetAll());
    }

    [Fact]
    public async Task Approve_then_retry_runs_once_and_the_next_identical_call_needs_a_new_approval()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var direct = new ApprovalSubject("restart_container", McpPermissionLevels.Write, "key:k1", "key:k1",
            "mcp-direct", Args("web-1")) { Binding = Bound };

        var raised = await gate.CheckAsync(direct, "needs a human", []);
        await store.ResolveAsync(raised.ApprovalId!, true, "admin@example.com", McpPermissionLevels.Admin);
        var retry = await gate.CheckAsync(direct, "needs a human", []);
        var used = await gate.TryConsumeAsync(retry.ApprovalId!, "op");
        var next = await gate.CheckAsync(direct, "needs a human", []);

        Assert.True(retry.Approved);
        Assert.Equal(raised.ApprovalId, retry.ApprovalId);
        Assert.True(used);
        Assert.False(next.Approved);
        Assert.NotEqual(raised.ApprovalId, next.ApprovalId);
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public async Task A_rejected_retry_approval_is_forgotten_and_the_call_asks_again()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var direct = new ApprovalSubject("restart_container", McpPermissionLevels.Write, "key:k1", "key:k1",
            "mcp-direct", Args("web-1")) { Binding = Bound };

        var raised = await gate.CheckAsync(direct, "needs a human", []);
        await store.ResolveAsync(raised.ApprovalId!, false, "admin@example.com", McpPermissionLevels.Admin);
        var again = await gate.CheckAsync(direct, "needs a human", []);

        Assert.False(again.Approved);
        Assert.NotEqual(raised.ApprovalId, again.ApprovalId);
    }

    [Fact]
    public async Task A_session_approval_does_not_cover_other_arguments()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var approval = await RaiseForSession(store, "s1", "op-1", "web-1");
        await store.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin);

        var result = await gate.CheckAsync(SessionCall("s1", "op-1", "db-1"), "needs a human", []);

        Assert.False(result.Approved);
        Assert.Contains("different arguments", result.Message);
    }

    [Theory]
    [InlineData(false)]   // still pending
    [InlineData(true)]    // rejected
    public async Task An_undecided_or_rejected_session_approval_runs_nothing_and_raises_nothing(bool reject)
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var approval = await RaiseForSession(store, "s1", "op-1", "web-1");
        if (reject) await store.ResolveAsync(approval.Id, false, "admin@example.com", McpPermissionLevels.Admin);

        var result = await gate.CheckAsync(SessionCall("s1", "op-1", "web-1"), "needs a human", []);

        Assert.False(result.Approved);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public async Task An_approval_older_than_its_lifetime_is_not_used()
    {
        var store = new ApprovalStore();
        var clock = new ShiftedClock();
        var gate = new ApprovalGate(store, time: clock);
        var approval = await RaiseForSession(store, "s1", "op-1", "web-1");
        await store.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin);

        clock.Shift = ApprovalGate.GrantLifetime + TimeSpan.FromSeconds(1);
        var result = await gate.CheckAsync(SessionCall("s1", "op-1", "web-1"), "needs a human", []);

        Assert.False(result.Approved);
    }

    [Fact]
    public async Task Without_an_operation_of_its_own_a_call_raises_an_approval_bound_to_its_arguments()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var subject = new ApprovalSubject("restart_container", McpPermissionLevels.Write, "ApiKey:key:k1", "ci",
            "mcp-direct", Args("web-1")) { Binding = Bound };

        var result = await gate.CheckAsync(subject, "needs a human", ["confirm"]);

        Assert.False(result.Approved);
        var raised = Assert.Single(store.GetPending());
        Assert.Equal(raised.Id, result.ApprovalId);
        Assert.Equal(ApprovalFingerprint.OfArguments(Args("web-1")), raised.ArgumentsHash);
        Assert.Equal("mcp-direct", raised.SessionId);
        Assert.Equal("confirm", raised.GuardrailRule);
    }

    [Fact]
    public void The_arguments_hash_ignores_key_order_but_not_values()
    {
        var a = new Dictionary<string, JsonElement>
        {
            ["a"] = JsonSerializer.SerializeToElement(1), ["b"] = JsonSerializer.SerializeToElement("x"),
        };
        var b = new Dictionary<string, JsonElement>
        {
            ["b"] = JsonSerializer.SerializeToElement("x"), ["a"] = JsonSerializer.SerializeToElement(1),
        };
        var c = new Dictionary<string, JsonElement>
        {
            ["a"] = JsonSerializer.SerializeToElement(2), ["b"] = JsonSerializer.SerializeToElement("x"),
        };

        Assert.Equal(ApprovalFingerprint.OfArguments(a), ApprovalFingerprint.OfArguments(b));
        Assert.NotEqual(ApprovalFingerprint.OfArguments(a), ApprovalFingerprint.OfArguments(c));
    }

    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Shift { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }

    [Fact]
    public async Task An_approval_below_the_effective_requirement_is_never_used()
    {
        // R1-05: raised at Write (the outer tool's level) and approved by an Operator, but the gateway now needs Admin.
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var approval = await RaiseForSession(store, "s1", "op-1", "web-1");
        Assert.True(await store.ResolveAsync(approval.Id, approved: true, "operator@x", McpPermissionLevels.Write));

        var admin = SessionCall("s1", "op-1", "web-1") with { Level = McpPermissionLevels.Admin };
        var result = await gate.CheckAsync(admin, "needs a human", []);
        Assert.False(result.Approved);
        Assert.Contains("requires 'admin'", result.Message);

        // At its own level the same approval still counts.
        Assert.True((await gate.CheckAsync(SessionCall("s1", "op-1", "web-1"), "needs a human", [])).Approved);
    }

    [Fact]
    public async Task A_retried_call_at_a_higher_level_raises_a_new_approval()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var write = new ApprovalSubject("restart_container", McpPermissionLevels.Write, "k", "k", "mcp", Args("web-1")) { Binding = Bound };
        var first = await gate.CheckAsync(write, "needs a human", []);
        Assert.True(await store.ResolveAsync(first.ApprovalId!, approved: true, "operator@x", McpPermissionLevels.Write));

        var retried = await gate.CheckAsync(write with { Level = McpPermissionLevels.Admin }, "needs a human", []);
        Assert.False(retried.Approved);
        Assert.NotEqual(first.ApprovalId, retried.ApprovalId);
        Assert.Equal(McpPermissionLevels.Admin, store.Get(retried.ApprovalId!)!.Level);
    }
}
