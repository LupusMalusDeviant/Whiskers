namespace Whiskers.Services.Observability.SelfMetrics;

/// <summary>One server's recent Docker API call durations (Plan-0004 WP4).</summary>
/// <param name="Samples">The durations, oldest first.</param>
/// <param name="LastAtUtc">When the newest of them was recorded.</param>
public sealed record ApiLatencyWindow(IReadOnlyList<TimeSpan> Samples, DateTime LastAtUtc);

/// <summary>How one background loop is doing against one server.</summary>
/// <param name="LastSuccess">When the loop last completed a cycle for this server. <b>The single most
/// important number in this type.</b> Failures are only counted when something happens; a loop that has
/// stopped entirely produces nothing at all, and the only thing that reveals it is the age of this
/// timestamp.</param>
/// <param name="SkipReason">Why the loop did not look at this server, when it deliberately skipped it —
/// "Kubernetes server, Docker loop", "suspended", "paused". A skipped server must still appear, or "the loop
/// does not run for this server" is indistinguishable from "the loop found nothing".</param>
/// <param name="ExpectedInterval">How often this loop intends to run. Reported by the loop itself, because
/// it is the only place that knows its own cadence — a supervisor with its own table of intervals would
/// quietly disagree with reality the first time someone changed a setting. Null means "cannot judge", and a
/// supervisor must then stay silent rather than guess.</param>
public sealed record LoopHealth(
    string Loop,
    string ServerId,
    DateTime? LastSuccess,
    DateTime? LastAttempt,
    TimeSpan LastDuration,
    long Cycles,
    long Failures,
    long Skips,
    string? SkipReason,
    TimeSpan? ExpectedInterval = null);

/// <summary>
/// What Whiskers knows about itself (Plan-0003 WP1/WP2).
///
/// <para>Whiskers exports a Prometheus endpoint carrying the container inventory of the whole fleet and not
/// one number about its own behaviour. On 2026-08-26 the log monitor wrote "timed out after 15s" every cycle
/// for six days; nothing counted it, so nothing could act on it. The incident report calls that the earliest
/// and most precise signal of the whole event.</para>
///
/// <para>This type deliberately does NOT re-measure what the load budget and the circuit breaker already
/// count. It covers the loops: did each one run, when did it last succeed, and how long did it take.</para>
/// </summary>
public interface ISelfMetrics
{
    /// <summary>A cycle finished for this server. <paramref name="interval"/> is the loop's own cadence, which
    /// is what lets a supervisor decide whether a gap is unusual — without it "no cycle for ten minutes" means
    /// nothing, since one loop runs every minute and another every six hours.</summary>
    void RecordCycle(string loop, string serverId, TimeSpan duration, bool success, TimeSpan? interval = null);

    /// <summary>The loop deliberately did not look at this server this cycle, and why. Recording the skip is
    /// the point: a server that simply vanishes from the metrics looks exactly like a quiet one.</summary>
    void RecordSkip(string loop, string serverId, string reason);

    /// <summary>Counts something worth counting that would otherwise only be a log line — a log-fetch timeout,
    /// a discarded duplicate. Keyed by a short, low-cardinality name.</summary>
    void Count(string name, string serverId);

    /// <summary>Seeds what was known before a restart (Plan-0003 WP3.2).
    ///
    /// <para>Without this, an empty <see cref="LoopHealth.LastSuccess"/> after a restart is indistinguishable
    /// from a loop that has never succeeded. A supervisor would then either alarm on every restart or have to
    /// ignore fresh loops entirely — one cries wolf, the other is deaf during the window when a bad deploy is
    /// most likely to have broken something.</para>
    ///
    /// <para>Only fills gaps: anything the running process has already observed wins, because a live reading
    /// is newer than anything on disk.</para></summary>
    void Restore(string loop, string serverId, DateTime? lastSuccess, TimeSpan? interval);

    IReadOnlyList<LoopHealth> Loops();

    /// <summary>Records how long one Docker API call to this server took (Plan-0004 WP4).
    ///
    /// <para>Only successful calls. A call that was cancelled at its timeout says "at least 8 seconds", not
    /// "8 seconds", and feeding the timeout value in as a measurement would peg the median at the timeout the
    /// moment a host goes fully silent — which the circuit breaker and the supervisory rule already cover far
    /// better. What this series is for is the case in between: a daemon still answering, but at 5 seconds
    /// instead of 100 milliseconds. That is the fingerprint of overload, and nothing else in Whiskers sees
    /// it.</para></summary>
    void RecordApiCall(string serverId, TimeSpan duration);

    /// <summary>Recent call durations per server, oldest first, with the time the newest one was recorded — both read
    /// under the same lock, so the time always belongs to the newest sample (review 1.0.0: a reader can tell a reading
    /// taken after an event from one taken before it). Bounded; see the implementation.</summary>
    IReadOnlyDictionary<string, ApiLatencyWindow> ApiLatencies();

    /// <summary>Named counters, as name → server → value.</summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Counters();
}
