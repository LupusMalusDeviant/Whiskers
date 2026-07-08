namespace Whiskers.Services.Notifications;

/// <summary>Small retry helper for notification sends: try once more on failure and never propagate, so a
/// broken provider can't take down a monitoring loop. Combined with a short per-client HttpClient timeout
/// (Program.cs), this bounds how long a slow endpoint can delay a background cycle.</summary>
public static class NotificationRetry
{
    /// <summary>Runs <paramref name="action"/>, retrying up to <paramref name="maxAttempts"/> times.
    /// Returns (true, null) as soon as a call succeeds, or (false, lastException) if all attempts threw.</summary>
    public static async Task<(bool Ok, Exception? Last)> TrySendAsync(Func<Task> action, int maxAttempts)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            try
            {
                await action();
                return (true, null);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        return (false, last);
    }
}
