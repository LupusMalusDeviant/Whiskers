namespace Whiskers.Configuration;

/// <summary>
/// How many Docker calls Whiskers may have in flight against one server at a time (Plan-0001 WP3.4).
///
/// <para>The defaults are sized for the smallest host in a typical fleet — the two-core machine the
/// 2026-08-26 incident happened on — not for a development box. Raising them is a deliberate act with a
/// visible cost on the monitored server, which is why they live in configuration and not in code.</para>
/// </summary>
public class ServerBudgetSettings
{
    public const string SectionName = "ServerBudget";

    /// <summary>
    /// Concurrent calls the background loops may share per server.
    ///
    /// <para>Raised from four to eight on 2026-08-27. Four was chosen when only two loops actually used this
    /// lane — the other ten had never entered it and were queueing in the interactive lane instead, so the
    /// fleet in practice had eight slots split across two queues. Putting all twelve loops where they belong
    /// halved the background capacity in one step, and the first symptom was every server's circuit opening
    /// within four seconds of startup. Eight restores what was really there, without taking the interactive
    /// lane back from the people waiting on it.</para>
    ///
    /// <para>It is still a cap, and that is the point: the 2026-08-26 incident ran on ten concurrent full-log
    /// scans against a two-core host.</para>
    /// </summary>
    public int BackgroundConcurrency { get; set; } = 8;

    /// <summary>A separate lane for anything a human is waiting for. Kept apart on purpose: sharing one
    /// queue means a long background scan freezes the UI, which then looks like the server is down.</summary>
    public int InteractiveConcurrency { get; set; } = 4;

    /// <summary>Consecutive transport failures before Whiskers stops calling a server (Plan-0001 WP4).
    /// Five is deliberately not one: a single dropped tunnel is normal and self-heals on the retry.</summary>
    public int CircuitFailureThreshold { get; set; } = 5;

    /// <summary>How long an open circuit stays closed to traffic before one probe is let through. Short
    /// enough that a recovered host comes back within a cycle or two, long enough that a dead one is not
    /// hammered.</summary>
    public int CircuitCooldownSeconds { get; set; } = 60;

    /// <summary>Per-server overrides, keyed by server id — a beefy host can take more, a Raspberry Pi less.
    /// Format: <c>ServerBudget:PerServer:web-01:BackgroundConcurrency</c>.</summary>
    public Dictionary<string, ServerBudgetOverride> PerServer { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Effective limits for one server. Values below 1 are lifted to 1 rather than rejected: a
    /// mis-typed 0 must not silently deadlock every call to that host — it degrades to "one at a time".</summary>
    public (int Background, int Interactive) LimitsFor(string serverId)
    {
        var background = BackgroundConcurrency;
        var interactive = InteractiveConcurrency;

        if (PerServer.TryGetValue(serverId, out var o))
        {
            background = o.BackgroundConcurrency ?? background;
            interactive = o.InteractiveConcurrency ?? interactive;
        }

        return (Math.Max(1, background), Math.Max(1, interactive));
    }
}

public class ServerBudgetOverride
{
    public int? BackgroundConcurrency { get; set; }
    public int? InteractiveConcurrency { get; set; }
}
