using System.Collections.Concurrent;

namespace Whiskers.Services.Observability.SelfMetrics;

/// <summary>
/// In-process counters about Whiskers itself (Plan-0003 WP1/WP2).
///
/// <para>Everything here is a lock-free read of process memory. Collecting these numbers must never cost a
/// Docker call, a database round trip or a lock held across an await — a self-measurement that adds load is
/// the same mistake as the one it exists to reveal, one level up. <c>SelfMetricsTests</c> pins that.</para>
///
/// <para>Labels are fixed to loop, server and counter name. Container ids are deliberately impossible: with
/// 200 containers across a fleet, a container label would multiply the series count into the thousands and
/// fill the time-series database — a monitoring outage caused by monitoring.</para>
/// </summary>
public sealed class SelfMetrics : ISelfMetrics
{
    private sealed class LoopState
    {
        public DateTime? LastSuccess;
        public DateTime? LastAttempt;
        public long DurationTicks;
        public long Cycles;
        public long Failures;
        public long Skips;
        public string? SkipReason;
        public TimeSpan? ExpectedInterval;
    }

    // Key: "{loop}|{serverId}". Loops and servers are both bounded and small, so this never grows unbounded.
    private readonly ConcurrentDictionary<string, LoopState> _loops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _counters = new(StringComparer.Ordinal);

    // A queue per server rather than a lock-free structure: a median needs the samples themselves, and the
    // lock is held for a handful of instructions on a path that runs once per server per cycle.
    // Review 1.0.0: with the time of the newest sample, kept under the same lock so a reader never pairs one with the other's.
    private readonly ConcurrentDictionary<string, LatencySeries> _latencies = new(StringComparer.Ordinal);

    private sealed class LatencySeries
    {
        public readonly Queue<TimeSpan> Samples = new(LatencyWindow);
        public DateTime LastAtUtc;
    }

    private static string Key(string loop, string serverId) => $"{loop}|{serverId}";

    public void RecordCycle(string loop, string serverId, TimeSpan duration, bool success, TimeSpan? interval = null)
    {
        var s = _loops.GetOrAdd(Key(loop, serverId), _ => new LoopState());
        Interlocked.Increment(ref s.Cycles);
        Interlocked.Exchange(ref s.DurationTicks, duration.Ticks);
        s.LastAttempt = DateTime.UtcNow;
        s.SkipReason = null;
        if (interval is not null) s.ExpectedInterval = interval;

        if (success) s.LastSuccess = DateTime.UtcNow;
        else Interlocked.Increment(ref s.Failures);
    }

    public void RecordSkip(string loop, string serverId, string reason)
    {
        var s = _loops.GetOrAdd(Key(loop, serverId), _ => new LoopState());
        Interlocked.Increment(ref s.Skips);
        s.LastAttempt = DateTime.UtcNow;
        s.SkipReason = reason;
    }

    public void Count(string name, string serverId)
    {
        var perServer = _counters.GetOrAdd(name, _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal));
        perServer.AddOrUpdate(serverId, 1, (_, v) => v + 1);
    }

    public void Restore(string loop, string serverId, DateTime? lastSuccess, TimeSpan? interval)
    {
        var s = _loops.GetOrAdd(Key(loop, serverId), _ => new LoopState());

        // Never overwrite a live observation. This runs at startup, but a loop with a short cadence can
        // already have completed a cycle by the time the restore reaches it, and a stale timestamp from disk
        // would make a working loop look older than it is.
        if (s.LastSuccess is null && lastSuccess is not null) s.LastSuccess = lastSuccess;
        if (s.ExpectedInterval is null && interval is not null) s.ExpectedInterval = interval;
    }

    /// <summary>How many recent call durations are kept per server. At one probe per health cycle (30 s)
    /// this is about an hour — long enough for a baseline that a five-minute deploy cannot drag with it, short
    /// enough that a host which genuinely got slower is not compared against yesterday forever.</summary>
    private const int LatencyWindow = 120;

    public void RecordApiCall(string serverId, TimeSpan duration)
    {
        var series = _latencies.GetOrAdd(serverId, _ => new LatencySeries());
        lock (series)
        {
            series.Samples.Enqueue(duration);
            while (series.Samples.Count > LatencyWindow) series.Samples.Dequeue();
            series.LastAtUtc = DateTime.UtcNow;
        }
    }

    public IReadOnlyDictionary<string, ApiLatencyWindow> ApiLatencies() =>
        _latencies.ToDictionary(
            kv => kv.Key,
            kv => { lock (kv.Value) { return new ApiLatencyWindow(kv.Value.Samples.ToList(), kv.Value.LastAtUtc); } },
            StringComparer.Ordinal);

    public IReadOnlyList<LoopHealth> Loops() =>
        _loops
            .Select(kv =>
            {
                var parts = kv.Key.Split('|', 2);
                var s = kv.Value;
                return new LoopHealth(
                    parts[0], parts[1],
                    s.LastSuccess, s.LastAttempt,
                    TimeSpan.FromTicks(Interlocked.Read(ref s.DurationTicks)),
                    Interlocked.Read(ref s.Cycles),
                    Interlocked.Read(ref s.Failures),
                    Interlocked.Read(ref s.Skips),
                    s.SkipReason,
                    s.ExpectedInterval);
            })
            .OrderBy(l => l.Loop, StringComparer.Ordinal)
            .ThenBy(l => l.ServerId, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Counters() =>
        _counters.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, long>)kv.Value.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
}
