using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Notifications;

namespace Whiskers.Services.Metrics.DiskHealth;

/// <summary>
/// R1-58 (contract 0005): the three disk-health alerts on the one duration model (<see cref="BreachTracker"/>, R1-56),
/// fed as 1/0 against threshold 1 per server:
/// <list type="bullet">
/// <item><c>disk_failing</c> — 1 while a SMART item is DegradedOrFailing; 0 only while every SMART item is Healthy.</item>
/// <item><c>array_degraded</c> — the same over the md arrays and ZFS pools.</item>
/// <item><c>disk_health_unknown</c> — 1 while the host reads Unknown after it read Healthy or DegradedOrFailing (an
/// Unknown that follows no such reading never raises it); 0 once it reads Healthy or DegradedOrFailing again.</item>
/// </list>
/// Raised after the state held for 10 minutes (two cycles), cleared after a good reading held for 10 minutes. Nothing is
/// fed while a group is neither clearly bad nor clearly good — an Unknown or Unsupported reading never counts as good, so
/// it never clears an open alert: the last bad state stays until a fresh, good reading. Unsupported never alerts. The
/// tracker bridges any pause between readings (no gap tolerance), for the same reason: a pause is no good reading.
/// Not thread-safe across servers by itself; the monitor calls it from its one loop.
/// </summary>
public sealed class DiskHealthAlerts
{
    /// <summary>How long a state must hold before it is raised, and how long a good reading before it is cleared.</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(10);

    /// <summary>1/0 against threshold 1: 0 is below the clear bound (1 − 0.5); no escalation, no gap reset, no cool-down.</summary>
    public static readonly BreachRules Rules = new(HoldFor, ClearMargin: 0.5, ClearedFor: HoldFor,
        EscalationStep: double.PositiveInfinity, GapTolerance: null, MinimumGap: TimeSpan.Zero);

    private const double Threshold = 1;

    private readonly BreachTracker _tracker = new(new HostLoadThresholds());
    private readonly HashSet<string> _unknownArmed = new(StringComparer.Ordinal);

    /// <summary>Judges one reading of <paramref name="serverId"/> against the state before it
    /// (<paramref name="previousOverall"/>, null for none). Returns the findings to deliver (raises and clears).</summary>
    public IReadOnlyList<HostLoadFinding> Evaluate(string serverId, string serverName, DiskHealthReading current,
        DiskHealthState? previousOverall, DateTime atUtc)
    {
        var findings = new List<HostLoadFinding>();
        void Add(HostLoadFinding? f) { if (f is not null) findings.Add(f); }

        var smart = current.Items.Where(i => i.Kind == DiskHealthKinds.Smart).ToList();
        var arrays = current.Items.Where(i => i.Kind is DiskHealthKinds.Md or DiskHealthKinds.Zfs).ToList();

        if (Signal(smart) is { } failing)
            Add(_tracker.Consider(atUtc, serverId, serverName, NotificationEventTypes.DiskFailing, failing, Threshold,
                (_, open) => $"{serverName}: SMART reports a failing disk for {BreachTracker.Describe(open)} — {Bad(smart)}.",
                _ => $"{serverName}: every disk reports SMART healthy again.", Rules));

        if (Signal(arrays) is { } degraded)
            Add(_tracker.Consider(atUtc, serverId, serverName, NotificationEventTypes.ArrayDegraded, degraded, Threshold,
                (_, open) => $"{serverName}: a disk array is degraded for {BreachTracker.Describe(open)} — {Bad(arrays)}.",
                _ => $"{serverName}: every md array and ZFS pool reads healthy again.", Rules));

        double? unknown = null;
        switch (current.Overall)
        {
            case DiskHealthState.Unknown:
                // Only a transition out of Healthy or DegradedOrFailing arms it; it stays armed while the host stays Unknown.
                if (previousOverall is DiskHealthState.Healthy or DiskHealthState.DegradedOrFailing) _unknownArmed.Add(serverId);
                if (_unknownArmed.Contains(serverId)) unknown = 1;
                break;
            case DiskHealthState.Healthy or DiskHealthState.DegradedOrFailing:
                _unknownArmed.Remove(serverId);
                unknown = 0;
                break;
            // Unsupported: nothing — it never alerts and is no good reading either.
        }
        if (unknown is { } u)
            Add(_tracker.Consider(atUtc, serverId, serverName, NotificationEventTypes.DiskHealthUnknown, u, Threshold,
                (_, open) => $"{serverName}: disk health cannot be read for {BreachTracker.Describe(open)} — {Unknowns(current)}.",
                _ => $"{serverName}: disk health can be read again.", Rules));

        return findings;
    }

    /// <summary>1 when an item reads bad, 0 when every item reads good, null (nothing fed) otherwise — also for no items.</summary>
    private static double? Signal(IReadOnlyList<DiskHealthItem> items)
    {
        if (items.Any(i => i.State == DiskHealthState.DegradedOrFailing)) return 1;
        if (items.Count > 0 && items.All(i => i.State == DiskHealthState.Healthy)) return 0;
        return null;
    }

    private static string Bad(IEnumerable<DiskHealthItem> items) =>
        Join(items.Where(i => i.State == DiskHealthState.DegradedOrFailing));

    private static string Unknowns(DiskHealthReading reading) =>
        reading.Items.Count == 0 ? "no readings" : Join(reading.Items.Where(i => i.State == DiskHealthState.Unknown));

    private static string Join(IEnumerable<DiskHealthItem> items)
    {
        var list = items.ToList();
        var shown = string.Join("; ", list.Take(5).Select(i => $"{i.Name}: {i.Reason}"));
        return list.Count > 5 ? $"{shown}; and {list.Count - 5} more" : shown;
    }

    /// <summary>Closes every open alert of <paramref name="serverId"/>, unconfirmed, naming <paramref name="reason"/>, and
    /// forgets its state (a server no longer read, the feature switched off).</summary>
    public IReadOnlyList<HostLoadFinding> Close(string serverId, DateTime atUtc, string reason)
    {
        _unknownArmed.Remove(serverId);
        return _tracker.Close(serverId, atUtc, reason);
    }

    /// <summary>The servers the alerts hold state for.</summary>
    public IReadOnlyCollection<string> Tracked() => _tracker.Tracked().Concat(_unknownArmed).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The raised alerts not yet closed.</summary>
    public IReadOnlyList<OpenFinding> OpenFindings() => _tracker.OpenFindings();

    /// <summary>What must survive a restart.</summary>
    public (IReadOnlyList<BreachState> Breaches, IReadOnlyList<string> UnknownArmed) Snapshot() =>
        (_tracker.Snapshot(), _unknownArmed.Order(StringComparer.Ordinal).ToList());

    /// <summary>Takes over <see cref="Snapshot"/> from before a restart.</summary>
    public void Restore(IEnumerable<BreachState> breaches, IEnumerable<string> unknownArmed, DateTime atUtc)
    {
        _tracker.Restore(breaches.Where(b => b.Kind is NotificationEventTypes.DiskFailing or NotificationEventTypes.ArrayDegraded
            or NotificationEventTypes.DiskHealthUnknown), atUtc);
        foreach (var id in unknownArmed.Where(id => !string.IsNullOrEmpty(id))) _unknownArmed.Add(id);
    }
}
