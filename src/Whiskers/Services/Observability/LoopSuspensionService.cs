using System.Collections.Concurrent;
using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Observability;

/// <summary>
/// In-memory emergency stop (Plan-0005 WP1).
///
/// <para>Deliberately not persisted across restarts: a pause is a reaction to something happening now, and a
/// forgotten one that survives a restart is a blind spot nobody remembers creating. The 24-hour reminder in
/// <see cref="ScanSupervisor"/> covers the case where a pause outlives its reason within one process life.</para>
/// </summary>
public sealed class LoopSuspensionService : ILoopSuspensionService
{
    private readonly ConcurrentDictionary<string, LoopSuspension> _suspended = new(StringComparer.OrdinalIgnoreCase);
    private readonly INotificationService _notifications;
    private readonly IServerConfigService _servers;
    private readonly ILogger<LoopSuspensionService> _logger;
    private readonly Outcomes.IActionOutcomeService _outcomes;

    public LoopSuspensionService(
        INotificationService notifications,
        IServerConfigService servers,
        ILogger<LoopSuspensionService> logger,
        Outcomes.IActionOutcomeService outcomes)
    {
        _notifications = notifications;
        _servers = servers;
        _logger = logger;
        _outcomes = outcomes;
    }

    public bool IsSuspended(string serverId)
    {
        try
        {
            if (!_suspended.TryGetValue(serverId, out var suspension)) return false;
            if (DateTime.UtcNow < suspension.Until) return true;

            // Expired: let it lapse on its own. A pause that has to be revoked by hand is a pause that gets
            // forgotten, and a forgotten pause is an unmonitored server nobody knows about.
            // R1-48: this is not checks.resume. The end was part of the checks.pause decision that set the deadline,
            // so the lapse takes no new decision; it removes only that very pause (a newer one stays) and resumes
            // observing, which is the fail-open default of this service.
            Lapse(serverId, suspension);
            return false;
        }
        catch (Exception ex)
        {
            // Fail OPEN. Observing is the normal state; a suspension service that fails closed would stop the
            // whole fleet's monitoring without anyone noticing — a quiet outage in place of a loud one.
            _logger.LogError(ex, "Could not read the suspension state for {ServerId} — loops keep running", serverId);
            return false;
        }
    }

    public void Suspend(string serverId, DateTime? until, string reason, bool automatic = false)
    {
        // "Until revoked" is stored as a far-future timestamp rather than a null, so every read path has one
        // shape. The reminder about long-standing pauses lives in the supervisor.
        var deadline = until ?? DateTime.UtcNow.AddYears(10);
        var suspension = new LoopSuspension(serverId, deadline, reason, automatic, DateTime.UtcNow);

        if (_suspended.TryGetValue(serverId, out var existing) && existing.Until >= deadline) return;
        _suspended[serverId] = suspension;

        var name = ServerName(serverId);
        _logger.LogWarning("Background loops for {Server} paused until {Until} ({Reason})", name, deadline, reason);

        // Plan-0006: an automatic action, so it gets checked. If the host's CPU has not come down when the
        // window closes, Whiskers was not the cause — and the pause has taken monitoring away from a server
        // that has a real problem.
        try { _ = _outcomes.RecordAsync(Outcomes.AutomaticActionKind.EmergencyStop, serverId, serverId, name, reason); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not file the pause of {ServerId} for an outcome check", serverId); }

        Announce(NotificationEventTypes.LoopsPaused, serverId, name,
            $"Background checks for {name} are paused" +
            (until is null ? " until revoked" : $" until {deadline:HH:mm} UTC") +
            $" ({reason}). Nothing is being checked there meanwhile — that is not the same as nothing being wrong." +
            (automatic ? " Whiskers did this by itself after repeated failures." : ""));
    }

    public void Resume(string serverId)
    {
        if (_suspended.TryRemove(serverId, out var was)) Announce(serverId, was);
    }

    /// <summary>The end of a pause whose deadline passed: removes exactly <paramref name="expired"/>, so a pause set
    /// again meanwhile is kept.</summary>
    private void Lapse(string serverId, LoopSuspension expired)
    {
        if (_suspended.TryRemove(new KeyValuePair<string, LoopSuspension>(serverId, expired))) Announce(serverId, expired);
    }

    private void Announce(string serverId, LoopSuspension was)
    {
        var name = ServerName(serverId);
        _logger.LogInformation("Background loops for {Server} resumed", name);
        Announce(NotificationEventTypes.LoopsResumed, serverId, name,
            $"Background checks for {name} are running again" + (was.Automatic ? " (the server started answering)." : "."));
    }

    public IReadOnlyList<LoopSuspension> Current() =>
        _suspended.Values.OrderBy(s => s.ServerId, StringComparer.Ordinal).ToList();

    private string ServerName(string serverId) => _servers.GetServer(serverId)?.Name ?? serverId;

    /// <summary>Every pause and every resume is announced. A silent self-throttle turns "quiet" into "blind"
    /// and hides the next incident behind the fix for the last one — the rule from the incident report.</summary>
    private void Announce(string eventType, string serverId, string serverName, string detail)
    {
        try
        {
            _ = _notifications.SendAsync(new NotificationEvent
            {
                EventType = eventType,
                ServerId = serverId,
                ServerName = serverName,
                ContainerName = serverName,
                ImageInfo = detail,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            // A broken notification channel must not prevent the pause itself — the pause is protecting a
            // server right now, and the supervisor will still report that nothing is being checked.
            _logger.LogWarning(ex, "Could not announce the pause change for {ServerId}", serverId);
        }
    }
}
