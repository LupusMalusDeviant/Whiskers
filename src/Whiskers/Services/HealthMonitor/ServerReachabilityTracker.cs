using System.Collections.Concurrent;
using Whiskers.Models;

namespace Whiskers.Services.HealthMonitor;

/// <summary>
/// Turns the per-cycle "which servers answered" signal of a <see cref="FleetContainerListing"/> into
/// <c>server_unreachable</c> / <c>server_recovered</c> events.
/// <para>Until this existed, a host dropping off the fleet was silent: the dashboard showed it as
/// unreachable, but nothing was sent anywhere — and every container alert and log-alert rule covering
/// that host quietly stopped producing anything. A blind monitor looks exactly like a healthy one.</para>
/// <para>A single failed cycle is not an outage (a tunnel rebuild or a host slower than the listing's 8s
/// bound trips it), so an alert needs <paramref name="threshold"/> consecutive failures. Each outage is
/// announced once and closed by exactly one recovery.</para>
/// <para><b>Cold start.</b> An outage is a TRANSITION. Right after a restart the remote connections
/// (SSH tunnels, mTLS sessions) are not up yet, so every server fails the first cycles — measured on a
/// six-server fleet that produced ten pointless notifications per restart, which is exactly how an alert
/// gets ignored. A server that has not answered even once since start therefore needs the much longer
/// <paramref name="coldStartThreshold"/> before it counts as unreachable; once it has answered, the normal
/// threshold applies. A host that really is dead at startup is still reported, just later.</para>
/// </summary>
public sealed class ServerReachabilityTracker
{
    private readonly int _threshold;
    private readonly int _coldStartThreshold;
    private readonly ConcurrentDictionary<string, int> _failStreak = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _outageAlerted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _everAnswered = new(StringComparer.OrdinalIgnoreCase);

    public ServerReachabilityTracker(int threshold, int coldStartThreshold)
    {
        _threshold = Math.Max(1, threshold);
        _coldStartThreshold = Math.Max(_threshold, coldStartThreshold);
    }

    /// <summary>Folds one cycle's listing into the tracker and returns the events to send (usually none).</summary>
    public IReadOnlyList<NotificationEvent> Evaluate(FleetContainerListing listing)
    {
        var events = new List<NotificationEvent>();

        foreach (var failure in listing.FailedServers)
        {
            // R1-41: a call that ran out of time in Whiskers' own queue never reached the server. It is neither evidence
            // of an outage (no streak) nor of a recovery (the streak stays as it was).
            if (failure.NotAsked) continue;
            var streak = _failStreak.AddOrUpdate(failure.ServerId, 1, (_, prev) => prev + 1);
            var needed = _everAnswered.ContainsKey(failure.ServerId) ? _threshold : _coldStartThreshold;
            if (streak < needed || _outageAlerted.GetValueOrDefault(failure.ServerId)) continue;

            _outageAlerted[failure.ServerId] = true;
            events.Add(new NotificationEvent
            {
                // Server-level event: no container, so the detail line carries the host and the reason.
                EventType = "server_unreachable",
                ServerId = failure.ServerId,
                ServerName = failure.ServerName,
                ImageInfo = $"{failure.ServerName} — {failure.Error}"
            });
        }

        foreach (var serverId in listing.RespondedServerIds)
        {
            _everAnswered[serverId] = true;
            _failStreak.TryRemove(serverId, out _);
            if (!_outageAlerted.TryRemove(serverId, out var wasAlerted) || !wasAlerted) continue;

            var name = listing.Containers.FirstOrDefault(c =>
                string.Equals(c.ServerId, serverId, StringComparison.OrdinalIgnoreCase))?.ServerName ?? serverId;
            events.Add(new NotificationEvent
            {
                EventType = "server_recovered",
                ServerId = serverId,
                ServerName = name,
                ImageInfo = $"{name} — reachable again"
            });
        }

        // Drop bookkeeping for servers that are no longer part of the fleet at all.
        var known = listing.RespondedServerIds
            .Concat(listing.FailedServers.Select(f => f.ServerId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _failStreak.Keys) if (!known.Contains(id)) _failStreak.TryRemove(id, out _);
        foreach (var id in _outageAlerted.Keys) if (!known.Contains(id)) _outageAlerted.TryRemove(id, out _);
        foreach (var id in _everAnswered.Keys) if (!known.Contains(id)) _everAnswered.TryRemove(id, out _);

        return events;
    }
}
