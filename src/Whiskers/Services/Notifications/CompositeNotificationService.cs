using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Services.Persistence;
using Whiskers.Models;
using Whiskers.Services.Agent.Triggers;

namespace Whiskers.Services.Notifications;

/// <summary>
/// Fans a notification out to every configured channel (Mattermost, Matrix, Telegram, …) plus the in-app
/// feed, and feeds every event to the AI-trigger dispatcher (resolved lazily to avoid a DI cycle). Channels
/// arrive as <c>IEnumerable&lt;INotificationChannel&gt;</c> (changeme C9) instead of being hard-wired, so
/// adding/removing a channel is a registration change only. Each channel does its own enabled/disabled check.
/// </summary>
public class CompositeNotificationService : INotificationService
{
    private readonly IReadOnlyList<INotificationChannel> _channels;
    private readonly IInAppNotificationStore _inApp;
    private readonly IContainerNotificationPrefsService _prefs;
    private readonly IServiceProvider _sp;
    private readonly ILogger<CompositeNotificationService> _logger;

    /// <summary>Events an admin must see whatever the target's mute or preferences say.</summary>
    internal static readonly IReadOnlySet<string> AdministrativeEvents =
        new HashSet<string>(StringComparer.Ordinal) { "automation_suspended", "automation_refused" };

    public CompositeNotificationService(
        IEnumerable<INotificationChannel> channels,
        IInAppNotificationStore inApp,
        IContainerNotificationPrefsService prefs,
        IServiceProvider sp,
        ILogger<CompositeNotificationService> logger)
    {
        _channels = channels.ToList();
        _inApp = inApp;
        _prefs = prefs;
        _sp = sp;
        _logger = logger;
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        // Per-container mute/prefs are enforced HERE, for every producer. They used to be checked only by
        // the container health monitor, so a muted container still sent log alerts, CVE findings, image
        // updates and metric alarms — the mute switch silenced barely a third of what it promised.
        // Server-level events carry no container name and are never suppressed, and neither are administrative
        // events about unattended automation (R1-04): a muted target must not hide that its task was suspended.
        if (!string.IsNullOrWhiteSpace(evt.ContainerName) && !AdministrativeEvents.Contains(evt.EventType)
            && !_prefs.ShouldNotify(evt.ContainerName, evt.EventType))
        {
            _logger.LogDebug("Notification suppressed for {Container} ({EventType}) — muted by prefs",
                evt.ContainerName, evt.EventType);
            return;
        }

        // Always record in the in-app feed (no external channel needed).
        try { _inApp.Add(evt); } catch (Exception ex) { _logger.LogWarning(ex, "In-app notification failed"); }
        await PersistHistoryAsync(evt);

        var tasks = _channels.Select(c => SafeSend(c.Name, () => c.SendAsync(evt))).ToList();
        // AI-trigger dispatch runs alongside the channels; lazily resolved to avoid a DI cycle.
        tasks.Add(SafeSend("AI-Trigger", () => _sp.GetRequiredService<IAiTriggerDispatcher>().OnEventAsync(evt)));

        await Task.WhenAll(tasks);
    }

    public async Task SendTestAsync()
    {
        var errors = new List<string>();
        foreach (var channel in _channels)
        {
            try { await channel.SendTestAsync(); }
            catch (Exception ex) { errors.Add($"{channel.Name}: {ex.Message}"); }
        }

        if (errors.Count > 0)
            throw new AggregateException($"Some providers failed: {string.Join("; ", errors)}");
    }

    /// <summary>Persists the event to <c>AlertHistory</c> — the queryable, server-scoped record behind the
    /// in-memory feed. The table existed from the first migration but nothing ever wrote to it, so every
    /// "what fired last week, and on which host?" question was unanswerable. Retention is handled by the
    /// metrics collector's prune. Never let a history write break the actual notification.</summary>
    private async Task PersistHistoryAsync(NotificationEvent evt)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var (title, _) = NotificationFormatter.Describe(evt);

            db.AlertHistory.Add(new AlertHistoryEntity
            {
                ServerId = evt.ServerId ?? "local",
                ContainerId = evt.ContainerId,
                ContainerName = evt.ContainerName,
                AlertType = evt.EventType,
                Message = $"{title} — {NotificationFormatter.Detail(evt)}",
                Timestamp = evt.Timestamp
            });

            // A recovery closes the outage it ends, so the history reads as episodes instead of a wall of
            // unresolved rows.
            if (evt.EventType == "server_recovered" && evt.ServerId is { } sid)
            {
                await db.AlertHistory
                    .Where(a => a.ServerId == sid && a.AlertType == "server_unreachable" && !a.Resolved)
                    .ExecuteUpdateAsync(u => u.SetProperty(a => a.Resolved, true));
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting alert history failed for {EventType}", evt.EventType);
        }
    }

    private async Task SafeSend(string provider, Func<Task> action)
    {
        // Retry once on failure; the per-client 15s HttpClient timeout (Program.cs) bounds each attempt so a
        // slow endpoint can't stall the loop. Log only the provider name (never the payload/URL).
        var (ok, last) = await NotificationRetry.TrySendAsync(action, maxAttempts: 2);
        if (!ok)
            _logger.LogError(last, "Notification provider {Provider} failed after retry", provider);
    }
}
