namespace Whiskers.Services.Docker.Budget;

/// <summary>What the budget currently looks like for one server. Read-only view for the self-metrics
/// (SP-3) and the server page; the numbers are the raw material Plan-0003 exports.</summary>
public sealed record ServerBudgetSnapshot(
    string ServerId,
    int BackgroundInFlight,
    int InteractiveInFlight,
    int BackgroundLimit,
    int InteractiveLimit,
    long Started,
    long WaitedMillisecondsTotal,
    long MaxWaitMilliseconds,
    long DiscardedDuplicates,
    /// <summary>Calls whose deadline expired mostly in this queue rather than at the server — Whiskers being
    /// the bottleneck. Kept apart from failures so it cannot be mistaken for an unhealthy host.</summary>
    long SaturationFailures);

/// <summary>
/// Caps the load Whiskers puts on ONE server, across every caller.
///
/// <para>Before this existed each background loop had its own timeout and no idea what the others were doing
/// on the same host. The server, of course, sees the sum. On 2026-08-26 that sum was thirteen concurrent
/// full-log scans against a two-core machine. A per-loop fix would have left the next loop free to repeat it,
/// which is why the limit lives here — at the single point every Docker call passes through — instead of in
/// the loops.</para>
///
/// <para>Two separate lanes: background work and anything a human is waiting for. They must not share a
/// queue, or a CVE scan holding the budget makes the UI look frozen and the health checks stop running while
/// everything still reports "fine". Callers mark background work with <see cref="BackgroundScope"/>; anything
/// unmarked counts as interactive, which is the safe default — mistaking a loop for a user costs a slot,
/// mistaking a user for a loop costs responsiveness.</para>
/// </summary>
/// <summary>Thrown when a background caller asks for something an identical background request is already
/// fetching. Not an error condition — the next cycle will ask again in a minute.</summary>
public sealed class DuplicateRequestException(string key)
    : Exception($"An identical background request is already in flight: {key}");

/// <summary>
/// A call that ran out of time mostly while queued for a budget slot, not while talking to the server.
///
/// <para>The distinction decides who gets blamed. A deadline that expired at the host is a statement about the
/// host and must count towards its circuit — that was the signal the 2026-08-26 incident produced for six days
/// and nobody tallied. A deadline that expired in <em>our own</em> queue says nothing about the host at all,
/// and counting it opens circuits across the whole fleet at once whenever Whiskers itself is busy. That is
/// exactly what happened on 2026-08-27: a slow dashboard and a stream of "paused / resumed" notifications
/// about servers that were perfectly healthy.</para>
/// </summary>
public sealed class BudgetSaturatedException(string serverId, TimeSpan waited, TimeSpan ran, Exception inner)
    : Exception($"Call to '{serverId}' spent {waited.TotalSeconds:F1}s waiting for a budget slot and only " +
                $"{ran.TotalSeconds:F1}s running before its deadline expired — the queue ran out the clock, " +
                "not the server.", inner)
{
    public string ServerId { get; } = serverId;
    public TimeSpan Waited { get; } = waited;
    public TimeSpan Ran { get; } = ran;
}

public interface IServerBudget
{
    /// <summary>Runs the operation once a slot on that server is free. Cancellation applies to the wait as
    /// well as the operation — a caller that gave up must not still be holding a place in the queue.
    ///
    /// <para><paramref name="singleFlightKey"/> discards a second <b>background</b> request for something
    /// already in flight (Plan-0001 WP3.2), throwing <see cref="DuplicateRequestException"/> rather than
    /// queueing it. Interactive callers ignore the key: for a background loop a discarded request costs one
    /// skipped cycle, but for a person it would be an error message where an answer belongs — and the load it
    /// would save is already capped by the budget itself.</para>
    ///
    /// <para><paramref name="deadline"/> (R1-41) is a deadline about the call rather than the caller giving up: it also ends
    /// the wait for a slot, and running out while still queued is <see cref="BudgetSaturatedException"/> — the queue ran
    /// out the clock, not the server. <paramref name="ct"/> stays the caller's own cancellation and is never reported as
    /// saturation.</para></summary>
    Task<T> RunAsync<T>(string serverId, Func<Task<T>> operation, CancellationToken ct = default, string? singleFlightKey = null,
        CancellationToken deadline = default);

    /// <summary>Marks everything inside as background work for the current async flow. Nested scopes are
    /// harmless; the flag is restored on dispose.</summary>
    IDisposable BackgroundScope();

    /// <summary>Whether the current call is running inside a <see cref="BackgroundScope"/>.
    /// The emergency stop needs this: it cuts background traffic to a paused server but leaves the
    /// operator able to look at the very server they just paused.</summary>
    bool IsBackgroundCall { get; }

    /// <summary>Current usage for one server. Never throws for an unknown server — it reports an idle
    /// budget, because "no data" and "nothing running" must not be told apart by an exception.</summary>
    ServerBudgetSnapshot Snapshot(string serverId);

    /// <summary>Every server the budget has seen, for the self-metrics loop.</summary>
    IReadOnlyList<ServerBudgetSnapshot> SnapshotAll();
}
