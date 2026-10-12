using System.Collections.Concurrent;
using Whiskers.Models;

namespace Whiskers.Services.Notifications;

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

    /// <summary>Event-aware throttle check. The key includes the server, so the same event on two hosts is
    /// two alerts: server-level events carry no container id, and keyed on that alone the second host to
    /// drop off within the window was silently swallowed — exactly when a mesh outage takes several down at
    /// once. Reachability transitions are never throttled: the tracker already announces each outage once
    /// and closes it with exactly one recovery, so dropping one would leave an outage without its end.</summary>
    public bool IsThrottled(NotificationEvent evt, int minutes) =>
        !IsTransition(evt.EventType) && IsThrottled(KeyFor(evt), evt.EventType, minutes);

    public void Record(NotificationEvent evt)
    {
        if (!IsTransition(evt.EventType)) Record(KeyFor(evt), evt.EventType);
    }

    private static bool IsTransition(string eventType) =>
        eventType is "server_unreachable" or "server_recovered";

    private static string KeyFor(NotificationEvent evt) => $"{evt.ServerId}/{evt.ContainerId}";
}
