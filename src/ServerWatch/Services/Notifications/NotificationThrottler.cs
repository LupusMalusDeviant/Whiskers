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

    public bool IsThrottled(string containerId, string eventType)
    {
        var key = $"{containerId}:{eventType}";
        if (_lastSent.TryGetValue(key, out var lastTime))
        {
            return DateTime.UtcNow - lastTime < TimeSpan.FromMinutes(_throttleMinutes);
        }
        return false;
    }

    public void Record(string containerId, string eventType)
    {
        var key = $"{containerId}:{eventType}";
        _lastSent[key] = DateTime.UtcNow;
    }
}
