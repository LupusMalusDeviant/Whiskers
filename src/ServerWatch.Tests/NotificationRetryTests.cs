using ServerWatch.Services.Notifications;

namespace ServerWatch.Tests;

public class NotificationRetryTests
{
    [Fact]
    public async Task SucceedsFirstTry_CalledOnce()
    {
        var calls = 0;
        var (ok, last) = await NotificationRetry.TrySendAsync(() => { calls++; return Task.CompletedTask; }, maxAttempts: 2);
        Assert.True(ok);
        Assert.Null(last);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SucceedsOnSecondAttempt()
    {
        var calls = 0;
        var (ok, last) = await NotificationRetry.TrySendAsync(() =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        }, maxAttempts: 2);
        Assert.True(ok);
        Assert.Null(last);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AllAttemptsFail_ReturnsFalse_NeverThrows()
    {
        var calls = 0;
        var (ok, last) = await NotificationRetry.TrySendAsync(
            () => { calls++; throw new InvalidOperationException("boom"); }, maxAttempts: 2);
        Assert.False(ok);
        Assert.NotNull(last);
        Assert.Equal(2, calls);
    }
}
