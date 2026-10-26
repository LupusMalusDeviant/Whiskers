using System.Text.Json;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;

namespace Whiskers.Tests;

/// <summary>The approval gate used by the Mutation Gateway and the direct-MCP guard: an approval counts only for
/// exactly the call a human saw, once, and within its lifetime.</summary>
public class ApprovalGateTests
{
    private static Dictionary<string, JsonElement> Args(string container) =>
        new() { ["containerId"] = JsonSerializer.SerializeToElement(container) };

    /// <summary>The approval an agent session raises through ApprovalCoordinator for one call.</summary>
    private static Approval RaiseForSession(ApprovalStore store, string session, string operation, string container) =>
        store.Create(new ApprovalRequest(session, "call-1", "ops@example.com", "agent-web", "restart_container",
            McpPermissionLevels.Write, null, "needs a human", CorrelationId: operation,
            ArgumentsHash: ApprovalFingerprint.OfArguments(Args(container))), _ => Task.CompletedTask);

    private static ApprovalSubject SessionCall(string session, string operation, string container) =>
        new("restart_container", McpPermissionLevels.Write, "Agent:agent:ops", "agent:ops", "agent", Args(container),
            operation, session);

    [Fact]
    public async Task A_session_approval_lets_its_own_operation_run_exactly_once()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var approval = RaiseForSession(store, "s1", "op-1", "web-1");
        Assert.True(await store.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin));

        var first = await gate.CheckAsync(SessionCall("s1", "op-1", "web-1"), "needs a human", []);
        var second = await gate.CheckAsync(SessionCall("s1", "op-1", "web-1"), "needs a human", []);

        Assert.True(first.Approved);
        Assert.Equal(approval.Id, first.ApprovalId);
        Assert.Equal("admin@example.com", first.ApprovedBy);
        Assert.False(second.Approved);
    }

    [Fact]
    public async Task A_session_approval_does_not_cover_other_arguments()
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store);
        var approval = RaiseForSession(store, "s1", "op-1", "web-1");
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
        var approval = RaiseForSession(store, "s1", "op-1", "web-1");
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
        var approval = RaiseForSession(store, "s1", "op-1", "web-1");
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
            "mcp-direct", Args("web-1"));

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
}
