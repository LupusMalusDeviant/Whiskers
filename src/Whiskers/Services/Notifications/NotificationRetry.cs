namespace Whiskers.Services.Notifications;

/// <summary>Small retry helper for notification sends: try again on failure and never propagate, so a
/// broken provider can't take down a monitoring loop. R1-40: every attempt has its own deadline that reaches the
/// provider's network call, so a slow endpoint delays a cycle by at most
/// <c>maxAttempts × attemptTimeout + (maxAttempts − 1) × backoff</c>.</summary>
public static class NotificationRetry
{
    /// <summary>Runs <paramref name="action"/>, retrying up to <paramref name="maxAttempts"/> times.
    /// Returns (true, null) as soon as a call succeeds, or (false, lastException) if all attempts threw.</summary>
    public static async Task<(bool Ok, Exception? Last)> TrySendAsync(Func<Task> action, int maxAttempts)
        => await TrySendAsync(_ => action(), maxAttempts, Timeout.InfiniteTimeSpan, TimeSpan.Zero);

    /// <summary>
    /// Runs <paramref name="action"/> up to <paramref name="maxAttempts"/> times, each attempt cancelled after
    /// <paramref name="attemptTimeout"/> (an attempt that runs out of time fails with a <see cref="TimeoutException"/>),
    /// waiting <paramref name="backoff"/> between attempts. The caller's <paramref name="ct"/> ends the retries: its
    /// cancellation is reported as an <see cref="OperationCanceledException"/>, not retried.
    /// </summary>
    public static async Task<(bool Ok, Exception? Last)> TrySendAsync(Func<CancellationToken, Task> action, int maxAttempts,
        TimeSpan attemptTimeout, TimeSpan backoff, CancellationToken ct = default)
    {
        Exception? last = null;
        var attempts = Math.Max(1, maxAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return (false, last ?? new OperationCanceledException(ct));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (attemptTimeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(attemptTimeout);
            try
            {
                await action(deadline.Token);
                return (true, null);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                return (false, ex);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                last = new TimeoutException($"The attempt did not finish within {attemptTimeout.TotalSeconds:0} s.");
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (attempt < attempts && backoff > TimeSpan.Zero)
            {
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException ex) { return (false, ex); }
            }
        }
        return (false, last);
    }
}
