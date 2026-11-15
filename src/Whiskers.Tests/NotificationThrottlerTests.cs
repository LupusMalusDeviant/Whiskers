using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

public class NotificationThrottlerTests
{
    [Fact]
    public void NotRecorded_IsNotThrottled()
    {
        var t = new NotificationThrottler();
        Assert.False(t.IsThrottled("c1", "stopped", 60));
    }

    [Fact]
    public void AfterRecord_ThrottledWithinWindow()
    {
        var t = new NotificationThrottler();
        t.Record("c1", "stopped", 60);
        Assert.True(t.IsThrottled("c1", "stopped", 60));
    }

    [Fact]
    public void WindowIsReadPerCall_NotFrozenAtConstruction()
    {
        // NIED-8: the ctor value (60) no longer drives IsThrottled. A just-recorded key throttles under a
        // 60-min window but NOT under a 0-min window passed at the call.
        var t = new NotificationThrottler();
        t.Record("c1", "stopped", 60);
        Assert.True(t.IsThrottled("c1", "stopped", 60));
        Assert.False(t.IsThrottled("c1", "stopped", 0));
    }

    [Fact]
    public void DifferentEventType_IsNotThrottled()
    {
        var t = new NotificationThrottler();
        t.Record("c1", "stopped", 60);
        Assert.False(t.IsThrottled("c1", "restart_loop", 60));
    }

    [Fact]
    public void The_same_event_on_two_servers_is_two_alerts()
    {
        var t = new NotificationThrottler();
        t.Record(new NotificationEvent { ServerId = "a", EventType = "log_rotation_missing" }, 60);
        Assert.True(t.IsThrottled(new NotificationEvent { ServerId = "a", EventType = "log_rotation_missing" }, 60));
        Assert.False(t.IsThrottled(new NotificationEvent { ServerId = "b", EventType = "log_rotation_missing" }, 60));
    }

    [Theory]
    [InlineData("server_unreachable")]
    [InlineData("server_recovered")]
    // R1-56: a breach raise and its recovery, a metrics gap and the quiet-hours summary are state transitions too.
    [InlineData("high_disk")]
    [InlineData("high_cpu_recovered")]
    [InlineData("metrics_gap")]
    [InlineData("metrics_gap_recovered")]
    [InlineData("host_cpu_high_recovered")]
    [InlineData("quiet_hours_summary")]
    public void Reachability_transitions_are_never_throttled(string eventType)
    {
        var t = new NotificationThrottler();
        var evt = new NotificationEvent { ServerId = "a", EventType = eventType };
        t.Record(evt, 60);
        Assert.False(t.IsThrottled(evt, 60));
    }

    // ---- Contract 0017 part G: distinct events of one type no longer share a throttle slot ---------------------------
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Posts);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }
    }

    private static DiscordNotificationService Discord(CountingHandler handler) => new(new HttpClient(handler),
        new StaticOptionsMonitor<Whiskers.Configuration.DiscordSettings>(new() { Enabled = true, WebhookUrl = "https://discord.example/api/webhooks/1/x" }),
        NullLogger<DiscordNotificationService>.Instance);

    [Fact]
    public async Task Two_different_approval_requests_both_reach_the_channel()
    {
        var handler = new CountingHandler();
        var discord = Discord(handler);
        // As ApprovalCoordinator.RaiseAsync builds them: no server, no container, one approval id each.
        NotificationEvent Approval(string id) => new()
            { EventType = NotificationEventTypes.AgentApproval, ApprovalId = id, CorrelationId = id, ImageInfo = "Approval " + id + " is waiting." };

        var first = await discord.DeliverAsync(Approval("a-1"), CancellationToken.None);
        var second = await discord.DeliverAsync(Approval("a-2"), CancellationToken.None);

        Assert.True(first && second && handler.Posts == 2,
            $"two different approvals a minute apart: delivered first={first}, second={second}, posts={handler.Posts} — the second " +
            "waits for a person who is never told (throttle key is server/container + type, both empty here)");
    }

    [Fact]
    public async Task Two_identical_container_events_in_the_window_are_still_throttled()
    {
        var handler = new CountingHandler();
        var discord = Discord(handler);
        // As CompositeNotificationService hands them on: each send gets its own DeliveryId, which must not split the slot.
        NotificationEvent Unhealthy() => new()
        {
            EventType = NotificationEventTypes.Unhealthy, ServerId = "srv", ContainerId = "c-web", ContainerName = "web",
            DeliveryId = Guid.NewGuid().ToString("N"),
        };

        Assert.True(await discord.DeliverAsync(Unhealthy(), CancellationToken.None));
        Assert.False(await discord.DeliverAsync(Unhealthy(), CancellationToken.None));
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public void Two_automation_refused_for_one_server_in_the_window_are_still_throttled()
    {
        // Unsuppressible (quiet hours never hold it) is not unthrottled: a refusing loop must not flood the channels.
        var t = new NotificationThrottler();
        NotificationEvent Refused() => new()
            { ServerId = "a", EventType = NotificationEventTypes.AutomationRefused, DeliveryId = Guid.NewGuid().ToString("N") };
        Assert.False(t.IsThrottled(Refused(), 60));
        t.Record(Refused(), 60);
        Assert.True(t.IsThrottled(Refused(), 60));
    }

    [Fact]
    public void An_event_carrying_an_approval_id_is_never_throttled()
    {
        var t = new NotificationThrottler();
        var evt = new NotificationEvent { EventType = NotificationEventTypes.AgentApproval, ApprovalId = "a-1" };
        t.Record(evt, 60);
        Assert.False(t.IsThrottled(evt, 60));
        t.Record(evt.Copy(), 60);
        Assert.False(t.IsThrottled(evt.Copy(), 60));
    }

    // ---- Review LOW: the map is pruned with the live window, not the one frozen at construction ----------------------
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Throttle_window_raised_at_runtime_is_honoured()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var throttler = new NotificationThrottler(clock);
        throttler.Record("srv/web", "unhealthy", minutes: 5);                  // sent under the old 5-minute window
        clock.Now = clock.Now.AddMinutes(20);                                  // 20 minutes later

        // The operator raised ThrottleMinutes to 60 since ("takes effect without recreating the provider").
        Assert.True(throttler.IsThrottled("srv/web", "unhealthy", minutes: 60));
        throttler.Record("srv/other", "stopped", minutes: 60);                 // any other notification goes out
        Assert.True(throttler.IsThrottled("srv/web", "unhealthy", minutes: 60),
            "20 minutes into a 60-minute throttle window the entry was pruned by another notification: the event is sent again");
    }

    [Fact]
    public void A_redelivery_after_the_window_was_raised_does_not_prune_with_the_old_window()
    {
        // A redelivery skips IsThrottled and only records. Its Record carries the channel's live window, so a window
        // raised since does not get undercut by pruning with whatever the last check happened to run with.
        var clock = new ManualTime(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var throttler = new NotificationThrottler(clock);
        var original = new NotificationEvent { ServerId = "srv", ContainerId = "c-web", EventType = NotificationEventTypes.Unhealthy };
        Assert.False(throttler.IsThrottled(original, 5));                      // checked and sent under the old window
        throttler.Record(original, 5);

        clock.Now = clock.Now.AddMinutes(20);                                  // window raised to 60 meanwhile
        var redelivery = new NotificationEvent
            { ServerId = "srv", ContainerId = "c-db", EventType = NotificationEventTypes.Stopped, IsRedelivery = true };
        throttler.Record(redelivery, 60);                                      // the only call on this channel since

        Assert.True(throttler.IsThrottled(original, 60),
            "the original repeats 20 minutes into a 60-minute window and is sent again: the redelivery pruned it");
    }
}
