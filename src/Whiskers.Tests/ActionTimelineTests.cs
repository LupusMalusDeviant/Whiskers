using Whiskers.Models;
using Whiskers.Services.Observability.SelfMetrics;

namespace Whiskers.Tests;

/// <summary>
/// The action timeline (Plan-0003 WP5).
///
/// <para>Self-inflicted load is the hardest kind to attribute: the metric curve shows the effect and nothing
/// shows the cause. These tests care most about the rule the plan singles out — <b>everything is computed in
/// UTC</b> — because a timeline with an offset in it is worse than no timeline. It will confidently suggest
/// that the thing which happened <em>after</em> the spike caused it, and someone will act on that.</para>
/// </summary>
public class ActionTimelineTests
{
    private static readonly DateTime Noon = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    private static AuditLogEntity Audit(
        DateTime at, string action = "container.restart", string? serverId = "host-a",
        string target = "app-web", bool success = true, long id = 1) => new()
    {
        Id = id,
        Timestamp = at,
        Action = action,
        TargetName = target,
        TargetType = "container",
        ServerId = serverId,
        Actor = "operator",
        ActorType = "web",
        Success = success
    };

    private static InAppNotification Event(DateTime at, string type, string title) =>
        new(Id: Guid.NewGuid().ToString("N"), Timestamp: at, EventType: type, Title: title,
            Detail: "", Severity: "Info");

    [Fact]
    public void An_action_lands_on_the_timeline_at_the_second_it_happened()
    {
        // The plan's acceptance criterion: a manually triggered restart appears at exactly the right place.
        var at = Noon.AddSeconds(-42);

        var entry = Assert.Single(ActionTimeline.Build(
            new[] { Audit(at) }, events: null, serverId: null, sinceUtc: Noon.AddHours(-1)));

        Assert.Equal(at, entry.AtUtc);
        Assert.Equal(DateTimeKind.Utc, entry.AtUtc.Kind);
        Assert.Contains("app-web", entry.Summary);
        Assert.Equal(1, entry.AuditId);
    }

    [Fact]
    public void A_timestamp_of_unknown_kind_is_read_as_UTC_and_not_as_local()
    {
        // THE test for this package. A DateTime read back from the database comes out Unspecified, and
        // Unspecified silently means "local" the moment anything converts it. On a host at UTC+2 that shifts
        // every stored entry by two hours — enough to reorder cause and effect, silently.
        var fromDatabase = DateTime.SpecifyKind(Noon.AddMinutes(-5), DateTimeKind.Unspecified);

        var entry = Assert.Single(ActionTimeline.Build(
            new[] { Audit(fromDatabase) }, events: null, serverId: null, sinceUtc: Noon.AddHours(-1)));

        Assert.Equal(Noon.AddMinutes(-5), entry.AtUtc);
        Assert.Equal(DateTimeKind.Utc, entry.AtUtc.Kind);
    }

    [Fact]
    public void A_local_timestamp_is_converted_rather_than_relabelled()
    {
        // The other half of the same rule: relabelling a local time as UTC keeps the wrong number. This one
        // only bites on a host whose clock is not already at UTC, which is why it is pinned rather than
        // assumed.
        var local = Noon.ToLocalTime();

        var entry = Assert.Single(ActionTimeline.Build(
            new[] { Audit(local) }, events: null, serverId: null, sinceUtc: Noon.AddHours(-2)));

        Assert.Equal(Noon, entry.AtUtc);
    }

    [Fact]
    public void The_newest_entry_comes_first()
    {
        // The question is nearly always about something that just happened.
        var entries = ActionTimeline.Build(new[]
        {
            Audit(Noon.AddMinutes(-30), target: "older", id: 1),
            Audit(Noon.AddMinutes(-2), target: "newer", id: 2)
        }, events: null, serverId: null, sinceUtc: Noon.AddHours(-1));

        Assert.Equal("newer", entries[0].Summary.Split(' ').Last());
    }

    [Fact]
    public void Whiskers_own_decisions_appear_alongside_human_actions()
    {
        // A circuit opening or a pause changes the curve without anybody touching the fleet. Left off the
        // timeline, that change has no visible cause at all — the hardest kind of mystery to close.
        var entries = ActionTimeline.Build(
            new[] { Audit(Noon.AddMinutes(-10)) },
            new[] { Event(Noon.AddMinutes(-5), "loops_paused", "Background checks paused for Host-A") },
            serverId: null, sinceUtc: Noon.AddHours(-1));

        Assert.Equal(2, entries.Count);
        Assert.Equal("Whiskers", entries[0].Actor);
        Assert.Null(entries[0].AuditId);   // not an audited action — nothing to open
    }

    [Fact]
    public void Routine_noise_is_left_out()
    {
        // A timeline that lists every read makes the one deploy impossible to find.
        var entries = ActionTimeline.Build(new[]
        {
            Audit(Noon.AddMinutes(-5), action: "container.list"),
            Audit(Noon.AddMinutes(-4), action: "auth.login")
        }, events: null, serverId: null, sinceUtc: Noon.AddHours(-1));

        Assert.Empty(entries);
    }

    [Fact]
    public void An_unfamiliar_action_is_kept_rather_than_dropped()
    {
        // The filter excludes READS rather than allow-listing writes, on purpose. An allow-list would
        // silently drop the next kind of intervention somebody adds, and a timeline missing the action that
        // caused the spike is worse than none — it looks complete. Worst case here is one line of noise.
        var entry = Assert.Single(ActionTimeline.Build(
            new[] { Audit(Noon.AddMinutes(-3), action: "container.quarantine") },
            events: null, serverId: null, sinceUtc: Noon.AddHours(-1)));

        Assert.Contains("quarantine", entry.Summary);
    }

    [Fact]
    public void Narrowing_to_one_server_drops_events_that_cannot_be_attributed_to_it()
    {
        // Stored notifications carry no server id. Showing them on a single server's timeline anyway would
        // attribute a pause on one host to a spike on another — precisely the false relationship this view
        // must never suggest.
        var entries = ActionTimeline.Build(
            new[] { Audit(Noon.AddMinutes(-10), serverId: "host-a"), Audit(Noon.AddMinutes(-9), serverId: "other", id: 2) },
            new[] { Event(Noon.AddMinutes(-5), "loops_paused", "Background checks paused") },
            serverId: "host-a", sinceUtc: Noon.AddHours(-1));

        var entry = Assert.Single(entries);
        Assert.Equal("host-a", entry.ServerId);
    }

    [Fact]
    public void Anything_older_than_the_window_is_left_out()
    {
        Assert.Empty(ActionTimeline.Build(
            new[] { Audit(Noon.AddHours(-5)) }, events: null, serverId: null, sinceUtc: Noon.AddHours(-1)));
    }

    [Fact]
    public void A_failed_action_stays_marked_as_failed()
    {
        // "The deploy at 14:02" and "the deploy that failed at 14:02" explain different curves.
        var entry = Assert.Single(ActionTimeline.Build(
            new[] { Audit(Noon.AddMinutes(-1), success: false) }, events: null, serverId: null,
            sinceUtc: Noon.AddHours(-1)));

        Assert.False(entry.Success);
    }
}
