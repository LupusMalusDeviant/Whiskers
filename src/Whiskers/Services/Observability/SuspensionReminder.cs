using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Observability;

/// <summary>
/// Keeps saying that a server is still not being watched (Plan-0005 WP0).
///
/// <para>The emergency stop creates a failure mode of its own: a pause that outlives its reason. Nobody
/// deliberately leaves a server unmonitored for a week — they pause it for ten minutes, get pulled into
/// something else, and the pause becomes the new normal. The one-time announcement scrolls out of the channel
/// and the server looks quiet in exactly the way a healthy one does.</para>
///
/// <para>This is a separate service on purpose. <see cref="ScanSupervisor"/> must not know about suspensions
/// at all — it reports that nothing is being checked, and a supervisor that can be silenced by the switch it
/// supervises is a blindfold with a label on it. So the reminder lives here, where it can read the pauses,
/// and the supervisor stays deaf to them. Neither one can be paused.</para>
/// </summary>
public sealed class SuspensionReminder : Whiskers.Services.FleetBackgroundService
{
    /// <summary>How long a pause may stand before it is treated as a blind spot rather than a decision.</summary>
    public static readonly TimeSpan ReminderAfter = TimeSpan.FromHours(24);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly ILoopSuspensionService _suspension;
    private readonly INotificationService _notifications;
    private readonly IServerConfigService _servers;
    private readonly ILogger<SuspensionReminder> _logger;
    private readonly Dictionary<string, DateTime> _lastReminded = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _reminderAfter;

    public SuspensionReminder(
        ILoopSuspensionService suspension,
        INotificationService notifications,
        IServerConfigService servers,
        ILogger<SuspensionReminder> logger,
        TimeSpan? reminderAfter = null)
    {
        _suspension = suspension;
        _notifications = notifications;
        _servers = servers;
        _logger = logger;
        _reminderAfter = reminderAfter ?? ReminderAfter;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Remind(DateTime.UtcNow);
            }
            catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))
            {
                _logger.LogError(ex, "Could not check for long-standing pauses");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass. Public so a test can drive time instead of waiting a day for it.</summary>
    public IReadOnlyList<string> Remind(DateTime now)
    {
        var reminded = new List<string>();

        foreach (var suspension in _suspension.Current())
        {
            // Measured from the START of the pause. Measuring from its end would let the open-ended pauses —
            // the only kind that can quietly become permanent — slip past the reminder entirely.
            if (now - suspension.Since < _reminderAfter) continue;

            if (_lastReminded.TryGetValue(suspension.ServerId, out var last) && now - last < _reminderAfter)
                continue;

            _lastReminded[suspension.ServerId] = now;
            reminded.Add(suspension.ServerId);

            var name = _servers.GetServer(suspension.ServerId)?.Name ?? suspension.ServerId;
            _logger.LogWarning("Background checks for {Server} have been paused for over {Hours}h ({Reason})",
                name, _reminderAfter.TotalHours, suspension.Reason);

            Announce(suspension, name);
        }

        // Forget servers that came back, so a later pause starts its own clock instead of inheriting this one.
        var stillPaused = _suspension.Current().Select(s => s.ServerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _lastReminded.Keys.Where(k => !stillPaused.Contains(k)).ToList())
            _lastReminded.Remove(gone);

        return reminded;
    }

    private void Announce(LoopSuspension suspension, string name)
    {
        try
        {
            _ = _notifications.SendAsync(new NotificationEvent
            {
                EventType = "loops_paused_reminder",
                ServerId = suspension.ServerId,
                ServerName = name,
                ContainerName = name,
                ImageInfo =
                    $"{name} has had its background checks paused for more than {_reminderAfter.TotalHours:0} hours " +
                    $"({suspension.Reason}). Nothing there is being watched. If the reason is gone, resume it; " +
                    "if it is not, this will keep asking.",
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send the pause reminder for {ServerId}", suspension.ServerId);
        }
    }
}
