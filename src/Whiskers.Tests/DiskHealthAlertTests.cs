using Whiskers.Models;
using Whiskers.Services.Metrics.DiskHealth;
using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Notifications;
using static Whiskers.Tests.DiskHealthFixtures;

namespace Whiskers.Tests;

/// <summary>
/// R1-58 (contract 0005): a failing disk, a degraded array and a reading that turns Unknown raise alerts through the R1-56
/// pipeline after 10 minutes and clear after 10 minutes of good readings. Unknown alerts only on a transition out of
/// Healthy or DegradedOrFailing; Unsupported never alerts; neither clears an open alert. All three kinds are recoverable
/// and never held by quiet hours.
/// </summary>
public sealed class DiskHealthAlertTests
{
    private static readonly TimeSpan Cycle = DiskHealthMonitor.Interval;

    /// <summary>Feeds the readings five minutes apart, each parsed against the one before, and collects the findings.</summary>
    private static List<HostLoadFinding> Drive(DiskHealthAlerts alerts, IEnumerable<DiskHealthQueries> cycles,
        DiskHealthReading? previous = null, DateTime? start = null)
    {
        var at = start ?? Now;
        var findings = new List<HostLoadFinding>();
        foreach (var queries in cycles)
        {
            var reading = DiskHealthParser.Parse("badwolf", queries, at, previous, DiskHealthParser.DefaultFreshness);
            findings.AddRange(alerts.Evaluate("badwolf", "Badwolf", reading, previous, at));
            previous = reading;
            at += Cycle;
        }
        return findings;
    }

    private static IEnumerable<DiskHealthQueries> Repeat(DiskHealthQueries q, int times) => Enumerable.Repeat(q, times);

    /// <summary>Good readings whose SMART evidence moves with the clock, so they stay fresh over a long drive.</summary>
    private static DiskHealthQueries FreshAt(DateTime at, string smart = SmartHealthy, string md = MdHealthy, string? zfs = null)
    {
        var shift = (long)(at - Now).TotalSeconds;
        string Shift(string json, params string[] values) =>
            values.Aggregate(json, (j, v) => j.Replace($"\"{v}\"", $"\"{long.Parse(v) + shift}\""));
        return Queries(smart: smart, run: Shift(SmartRunFresh, "1793879100", "1793879101"),
            mtime: MtimeFresh.Replace("\"1793879105.25\"", $"\"{1793879105 + shift}\""), md: md, zfs: zfs);
    }

    private static List<HostLoadFinding> DriveFresh(DiskHealthAlerts alerts, IEnumerable<Func<DateTime, DiskHealthQueries>> cycles,
        DiskHealthReading? previous = null)
    {
        var at = Now;
        var findings = new List<HostLoadFinding>();
        foreach (var make in cycles)
        {
            var reading = DiskHealthParser.Parse("badwolf", make(at), at, previous, DiskHealthParser.DefaultFreshness);
            findings.AddRange(alerts.Evaluate("badwolf", "Badwolf", reading, previous, at));
            previous = reading;
            at += Cycle;
        }
        return findings;
    }

    // ---------------------------------------------------------------------------------------------- raise and clear

    [Fact]
    public void A_failing_disk_raises_disk_failing_after_ten_minutes_and_once()
    {
        var alerts = new DiskHealthAlerts();

        // Readings at 0, 5, 10, 15 minutes: the state held for 10 minutes at the third, and is not raised again.
        var findings = Drive(alerts, Repeat(Queries(smart: SmartFailed), 4));

        var raised = Assert.Single(findings);
        Assert.Equal(Now.AddMinutes(10), raised.AtUtc);
        Assert.Equal(NotificationEventTypes.DiskFailing, raised.EventType);
        Assert.Equal(FindingKind.Raised, raised.What);
        Assert.Contains("/dev/sda: SMART overall-health failed", raised.Summary);
        Assert.Equal(TimeSpan.FromMinutes(10), raised.OpenFor);
    }

    [Fact]
    public void A_failing_disk_clears_only_after_ten_minutes_of_fresh_good_readings()
    {
        var alerts = new DiskHealthAlerts();
        var cycles = Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(at => FreshAt(at, smart: SmartFailed), 3)
            .Concat(Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(at => FreshAt(at), 3));

        var findings = DriveFresh(alerts, cycles);

        Assert.Equal(2, findings.Count);
        Assert.Equal(NotificationEventTypes.DiskFailing, findings[0].EventType);
        var cleared = findings[1];
        Assert.Equal("disk_failing_recovered", cleared.EventType);
        Assert.False(cleared.Unconfirmed);
        Assert.Empty(alerts.OpenFindings());
    }

    [Theory]
    [InlineData("md-failed")]
    [InlineData("md-fewer")]
    [InlineData("zfs-degraded")]
    [InlineData("zfs-faulted")]
    public void A_degraded_array_raises_array_degraded(string which)
    {
        var alerts = new DiskHealthAlerts();
        string md = MdHealthy;
        string? zfs = null;
        switch (which)
        {
            case "md-failed": md = MdFailedDisk; break;
            case "md-fewer": md = MdFewerActive; break;
            case "zfs-degraded": zfs = Zpool("degraded"); break;
            case "zfs-faulted": zfs = Zpool("faulted"); break;
        }

        var findings = DriveFresh(alerts, Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(at => FreshAt(at, md: md, zfs: zfs), 3));

        var raised = Assert.Single(findings);
        Assert.Equal(NotificationEventTypes.ArrayDegraded, raised.EventType);
        Assert.Equal(FindingKind.Raised, raised.What);
    }

    [Fact]
    public void A_state_that_does_not_hold_for_ten_minutes_raises_nothing()
    {
        var alerts = new DiskHealthAlerts();
        var cycles = new Func<DateTime, DiskHealthQueries>[]
        {
            at => FreshAt(at, smart: SmartFailed), at => FreshAt(at, smart: SmartFailed), at => FreshAt(at), at => FreshAt(at),
        };

        Assert.Empty(DriveFresh(alerts, cycles));
    }

    // ---------------------------------------------------------------------------------------------- unknown

    [Fact]
    public void A_reading_that_turns_Unknown_after_Healthy_raises_disk_health_unknown_and_clears_when_readable()
    {
        var alerts = new DiskHealthAlerts();
        var cycles = new Func<DateTime, DiskHealthQueries>[]
        {
            at => FreshAt(at),                                       // Healthy
            _ => Queries(run: SmartRunExpired),                      // Unknown from here (the timer stopped) …
            _ => Queries(run: SmartRunExpired),
            _ => Queries(run: SmartRunExpired),                      // … 10 minutes: raised
            at => FreshAt(at), at => FreshAt(at), at => FreshAt(at), // readable again for 10 minutes: cleared
        };

        var findings = DriveFresh(alerts, cycles);

        Assert.Equal(2, findings.Count);
        Assert.Equal(NotificationEventTypes.DiskHealthUnknown, findings[0].EventType);
        Assert.Contains("smartctl last ran", findings[0].Summary);
        Assert.Equal("disk_health_unknown_recovered", findings[1].EventType);
    }

    [Fact]
    public void A_reading_that_turns_Unknown_after_DegradedOrFailing_raises_disk_health_unknown_and_keeps_disk_failing_open()
    {
        var alerts = new DiskHealthAlerts();
        var unreadable = DiskHealthQueries.AllFailed("the metrics endpoint could not be reached (ConnectionError)");
        var cycles = Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(at => FreshAt(at, smart: SmartFailed), 3)
            .Concat(Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(_ => unreadable, 12));      // an hour unreadable

        var findings = DriveFresh(alerts, cycles);

        Assert.Equal([NotificationEventTypes.DiskFailing, NotificationEventTypes.DiskHealthUnknown], findings.Select(f => f.EventType));
        // Unknown never clears what was failing: still open after an hour without a reading.
        Assert.Contains(alerts.OpenFindings(), f => f.Kind == NotificationEventTypes.DiskFailing);
    }

    [Fact]
    public void Unknown_alerts_only_on_a_transition_out_of_Healthy_or_DegradedOrFailing()
    {
        // A host whose first reading is already Unknown (the timer never ran fresh) never raises disk_health_unknown.
        var alerts = new DiskHealthAlerts();
        Assert.Empty(Drive(alerts, Repeat(Queries(run: SmartRunExpired), 24)));
        Assert.Empty(alerts.OpenFindings());

        // Nor does one that was Unsupported before.
        var after = new DiskHealthAlerts();
        var unsupported = Read(DiskHealthQueries.WithoutEndpoint());
        Assert.Empty(Drive(after, Repeat(Queries(run: SmartRunExpired), 24), previous: unsupported));
    }

    [Fact]
    public void Unsupported_never_alerts()
    {
        var alerts = new DiskHealthAlerts();

        Assert.Empty(Drive(alerts, Repeat(DiskHealthQueries.WithoutEndpoint(), 50)));
        Assert.Empty(Drive(alerts, Repeat(Queries(smart: Empty, run: Empty, mtime: Empty, md: Empty, zfs: Empty), 50)));
        Assert.Empty(alerts.OpenFindings());
        Assert.Empty(alerts.Tracked());
    }

    [Fact]
    public void Unsupported_does_not_clear_an_open_alert_either()
    {
        var alerts = new DiskHealthAlerts();
        DriveFresh(alerts, Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(at => FreshAt(at, smart: SmartFailed), 3));
        Assert.Single(alerts.OpenFindings());

        // After a restart without saved readings the host may read Unsupported: no reading, so no clear.
        var unsupported = Read(DiskHealthQueries.WithoutEndpoint());
        for (var i = 0; i < 12; i++)
            Assert.Empty(alerts.Evaluate("badwolf", "Badwolf", unsupported, null, Now.AddMinutes(15 + 5 * i)));
        Assert.Single(alerts.OpenFindings());
    }

    // ---------------------------------------------------------------------------------------------- close and restart

    [Fact]
    public void Close_ends_every_open_alert_unconfirmed()
    {
        var alerts = new DiskHealthAlerts();
        DriveFresh(alerts, Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(at => FreshAt(at, smart: SmartFailed, md: MdFailedDisk), 3));

        var closed = alerts.Close("badwolf", Now.AddHours(1), "disk-health readings were switched off");

        Assert.Equal(2, closed.Count);
        Assert.All(closed, c => Assert.True(c.Unconfirmed));
        Assert.All(closed, c => Assert.Contains("switched off", c.Summary));
        Assert.Empty(alerts.OpenFindings());
    }

    [Fact]
    public void An_open_alert_survives_a_restart_and_is_not_raised_twice()
    {
        var before = new DiskHealthAlerts();
        DriveFresh(before, Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(at => FreshAt(at, smart: SmartFailed), 3));
        var (breaches, armed) = before.Snapshot();

        var after = new DiskHealthAlerts();
        after.Restore(breaches, armed, Now.AddMinutes(20));
        var again = Drive(after, Repeat(Queries(smart: SmartFailed), 5), start: Now.AddMinutes(20));

        Assert.Empty(again);
        Assert.Single(after.OpenFindings());
    }

    // ---------------------------------------------------------------------------------------------- the registry

    [Fact]
    public void The_three_kinds_are_recoverable_and_never_held_by_quiet_hours_nor_their_ends()
    {
        foreach (var kind in new[] { NotificationEventTypes.DiskFailing, NotificationEventTypes.ArrayDegraded, NotificationEventTypes.DiskHealthUnknown })
        {
            Assert.Contains(kind, NotificationEventTypes.RecoverableKinds);
            Assert.Contains(kind, NotificationEventTypes.UnsuppressibleEvents);
            Assert.False(NotificationEventTypes.IsSuppressible(new NotificationEvent { EventType = kind }));
            Assert.False(NotificationEventTypes.IsSuppressible(new NotificationEvent { EventType = NotificationEventTypes.Recovered(kind) }));
            Assert.True(NotificationEventTypes.IsStateTransition(kind));
            Assert.NotEqual(kind, NotificationFormatter.Describe(new NotificationEvent { EventType = kind }).Title);
        }
        // Other recoveries keep their classification.
        Assert.True(NotificationEventTypes.IsSuppressible(new NotificationEvent { EventType = "high_disk_recovered" }));
    }

    [Fact]
    public void A_host_already_Unknown_for_a_harmless_reason_still_alerts_when_its_good_items_turn_Unknown()
    {
        // sda has no usable SMART (Unknown from the first reading); nvme0, md0 and tank read healthy.
        var unavailable = SmartHealthy.Replace(
            "\"smartmon_device_smart_available\",\"disk\":\"/dev/sda\",\"instance\":\"100.64.0.7:9100\",\"job\":\"node\",\"server\":\"badwolf\",\"type\":\"sat\"},\"value\":[1793880000.123,\"1\"]",
            "\"smartmon_device_smart_available\",\"disk\":\"/dev/sda\",\"instance\":\"100.64.0.7:9100\",\"job\":\"node\",\"server\":\"badwolf\",\"type\":\"sat\"},\"value\":[1793880000.123,\"0\"]");
        var alerts = new DiskHealthAlerts();
        var cycles = new[] { Queries(smart: unavailable) }
            .Concat(Repeat(DiskHealthQueries.AllFailed("the metrics endpoint could not be reached (ConnectionError)"), 3));

        var findings = Drive(alerts, cycles);

        var raised = Assert.Single(findings, f => f.EventType == NotificationEventTypes.DiskHealthUnknown);
        Assert.Equal(Now.AddMinutes(15), raised.AtUtc);      // all lost from minute 5, held 10 minutes
    }

    /// <summary>Review round 2 (N1): /dev/sda reported twice with different <c>type</c> labels (a second textfile, a
    /// smartmon variant) — and a failing nvme0 next to it. Two items of one name must not stop the alerts for the host.</summary>
    internal static string SmartFailingNvmeWithDuplicateSda => SmartHealthy
        .Replace(
            "\"smartmon_device_smart_healthy\",\"disk\":\"/dev/nvme0\",\"instance\":\"100.64.0.7:9100\",\"job\":\"node\",\"server\":\"badwolf\",\"type\":\"nvme\"},\"value\":[1793880000.123,\"1\"]",
            "\"smartmon_device_smart_healthy\",\"disk\":\"/dev/nvme0\",\"instance\":\"100.64.0.7:9100\",\"job\":\"node\",\"server\":\"badwolf\",\"type\":\"nvme\"},\"value\":[1793880000.123,\"0\"]")
        .Replace("\"result\":[", """
            "result":[
              {"metric":{"__name__":"smartmon_device_smart_available","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"scsi"},"value":[1793880000.123,"1"]},
              {"metric":{"__name__":"smartmon_device_smart_enabled","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"scsi"},"value":[1793880000.123,"1"]},
              {"metric":{"__name__":"smartmon_device_smart_healthy","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"scsi"},"value":[1793880000.123,"1"]},
            """);

    [Fact]
    public void Two_entries_of_one_disk_with_different_types_do_not_stop_disk_failing_for_another_disk()
    {
        var alerts = new DiskHealthAlerts();

        var findings = DriveFresh(alerts,
            Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(at => FreshAt(at, smart: SmartFailingNvmeWithDuplicateSda), 4));

        var raised = Assert.Single(findings, f => f.EventType == NotificationEventTypes.DiskFailing);
        Assert.Equal(Now.AddMinutes(10), raised.AtUtc);
        Assert.Contains("/dev/nvme0", raised.Summary);
    }

    [Fact]
    public void A_saved_reading_with_two_items_of_one_name_is_evaluated_without_throwing()
    {
        // A disk-health.json written before review round 2 can hold two items with the same kind and name.
        var duplicate = new DiskHealthReading("badwolf", DiskHealthState.Healthy,
        [
            new DiskHealthItem(DiskHealthKinds.Smart, "/dev/sda", DiskHealthState.Healthy, "SMART overall-health passed", Now),
            new DiskHealthItem(DiskHealthKinds.Smart, "/dev/sda", DiskHealthState.Unknown, "no smartctl run time", Now),
        ], Now.AddMinutes(-5));
        var alerts = new DiskHealthAlerts();

        var findings = Drive(alerts, Repeat(DiskHealthQueries.AllFailed("the read failed (HttpRequestException)"), 3), previous: duplicate);

        // sda read Healthy before and is lost now: disk_health_unknown after 10 minutes.
        var raised = Assert.Single(findings, f => f.EventType == NotificationEventTypes.DiskHealthUnknown);
        Assert.Equal(Now.AddMinutes(10), raised.AtUtc);
    }

    [Fact]
    public void A_good_reading_Unknown_for_hours_and_one_good_reading_does_not_clear_at_once()
    {
        var alerts = new DiskHealthAlerts();
        var cycles = Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(at => FreshAt(at, smart: SmartFailed), 3)   // raised at 10 min
            .Append(at => FreshAt(at))                                                                                 // one good reading
            .Concat(Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(_ => Queries(run: SmartRunExpired), 24))     // 2 h Unknown
            .Append(at => FreshAt(at));                                                                                // one good reading

        var findings = DriveFresh(alerts, cycles);

        Assert.DoesNotContain(findings, f => f.EventType == "disk_failing_recovered");
        Assert.Contains(alerts.OpenFindings(), f => f.Kind == NotificationEventTypes.DiskFailing);

        // Ten more minutes of good readings do clear it.
        var more = new List<HostLoadFinding>();
        var at = Now + Cycle * 29;
        var previous = DiskHealthParser.Parse("badwolf", FreshAt(at - Cycle), at - Cycle, null, DiskHealthParser.DefaultFreshness);
        for (var i = 0; i < 2; i++, at += Cycle)
        {
            var reading = DiskHealthParser.Parse("badwolf", FreshAt(at), at, previous, DiskHealthParser.DefaultFreshness);
            more.AddRange(alerts.Evaluate("badwolf", "Badwolf", reading, previous, at));
            previous = reading;
        }
        Assert.Contains(more, f => f.EventType == "disk_failing_recovered");
    }

    [Fact]
    public void A_bad_reading_Unknown_for_hours_and_one_bad_reading_does_not_raise_at_once()
    {
        var alerts = new DiskHealthAlerts();
        var cycles = new Func<DateTime, DiskHealthQueries>[] { at => FreshAt(at, smart: SmartFailed) }
            .Concat(Enumerable.Repeat<Func<DateTime, DiskHealthQueries>>(_ => Queries(smart: SmartFailed.Replace(
                "\"smartmon_device_smart_enabled\",\"disk\":\"/dev/sda\",\"instance\":\"100.64.0.7:9100\",\"job\":\"node\",\"server\":\"badwolf\",\"type\":\"sat\"},\"value\":[1793880000.123,\"1\"]",
                "\"smartmon_device_smart_enabled\",\"disk\":\"/dev/sda\",\"instance\":\"100.64.0.7:9100\",\"job\":\"node\",\"server\":\"badwolf\",\"type\":\"sat\"},\"value\":[1793880000.123,\"0\"]")), 24))
            .Append(at => FreshAt(at, smart: SmartFailed));

        Assert.DoesNotContain(DriveFresh(alerts, cycles), f => f.EventType == NotificationEventTypes.DiskFailing);
    }

    [Fact]
    public void Hold_restarts_a_confirmation_in_progress_and_keeps_a_raised_breach_open()
    {
        var tracker = new BreachTracker(new HostLoadThresholds());
        BreachRules rules = DiskHealthAlerts.Rules;
        HostLoadFinding? Feed(int minute, double v) => tracker.Consider(Now.AddMinutes(minute), "s", "S",
            NotificationEventTypes.DiskFailing, v, 1, (_, _) => "raised", _ => "cleared", rules);

        Assert.Null(Feed(0, 1));
        tracker.Hold("s", NotificationEventTypes.DiskFailing);
        Assert.Null(Feed(10, 1));                        // the confirmation started again at minute 10
        Assert.Equal(FindingKind.Raised, Feed(20, 1)!.What);

        Assert.Null(Feed(25, 0));
        tracker.Hold("s", NotificationEventTypes.DiskFailing);
        Assert.Null(Feed(40, 0));                        // the recovery started again at minute 40
        Assert.Single(tracker.OpenFindings());
        Assert.Equal(FindingKind.Cleared, Feed(50, 0)!.What);

        tracker.Hold("nobody", NotificationEventTypes.DiskFailing);   // nothing to hold: no effect
    }

    [Fact]
    public void The_durations_are_ten_minutes_to_open_and_to_clear()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), DiskHealthAlerts.Rules.SustainedFor);
        Assert.Equal(TimeSpan.FromMinutes(10), DiskHealthAlerts.Rules.ClearedFor);
        Assert.Null(DiskHealthAlerts.Rules.GapTolerance);     // a pause is no good reading: it never resets a breach
    }
}
