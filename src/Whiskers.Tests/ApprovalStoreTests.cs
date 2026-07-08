using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Approvals;

namespace Whiskers.Tests;

public class ApprovalStoreTests
{
    private static ApprovalRequest Req(string session = "s1", string toolCallId = "tc1") => new(
        SessionId: session,
        ToolCallId: toolCallId,
        Actor: "alice@example.com",
        ActorType: "agent-web",
        ToolName: "stop_container",
        Level: "write",
        ParamsJson: "{\"id\":\"abc\"}",
        Reason: "Schreibender Zugriff erfordert Freigabe.");

    [Fact]
    public void Create_adds_a_pending_approval_and_fires_Changed()
    {
        var store = new ApprovalStore();
        var fired = 0;
        store.Changed += () => fired++;

        var a = store.Create(Req(), _ => Task.CompletedTask);

        Assert.Equal(ApprovalStatus.Pending, a.Status);
        Assert.Single(store.GetPending());
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Resolve_approve_invokes_resolver_with_true_and_marks_approved()
    {
        var store = new ApprovalStore();
        bool? decision = null;
        var a = store.Create(Req(), approved => { decision = approved; return Task.CompletedTask; });

        var ok = await store.ResolveAsync(a.Id, approved: true, resolvedBy: "bob@example.com");

        Assert.True(ok);
        Assert.True(decision);
        Assert.Equal(ApprovalStatus.Approved, a.Status);
        Assert.Equal("bob@example.com", a.ResolvedBy);
        Assert.Empty(store.GetPending());
    }

    [Fact]
    public async Task Resolve_reject_invokes_resolver_with_false()
    {
        var store = new ApprovalStore();
        bool? decision = null;
        var a = store.Create(Req(), approved => { decision = approved; return Task.CompletedTask; });

        await store.ResolveAsync(a.Id, approved: false, resolvedBy: "bob");

        Assert.False(decision);
        Assert.Equal(ApprovalStatus.Rejected, a.Status);
    }

    [Fact]
    public async Task Resolving_twice_returns_false_and_runs_resolver_once()
    {
        var store = new ApprovalStore();
        var calls = 0;
        var a = store.Create(Req(), _ => { calls++; return Task.CompletedTask; });

        Assert.True(await store.ResolveAsync(a.Id, true, "x"));
        Assert.False(await store.ResolveAsync(a.Id, true, "x"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ResolveAsync_unknown_id_returns_false()
    {
        var store = new ApprovalStore();
        Assert.False(await store.ResolveAsync("nope", true, "x"));
    }

    [Fact]
    public async Task CancelForSession_denies_only_that_sessions_pending_approvals()
    {
        var store = new ApprovalStore();
        var d1 = (bool?)null;
        var d2 = (bool?)null;
        var a1 = store.Create(Req(session: "s1", toolCallId: "a"), v => { d1 = v; return Task.CompletedTask; });
        var a2 = store.Create(Req(session: "s2", toolCallId: "b"), v => { d2 = v; return Task.CompletedTask; });

        await store.CancelForSessionAsync("s1");

        Assert.Equal(ApprovalStatus.Cancelled, a1.Status);
        Assert.False(d1);
        Assert.Equal(ApprovalStatus.Pending, a2.Status);
        Assert.Null(d2);
        Assert.Single(store.GetPending());
    }
}
