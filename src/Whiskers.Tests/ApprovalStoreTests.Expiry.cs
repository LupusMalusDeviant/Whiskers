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

    /// <summary>A database whose decision write waits (a lock) until the clock has passed the approval's expiry.</summary>
    private sealed class LockedUntilExpiry(InMemoryApprovalPersistence inner, ManualTime time) : IApprovalPersistence
    {
        public Task InsertAsync(Approval approval, CancellationToken ct = default) => inner.InsertAsync(approval, ct);

        public Task<bool> ResolveAsync(string approvalId, ApprovalStatus status, DateTime at, string? by,
            DateTime? grantExpiresAt, bool selfApproved = false, CancellationToken ct = default)
        {
            if (ApprovalRecords.IsDecision(status)) time.Now += Ttl;   // the lock is released only after the expiry
            return inner.ResolveAsync(approvalId, status, at, by, grantExpiresAt, selfApproved, ct);
        }

        public Task<bool> ExpireLateDecisionAsync(string approvalId, ApprovalStatus decided, DateTime at,
            CancellationToken ct = default) => inner.ExpireLateDecisionAsync(approvalId, decided, at, ct);

        public Task<bool> ConsumeAsync(string approvalId, string operationId, DateTime now, CancellationToken ct = default) =>
            inner.ConsumeAsync(approvalId, operationId, now, ct);

        public Task<IReadOnlyList<Approval>> LoadOpenAsync(CancellationToken ct = default) => inner.LoadOpenAsync(ct);

        public Task<IReadOnlyList<Approval>> LoadHistoryAsync(int skip, int take, CancellationToken ct = default) =>
            inner.LoadHistoryAsync(skip, take, ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_decision_whose_write_completes_only_after_the_expiry_does_not_count(bool approve)
    {
        var time = new ManualTime();
        var records = new InMemoryApprovalPersistence();
        var store = new ApprovalStore(approvalTtl: Ttl, time: time, persistence: new LockedUntilExpiry(records, time));
        var decisions = new List<bool>();
        var a = await store.CreateAsync(Req(), d => { decisions.Add(d); return Task.CompletedTask; });

        time.Now += Ttl - TimeSpan.FromSeconds(1);   // decided one second before the expiry…
        var result = await store.DecideAsync(a.Id, approve, Admin());   // …but on record only after it

        Assert.Equal(ApprovalDecisionResult.Expired, result);
        Assert.Equal(ApprovalStatus.Expired, a.Status);
        Assert.Equal([false], decisions);   // the waiting session is released with a "no", never a "yes"
        Assert.Equal(nameof(ApprovalStatus.Expired), Assert.Single(await records.LoadHistoryAsync(0, 10)).Status.ToString());
        Assert.Empty(await records.LoadOpenAsync());
        Assert.False(await store.TryConsumeAsync(a.Id, "op-1", time.Now.UtcDateTime));
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
        ApprovalRequest From(string requester, int n) => Req(toolCallId: $"c{n}") with { Requesters = [requester, $"user:owner-of-{requester}"] };

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
    public async Task One_user_cannot_get_past_the_bound_by_opening_more_chats()
    {
        // An agent session's approvals count against the user it acts for too, not only against the session.
        var store = new ApprovalStore();
        ApprovalRequest InChat(string session, int n) =>
            Req(toolCallId: $"{session}-{n}") with { Requesters = [$"agent:{session}", "user:alice@example.com"] };

        for (var i = 0; i < ApprovalEligibility.MaxPendingPerPrincipal; i++)
            await store.CreateAsync(InChat($"chat-{i % 5}", i), _ => Task.CompletedTask);

        var refused = await Assert.ThrowsAsync<ApprovalLimitException>(
            () => store.CreateAsync(InChat("chat-new", 0), _ => Task.CompletedTask));
        Assert.Equal("user:alice@example.com", refused.Requester);
        Assert.Equal(ApprovalEligibility.MaxPendingPerPrincipal, store.GetPending().Count);
        // Another user's chat is not affected.
        await store.CreateAsync(Req(toolCallId: "b") with { Requesters = ["agent:chat-b", "user:bob@example.com"] },
            _ => Task.CompletedTask);
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
