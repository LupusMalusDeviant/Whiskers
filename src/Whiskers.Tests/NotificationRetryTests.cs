using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

public class NotificationRetryTests
{
    /// <summary>R1-40 (F-08): a channel adapter no longer swallows its failure, so the composite's retry sees it — through
    /// the real Slack adapter over HTTP: the first POST fails with 500, the second gets through, nothing is kept.</summary>
    [Fact]
    public async Task Provider_failure_reaches_retry_and_second_attempt_succeeds()
    {
        var handler = new NotificationDeliveryTests.ScriptedHandler(NotificationDeliveryTests.Status(500));
        var outbox = NotificationOutbox.InMemory(Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationOutbox>.Instance);
        var composite = NotificationDeliveryTests.Composite([NotificationDeliveryTests.Slack(handler)], outbox);

        await composite.SendAsync(new Whiskers.Models.NotificationEvent { EventType = "stopped", ContainerName = "web" });

        Assert.Equal(2, handler.Requests);
        Assert.Contains("Container stopped", handler.LastBody);
        Assert.Equal(new NotificationOutboxStatus(0, 0, 0, 0), outbox.Status);
    }

    [Fact]
    public async Task An_attempt_past_its_deadline_fails_as_a_timeout_and_the_callers_cancellation_stays_one()
    {
        var (ok, last) = await NotificationRetry.TrySendAsync(ct => Task.Delay(Timeout.Infinite, ct), maxAttempts: 2,
            TimeSpan.FromMilliseconds(50), TimeSpan.Zero);
        Assert.False(ok);
        Assert.IsType<TimeoutException>(last);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var calls = 0;
        (ok, last) = await NotificationRetry.TrySendAsync(ct => { calls++; return Task.Delay(Timeout.Infinite, ct); },
            maxAttempts: 3, TimeSpan.FromSeconds(30), TimeSpan.Zero, cts.Token);
        Assert.False(ok);
        Assert.IsAssignableFrom<OperationCanceledException>(last);
        Assert.Equal(1, calls);                                // not retried after the caller cancelled
    }

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
