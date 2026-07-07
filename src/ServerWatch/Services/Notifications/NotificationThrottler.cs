using System.Collections.Concurrent;

namespace ServerWatch.Services.Notifications;

public class NotificationThrottler
{
    private readonly ConcurrentDictionary<string, DateTime> _lastSent = new();
    private readonly int _throttleMinutes;

    public NotificationThrottler(int throttleMinutes)
    {
        _throttleMinutes = throttleMinutes;
    }

    // The window is passed per call (from live settings) rather than frozen at construction, so a changed
    // ThrottleMinutes takes effect without recreating the provider. 0 = never throttle.
    public bool IsThrottled(string containerId, string eventType, int minutes)
    {
        var key = $"{containerId}:{eventType}";
        if (_lastSent.TryGetValue(key, out var lastTime))
        {
            return DateTime.UtcNow - lastTime < TimeSpan.FromMinutes(Math.Max(0, minutes));
        }
        return false;
    }

    public void Record(string containerId, string eventType)
    {
        var key = $"{containerId}:{eventType}";
        var now = DateTime.UtcNow;
        _lastSent[key] = now;

        // Keep the map bounded: an entry can't affect throttling once it's older than twice the window.
        var cutoff = now - TimeSpan.FromMinutes(2 * Math.Max(1, _throttleMinutes));
        foreach (var kv in _lastSent)
            if (kv.Value < cutoff) _lastSent.TryRemove(kv.Key, out _);
    }
}
