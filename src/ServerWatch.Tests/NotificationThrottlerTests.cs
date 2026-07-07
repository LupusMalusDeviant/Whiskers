using ServerWatch.Services.Notifications;

namespace ServerWatch.Tests;

public class NotificationThrottlerTests
{
    [Fact]
    public void NotRecorded_IsNotThrottled()
    {
        var t = new NotificationThrottler(60);
        Assert.False(t.IsThrottled("c1", "stopped", 60));
    }

    [Fact]
    public void AfterRecord_ThrottledWithinWindow()
    {
        var t = new NotificationThrottler(60);
        t.Record("c1", "stopped");
        Assert.True(t.IsThrottled("c1", "stopped", 60));
    }

    [Fact]
    public void WindowIsReadPerCall_NotFrozenAtConstruction()
    {
        // NIED-8: the ctor value (60) no longer drives IsThrottled. A just-recorded key throttles under a
        // 60-min window but NOT under a 0-min window passed at the call.
        var t = new NotificationThrottler(60);
        t.Record("c1", "stopped");
        Assert.True(t.IsThrottled("c1", "stopped", 60));
        Assert.False(t.IsThrottled("c1", "stopped", 0));
    }

    [Fact]
    public void DifferentEventType_IsNotThrottled()
    {
        var t = new NotificationThrottler(60);
        t.Record("c1", "stopped");
        Assert.False(t.IsThrottled("c1", "restart_loop", 60));
    }
}
