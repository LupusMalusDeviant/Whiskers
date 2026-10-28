using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>R1-15 (INV-32, C-07, C-19): expiry is checked when an approval is decided and when it is used; overdue
/// approvals are expired on a timer; pending approvals are bounded per principal.</summary>
public partial class ApprovalStoreTests
{
    /// <summary>A clock the test moves, whose timers the test fires.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public List<(TimerCallback Callback, object? State)> Timers { get; } = [];
        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers.Add((callback, state));
            return new NoTimer();
        }

        public void FireTimers()
        {
            foreach (var (callback, state) in Timers) callback(state);
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private static ApprovalResolver Admin(string email = "bob@example.com") =>
        new(PrincipalIds.User(email), McpPermissionLevels.Admin, email);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_approval_past_its_expiry_is_expired_not_decided(bool approve)
    {
        var time = new ManualTime();
        var store = new ApprovalStore(approvalTtl: Ttl, time: time);
        var decisions = new List<bool>();
        var a = await store.CreateAsync(Req(), d => { decisions.Add(d); return Task.CompletedTask; });

        time.Now += Ttl;   // exactly at expiry: no longer decidable
        Assert.Equal(ApprovalDecisionResult.Expired, store.CanDecide(a.Id, Admin()));
        Assert.Equal(ApprovalDecisionResult.Expired, await store.DecideAsync(a.Id, approve, Admin()));

        Assert.Equal(ApprovalStatus.Expired, a.Status);
        Assert.Equal([false], decisions);   // the waiting session is released with a "no"
        Assert.Equal(ApprovalDecisionResult.NotPending, await store.DecideAsync(a.Id, true, Admin()));
    }

    [Fact]
    public async Task Just_before_expiry_it_can_still_be_decided()
    {
        var time = new ManualTime();
        var store = new ApprovalStore(approvalTtl: Ttl, time: time);
        var a = await store.CreateAsync(Req(), _ => Task.CompletedTask);

        time.Now += Ttl - TimeSpan.FromSeconds(1);
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(a.Id, true, Admin()));
        Assert.Equal(ApprovalStatus.Approved, a.Status);
    }

    [Fact]
    public async Task The_record_refuses_a_decision_after_expiry_even_when_the_store_has_not_noticed()
    {
        // Another process (or a store whose clock lags) decides an approval that expired on record.
        var persistence = new InMemoryApprovalPersistence();
        var time = new ManualTime();
        var store = new ApprovalStore(approvalTtl: Ttl, time: time, persistence: persistence);
        var a = await store.CreateAsync(Req(), _ => Task.CompletedTask);

        Assert.False(await persistence.ResolveAsync(a.Id, ApprovalStatus.Approved, a.ExpiresAt, "bob", a.ExpiresAt.AddMinutes(10)));
        Assert.False(await persistence.ResolveAsync(a.Id, ApprovalStatus.Rejected, a.ExpiresAt.AddSeconds(1), "bob", null));
        // The system may still close it.
        Assert.True(await persistence.ResolveAsync(a.Id, ApprovalStatus.Expired, a.ExpiresAt.AddSeconds(1), "system", null));
    }

    [Fact]
    public async Task An_approved_approval_is_usable_only_within_its_grant()
    {
        var time = new ManualTime();
        var store = new ApprovalStore(approvalTtl: Ttl, time: time);
        var late = await store.CreateAsync(Req(toolCallId: "late"), _ => Task.CompletedTask);
        var onTime = await store.CreateAsync(Req(toolCallId: "on-time"), _ => Task.CompletedTask);
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(late.Id, true, Admin()));
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(onTime.Id, true, Admin()));

        var grantEnd = time.Now.UtcDateTime + ApprovalStore.GrantLifetime;
        Assert.False(await store.TryConsumeAsync(late.Id, "op-late", grantEnd));
        Assert.True(await store.TryConsumeAsync(onTime.Id, "op-1", grantEnd.AddSeconds(-1)));
        Assert.Null(late.ConsumedAt);
    }

    [Fact]
    public async Task Overdue_approvals_are_expired_on_a_timer_and_release_the_waiting_session()
    {
        var time = new ManualTime();
        var store = new ApprovalStore(approvalTtl: Ttl, time: time);
        var released = new TaskCompletionSource<bool>();
        var a = await store.CreateAsync(Req(), d => { released.TrySetResult(d); return Task.CompletedTask; });

        time.Now += Ttl;
        time.FireTimers();   // nobody looks at the list

        Assert.False(await released.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ApprovalStatus.Expired, a.Status);
        Assert.NotEmpty(time.Timers);
    }

    [Fact]
    public async Task Pending_approvals_are_bounded_per_requesting_principal()
    {
        var store = new ApprovalStore();
        ApprovalRequest From(string requester, int n) => Req(toolCallId: $"c{n}") with { Requesters = [requester, "user:owner@example.com"] };

        var first = await store.CreateAsync(From("key:k1", 0), _ => Task.CompletedTask);
        for (var i = 1; i < ApprovalEligibility.MaxPendingPerPrincipal; i++)
            await store.CreateAsync(From("key:k1", i), _ => Task.CompletedTask);

        var refused = await Assert.ThrowsAsync<ApprovalLimitException>(
            () => store.CreateAsync(From("key:k1", 99), _ => Task.CompletedTask));
        Assert.Equal("key:k1", refused.Requester);
        Assert.Equal(ApprovalEligibility.MaxPendingPerPrincipal, store.GetPending().Count);

        // Another principal is not affected; a decided approval frees a place.
        await store.CreateAsync(From("key:k2", 0), _ => Task.CompletedTask);
        Assert.Equal(ApprovalDecisionResult.Decided, await store.DecideAsync(first.Id, false, Admin()));
        await store.CreateAsync(From("key:k1", 100), _ => Task.CompletedTask);
    }

    [Fact]
    public async Task Concurrent_requests_cannot_exceed_the_bound()
    {
        var store = new ApprovalStore();
        var attempts = Enumerable.Range(0, ApprovalEligibility.MaxPendingPerPrincipal * 2).Select(i => Task.Run(async () =>
        {
            try
            {
                await store.CreateAsync(Req(toolCallId: $"c{i}") with { Requesters = ["key:k1"] }, _ => Task.CompletedTask);
                return true;
            }
            catch (ApprovalLimitException) { return false; }
        }));

        var results = await Task.WhenAll(attempts);
        Assert.Equal(ApprovalEligibility.MaxPendingPerPrincipal, results.Count(r => r));
        Assert.Equal(ApprovalEligibility.MaxPendingPerPrincipal, store.GetPending().Count);
    }
}
