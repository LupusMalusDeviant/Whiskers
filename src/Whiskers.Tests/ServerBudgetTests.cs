using Whiskers.Services.Docker.Budget;

namespace Whiskers.Tests;

/// <summary>
/// The per-server load cap (Plan-0001 WP3).
///
/// <para>The budget exists because the server sees the <em>sum</em> of what every loop does, while each loop
/// only ever policed itself. These tests pin the two properties that make it worth having: the sum really is
/// bounded, and the bound never costs a waiting human their answer.</para>
/// </summary>
public class ServerBudgetTests
{
    /// <summary>Runs <paramref name="callers"/> operations at once and reports the highest number that were
    /// ever inside the budget at the same moment.</summary>
    private static async Task<int> PeakConcurrencyAsync(
        IServerBudget budget, string serverId, int callers, bool background, int expected)
    {
        var inFlight = 0;
        var peak = 0;
        var release = new TaskCompletionSource();
        var everyoneIn = new TaskCompletionSource();

        var running = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            using var scope = background ? budget.BackgroundScope() : null;
            await budget.RunAsync(serverId, async () =>
            {
                var now = Interlocked.Increment(ref inFlight);
                int seen;
                while (now > (seen = Volatile.Read(ref peak)))
                    Interlocked.CompareExchange(ref peak, now, seen);

                if (now >= expected) everyoneIn.TrySetResult();

                await release.Task;              // hold the slot until everyone has had a chance to enter
                Interlocked.Decrement(ref inFlight);
                return 0;
            });
        })).ToArray();

        // Wait for the callers the budget SHOULD admit rather than for a fixed span. The fixed 200 ms was a
        // guess about the thread pool: under the full suite the pool is busy, sometimes only one of the six
        // Task.Run bodies had started when the clock ran out, and the test failed for a reason that had
        // nothing to do with the budget. A flaky guard is a guard people end up muting.
        await everyoneIn.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Then a moment for a caller the budget should have HELD BACK to sneak in — that would raise the peak
        // above the limit, which is the failure this test exists to catch.
        await Task.Delay(100);
        release.SetResult();
        await Task.WhenAll(running);

        return Volatile.Read(ref peak);
    }

    [Fact]
    public async Task Background_work_never_exceeds_the_limit_for_a_server()
    {
        var budget = TestBudget.Create(background: 3, interactive: 4);

        var peak = await PeakConcurrencyAsync(budget, "host-a", callers: 12, background: true, expected: 3);

        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task A_raised_limit_really_raises_the_ceiling()
    {
        // The counter-proof for the test above: if the cap were not actually enforced, both cases would show
        // the same number and the assertion would be measuring nothing.
        var peak = await PeakConcurrencyAsync(TestBudget.Create(background: 8), "host-a", callers: 12, background: true, expected: 8);

        Assert.Equal(8, peak);
    }

    [Fact]
    public async Task Each_server_gets_its_own_budget()
    {
        var budget = TestBudget.Create(background: 2);

        var a = PeakConcurrencyAsync(budget, "host-a", callers: 6, background: true, expected: 2);
        var b = PeakConcurrencyAsync(budget, "host-b", callers: 6, background: true, expected: 2);
        await Task.WhenAll(a, b);

        // A busy host must not throttle a healthy one — the fleet is not one queue.
        Assert.Equal(2, await a);
        Assert.Equal(2, await b);
    }

    [Fact]
    public async Task A_full_background_lane_does_not_block_a_waiting_human()
    {
        var budget = TestBudget.Create(background: 2, interactive: 2);
        var hold = new TaskCompletionSource();

        // Fill the background lane and keep it full.
        var blocked = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var scope = budget.BackgroundScope();
            await budget.RunAsync("host-a", async () => { await hold.Task; return 0; });
        })).ToArray();

        await Task.Delay(100);

        // No BackgroundScope → interactive lane. This is the property that keeps a CVE scan from making the
        // UI look frozen, which in turn is what makes people think the server is down.
        var interactive = budget.RunAsync("host-a", () => Task.FromResult(42));
        var winner = await Task.WhenAny(interactive, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.True(winner == interactive, "an interactive call waited behind saturated background work");
        Assert.Equal(42, await interactive);

        hold.SetResult();
        await Task.WhenAll(blocked);
    }

    [Fact]
    public async Task A_caller_that_gives_up_while_queued_leaves_the_queue()
    {
        var budget = TestBudget.Create(background: 1);
        var hold = new TaskCompletionSource();

        using var occupied = budget.BackgroundScope();
        var holder = budget.RunAsync("host-a", async () => { await hold.Task; return 0; });
        await Task.Delay(50);

        using var cts = new CancellationTokenSource(50);
        var queued = budget.RunAsync("host-a", () => Task.FromResult(1), cts.Token);

        // A timed-out caller must not keep its place — otherwise the backlog outlives the callers and the
        // next free slot is handed to someone who stopped caring, which is the incident's shape once more.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        hold.SetResult();
        await holder;

        // And the slot is genuinely free again afterwards.
        Assert.Equal(7, await budget.RunAsync("host-a", () => Task.FromResult(7)));
    }

    [Fact]
    public async Task The_snapshot_reports_what_is_running()
    {
        var budget = TestBudget.Create(background: 2, interactive: 2);
        var hold = new TaskCompletionSource();

        using var scope = budget.BackgroundScope();
        var running = budget.RunAsync("host-a", async () => { await hold.Task; return 0; });
        await Task.Delay(50);

        var snapshot = budget.Snapshot("host-a");
        Assert.Equal(1, snapshot.BackgroundInFlight);
        Assert.Equal(2, snapshot.BackgroundLimit);
        Assert.True(snapshot.Started >= 1);

        hold.SetResult();
        await running;

        // An unknown server reports an idle budget instead of throwing: "nothing running" and "never seen"
        // must not be distinguishable by an exception in a metrics path.
        Assert.Equal(0, budget.Snapshot("never-heard-of").BackgroundInFlight);
    }
}
