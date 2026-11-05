using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Notifications;

namespace Whiskers.Services.Metrics.DiskHealth;

/// <summary>R1-58: an item of a server whose reading was lost — it read Healthy or DegradedOrFailing and is Unknown now.</summary>
public sealed record DiskHealthLostItem(string ServerId, string Kind, string Name);

/// <summary>
/// R1-58 (contract 0005): the three disk-health alerts on the one duration model (<see cref="BreachTracker"/>, R1-56),
/// fed as 1/0 against threshold 1 per server:
/// <list type="bullet">
/// <item><c>disk_failing</c> — 1 while a SMART item is DegradedOrFailing; 0 only while every SMART item is Healthy.</item>
/// <item><c>array_degraded</c> — the same over the md arrays and ZFS pools.</item>
/// <item><c>disk_health_unknown</c> — 1 while an item that read Healthy or DegradedOrFailing reads Unknown (review round
/// 1, M2: armed per item, so a host already Unknown for a harmless reason — a disk without SMART — still alerts when its
/// good items are lost; an item that was never read never arms it); 0 once none is lost.</item>
/// </list>
/// Raised after the state held for 10 minutes (two cycles), cleared after a good reading held for 10 minutes. Nothing is
/// fed while a group is neither clearly bad nor clearly good — an Unknown or Unsupported reading never counts as good, so
/// it never clears an open alert: the last bad state stays until a fresh, good reading. Such a reading also restarts a
/// confirmation in progress (review round 1, m1, <see cref="BreachTracker.Hold"/>): 10 minutes of good (or bad) readings
/// means 10 minutes without a reading in between that could not be judged. Unsupported never alerts. No gap tolerance: a
/// pause is no good reading. Not thread-safe across servers by itself; the monitor calls it from its one loop.
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
    private readonly Dictionary<string, HashSet<string>> _lost = new(StringComparer.Ordinal);

    private static string Key(DiskHealthItem item) => item.Kind + "\n" + item.Name;

    /// <summary>Judges one reading of <paramref name="serverId"/> against the one before it (<paramref name="previous"/>,
    /// null for none). Returns the findings to deliver (raises and clears).</summary>
    public IReadOnlyList<HostLoadFinding> Evaluate(string serverId, string serverName, DiskHealthReading current,
        DiskHealthReading? previous, DateTime atUtc)
    {
        var findings = new List<HostLoadFinding>();
        void Feed(string kind, double? signal, Func<double, TimeSpan, string> breach, Func<double, string> recovery)
        {
            if (signal is { } value)
            {
                if (_tracker.Consider(atUtc, serverId, serverName, kind, value, Threshold, breach, recovery, Rules) is { } f)
                    findings.Add(f);
            }
            else _tracker.Hold(serverId, kind);
        }

        var smart = current.Items.Where(i => i.Kind == DiskHealthKinds.Smart).ToList();
        var arrays = current.Items.Where(i => i.Kind is DiskHealthKinds.Md or DiskHealthKinds.Zfs).ToList();

        Feed(NotificationEventTypes.DiskFailing, Signal(smart),
            (_, open) => $"{serverName}: SMART reports a failing disk for {BreachTracker.Describe(open)} — {Bad(smart)}.",
            _ => $"{serverName}: every disk reports SMART healthy again.");

        Feed(NotificationEventTypes.ArrayDegraded, Signal(arrays),
            (_, open) => $"{serverName}: a disk array is degraded for {BreachTracker.Describe(open)} — {Bad(arrays)}.",
            _ => $"{serverName}: every md array and ZFS pool reads healthy again.");

        // An item is lost while it reads Unknown after it read Healthy or DegradedOrFailing (it stays lost while Unknown).
        var before = previous?.Items.ToDictionary(Key, i => i.State, StringComparer.Ordinal) ?? [];
        var wasLost = _lost.TryGetValue(serverId, out var known) ? known : [];
        var lost = current.Items
            .Where(i => i.State == DiskHealthState.Unknown)
            .Where(i => wasLost.Contains(Key(i))
                        || before.GetValueOrDefault(Key(i), DiskHealthState.Unsupported) is DiskHealthState.Healthy or DiskHealthState.DegradedOrFailing)
            .ToList();
        if (lost.Count > 0) _lost[serverId] = lost.Select(Key).ToHashSet(StringComparer.Ordinal);
        else _lost.Remove(serverId);

        // Unsupported: nothing — it never alerts and is no good reading either.
        double? unknown = lost.Count > 0 ? 1 : current.HadReading ? 0 : null;
        Feed(NotificationEventTypes.DiskHealthUnknown, unknown,
            (_, open) => $"{serverName}: disk health cannot be read for {BreachTracker.Describe(open)} — {Join(lost)}.",
            _ => $"{serverName}: disk health can be read again.");

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
        _lost.Remove(serverId);
        return _tracker.Close(serverId, atUtc, reason);
    }

    /// <summary>The servers the alerts hold state for.</summary>
    public IReadOnlyCollection<string> Tracked() => _tracker.Tracked().Concat(_lost.Keys).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The raised alerts not yet closed.</summary>
    public IReadOnlyList<OpenFinding> OpenFindings() => _tracker.OpenFindings();

    /// <summary>What must survive a restart.</summary>
    public (IReadOnlyList<BreachState> Breaches, IReadOnlyList<DiskHealthLostItem> Lost) Snapshot() =>
        (_tracker.Snapshot(),
            _lost.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .SelectMany(kv => kv.Value.Order(StringComparer.Ordinal).Select(k =>
                {
                    var parts = k.Split('\n', 2);
                    return new DiskHealthLostItem(kv.Key, parts[0], parts.Length > 1 ? parts[1] : "");
                }))
                .ToList());

    /// <summary>Takes over <see cref="Snapshot"/> from before a restart.</summary>
    public void Restore(IEnumerable<BreachState> breaches, IEnumerable<DiskHealthLostItem> lost, DateTime atUtc)
    {
        _tracker.Restore(breaches.Where(b => b.Kind is NotificationEventTypes.DiskFailing or NotificationEventTypes.ArrayDegraded
            or NotificationEventTypes.DiskHealthUnknown), atUtc);
        foreach (var item in lost.Where(l => !string.IsNullOrEmpty(l.ServerId)))
        {
            if (!_lost.TryGetValue(item.ServerId, out var set)) _lost[item.ServerId] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(item.Kind + "\n" + item.Name);
        }
    }
}
