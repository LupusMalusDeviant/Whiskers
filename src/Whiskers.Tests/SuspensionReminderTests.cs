using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Observability;

namespace Whiskers.Tests;

/// <summary>
/// The reminder that a pause has outlived its reason (Plan-0005 WP0).
///
/// <para>These tests care about the reminder FIRING. A reminder that only proves it stays quiet would pass
/// just as happily if it never worked at all — and the failure it exists to catch is a server nobody has
/// looked at for a week, which is silent by definition.</para>
/// </summary>
public class SuspensionReminderTests
{
    private static readonly TimeSpan After = TimeSpan.FromHours(24);

    private static (SuspensionReminder Reminder, ILoopSuspensionService Suspension, FakeNotifications Sent) Build()
    {
        var servers = new FakeServerConfig(new ServerConfig { Id = "host-a", Name = "Host-A", IsDefault = true });
        var sent = new FakeNotifications();
        var suspension = new LoopSuspensionService(new FakeNotifications(), servers, NullLogger<LoopSuspensionService>.Instance, new NoOutcomes());
        var reminder = new SuspensionReminder(
            suspension, sent, servers, NullLogger<SuspensionReminder>.Instance, After);
        return (reminder, suspension, sent);
    }

    [Fact]
    public void A_pause_that_outlives_its_reason_gets_reported()
    {
        var (reminder, suspension, sent) = Build();
        suspension.Suspend("host-a", DateTime.UtcNow.AddMinutes(30), "quick look");

        Assert.Empty(reminder.Remind(DateTime.UtcNow));

        var reminded = reminder.Remind(DateTime.UtcNow + After + TimeSpan.FromMinutes(1));

        Assert.Equal(new[] { "host-a" }, reminded);
        var evt = Assert.Single(sent.Events);
        Assert.Equal("loops_paused_reminder", evt.EventType);
        Assert.Contains("Nothing there is being watched", evt.ImageInfo);
    }

    [Fact]
    public void An_open_ended_pause_is_reminded_about_too()
    {
        // The case the first draft got backwards: "until revoked" is stored as a deadline ten years out, so a
        // reminder measured against the END would have skipped exactly the pauses that can become permanent.
        var (reminder, suspension, sent) = Build();
        suspension.Suspend("host-a", until: null, reason: "investigating");

        var reminded = reminder.Remind(DateTime.UtcNow + After + TimeSpan.FromMinutes(1));

        Assert.Equal(new[] { "host-a" }, reminded);
        Assert.Single(sent.Events);
    }

    [Fact]
    public void It_keeps_asking_rather_than_saying_it_once()
    {
        // One message scrolls out of the channel. The blind spot does not.
        var (reminder, suspension, sent) = Build();
        suspension.Suspend("host-a", until: null, reason: "investigating");
        var start = DateTime.UtcNow;

        reminder.Remind(start + After + TimeSpan.FromMinutes(1));
        Assert.Empty(reminder.Remind(start + After + TimeSpan.FromHours(2)));   // not a fresh nag every hour
        Assert.Equal(new[] { "host-a" }, reminder.Remind(start + After + After + TimeSpan.FromMinutes(2)));

        Assert.Equal(2, sent.Events.Count);
    }

    [Fact]
    public void A_server_that_came_back_and_was_paused_again_is_not_muted_by_the_old_reminder()
    {
        // The bookkeeping risk points at silence, not at noise: if the service kept the old "already told you"
        // timestamp after the server came back, the NEXT pause would inherit it and its reminder would be
        // swallowed. A missed reminder is the failure this whole service exists to prevent.
        var (reminder, suspension, sent) = Build();
        var start = DateTime.UtcNow;

        suspension.Suspend("host-a", until: null, reason: "first");
        reminder.Remind(start + After + TimeSpan.FromMinutes(1));
        Assert.Single(sent.Events);

        suspension.Resume("host-a");
        reminder.Remind(start + After + TimeSpan.FromMinutes(2));   // the pass that forgets it
        suspension.Suspend("host-a", until: null, reason: "second");

        // Only a minute later on the reminder's clock — well inside the window that would suppress a repeat
        // nag, but the entry should be gone, so the new pause gets its own first reminder.
        Assert.Equal(new[] { "host-a" }, reminder.Remind(start + After + TimeSpan.FromMinutes(3)));
        Assert.Equal(2, sent.Events.Count);
    }
}
