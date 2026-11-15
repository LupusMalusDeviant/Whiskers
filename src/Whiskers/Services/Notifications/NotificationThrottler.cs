using System.Collections.Concurrent;
using Whiskers.Models;

namespace Whiskers.Services.Notifications;

public class NotificationThrottler
{
    private readonly ConcurrentDictionary<string, DateTime> _lastSent = new();
    private readonly TimeProvider _time;

    /// <param name="time">The clock (tests move it); the system clock by default.</param>
    public NotificationThrottler(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // The window is passed per call (from live settings) rather than frozen at construction, so a changed
    // ThrottleMinutes takes effect without recreating the provider. 0 = never throttle.
    public bool IsThrottled(string containerId, string eventType, int minutes)
    {
        var key = $"{containerId}:{eventType}";
        if (_lastSent.TryGetValue(key, out var lastTime))
        {
            return UtcNow - lastTime < TimeSpan.FromMinutes(Math.Max(0, minutes));
        }
        return false;
    }

    /// <summary>Records a send. <paramref name="minutes"/> is the live window (the channel's current
    /// <c>ThrottleMinutes</c>), also for a redelivery, which skips the check: the map is pruned with it, so a window
    /// raised at runtime is not undercut by pruning with an older value.</summary>
    public void Record(string containerId, string eventType, int minutes)
    {
        var key = $"{containerId}:{eventType}";
        var now = UtcNow;
        _lastSent[key] = now;

        // Keep the map bounded: an entry can't affect throttling once it's older than twice the window.
        var cutoff = now - TimeSpan.FromMinutes(2 * Math.Max(1, minutes));
        foreach (var kv in _lastSent)
            if (kv.Value < cutoff) _lastSent.TryRemove(kv.Key, out _);
    }

    /// <summary>Event-aware throttle check. The key includes the server, so the same event on two hosts is
    /// two alerts: server-level events carry no container id, and keyed on that alone the second host to
    /// drop off within the window was silently swallowed — exactly when a mesh outage takes several down at
    /// once. Reachability transitions are never throttled: the tracker already announces each outage once
    /// and closes it with exactly one recovery, so dropping one would leave an outage without its end.
    /// Contract 0017 G: an event that carries an <see cref="NotificationEvent.ApprovalId"/> is never throttled either —
    /// every approval request is its own decision, and one held back waits for a person who is never told.</summary>
    public bool IsThrottled(NotificationEvent evt, int minutes) =>
        !IsExempt(evt) && IsThrottled(KeyFor(evt), evt.EventType, minutes);

    public void Record(NotificationEvent evt, int minutes)
    {
        if (!IsExempt(evt)) Record(KeyFor(evt), evt.EventType, minutes);
    }

    private static bool IsExempt(NotificationEvent evt) =>
        IsTransition(evt.EventType) || !string.IsNullOrEmpty(evt.ApprovalId);

    // Certificate alerts are deduplicated at the source (one per stage per certificate, persisted), and several
    // certificates on one server reach a stage together — the throttle key would swallow all but the first.
    // R1-56: a breach's raise and its <kind>_recovered, a metrics gap and the quiet-hours summary too — each is said once
    // by its source, so a throttled recovery would leave the breach open in every channel.
    private static bool IsTransition(string eventType) => NotificationEventTypes.IsStateTransition(eventType);

    // Deliberately without DeliveryId: CompositeNotificationService gives every event a fresh one, so it would make every
    // event its own slot and switch throttling off.
    private static string KeyFor(NotificationEvent evt) => $"{evt.ServerId}/{evt.ContainerId}";
}
