using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Services.Persistence;
using Whiskers.Models;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Utils;

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
    private readonly INotificationOutbox? _outbox;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _backoff;

    /// <summary>R1-40: how long one delivery attempt to one channel may take (its deadline reaches the network call).</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Attempts per channel and event; with <see cref="AttemptTimeout"/> and <see cref="Backoff"/> the longest a
    /// producer waits for <see cref="SendAsync"/> is 2 × 20 s + 2 s, the channels running side by side.</summary>
    internal const int MaxAttempts = 2;

    internal static readonly TimeSpan Backoff = TimeSpan.FromSeconds(2);

    /// <summary>Events an admin must see whatever the target's mute or preferences say.</summary>
    internal static readonly IReadOnlySet<string> AdministrativeEvents = NotificationEventTypes.AdministrativeEvents;

    public CompositeNotificationService(
        IEnumerable<INotificationChannel> channels,
        IInAppNotificationStore inApp,
        IContainerNotificationPrefsService prefs,
        IServiceProvider sp,
        ILogger<CompositeNotificationService> logger,
        INotificationOutbox? outbox = null)
        : this(channels, inApp, prefs, sp, logger, outbox, AttemptTimeout, Backoff) { }

    internal CompositeNotificationService(
        IEnumerable<INotificationChannel> channels,
        IInAppNotificationStore inApp,
        IContainerNotificationPrefsService prefs,
        IServiceProvider sp,
        ILogger<CompositeNotificationService> logger,
        INotificationOutbox? outbox,
        TimeSpan attemptTimeout,
        TimeSpan backoff)
    {
        _outbox = outbox;
        _attemptTimeout = attemptTimeout;
        _backoff = backoff;
        _channels = channels.ToList();
        _inApp = inApp;
        _prefs = prefs;
        _sp = sp;
        _logger = logger;
    }

    public Task SendAsync(NotificationEvent evt) => SendAsync(evt, CancellationToken.None);

    /// <summary>R1-41: <paramref name="ct"/> (a producer stopped by the host) ends the attempts and retries of every
    /// channel, and they are awaited: when this throws its <see cref="OperationCanceledException"/>, no channel delivery of
    /// this event runs on. A delivery the stop interrupted is not a failed one; a critical event it interrupted is kept for
    /// redelivery, as a failed one would be (R1-40), so the stop cannot lose it. The in-app feed entry and the
    /// <c>AlertHistory</c> row are written before the channels run (a stop already requested ends the send before them; the
    /// history write observes the token). The AI-trigger dispatch takes no token: it returns at once and the runs it starts
    /// are bounded on their own (INV-40), so they can outlive the stop.</summary>
    public async Task SendAsync(NotificationEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

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

        // Redacted once, before anything is formatted, stored or sent (INV-51, R1-25): every producer's free text
        // (a matched log line, a connection error, a model's summary) reaches the in-app feed, the alert history,
        // each channel and the AI-trigger dispatcher without the secrets in it.
        evt = Redacted(evt);

        // Always record in the in-app feed (no external channel needed).
        try { _inApp.Add(evt); } catch (Exception ex) { _logger.LogWarning(ex, "In-app notification failed"); }
        await PersistHistoryAsync(evt, ct);

        // R1-56: quiet hours hold the channels back, nothing else — the in-app feed and AlertHistory above recorded it, and
        // the AI triggers are dispatched as always (a trigger decides for itself). What was held is recorded durably and
        // delivered as one summary after the window. The per-container mute above keeps its meaning.
        if (await HeldAsync(evt, ct))
        {
            await DispatchTriggersAsync(evt);
            ct.ThrowIfCancellationRequested();
            return;
        }

        // Each channel its own copy: what one records about its delivery (the e-mail recipients reached) is kept with
        // its own outbox entry only. The producer's stop (R1-41) reaches every channel's attempts and retries.
        var tasks = _channels.Select(c => DeliverAsync(c, evt.Copy(), ct)).ToList();
        // AI-trigger dispatch runs alongside the channels; lazily resolved to avoid a DI cycle. Once, never retried:
        // a second dispatch would start the triggers a second time. Its wait is bounded; the dispatch itself bounds
        // its runs (INV-40).
        tasks.Add(DispatchTriggersAsync(evt));

        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
    }

    public async Task SendTestAsync()
    {
        var errors = new List<string>();
        foreach (var channel in _channels)
        {
            try { await channel.SendTestAsync(); }
            catch (Exception ex) { errors.Add($"{channel.Name}: {SinkRedaction.Text(ex.Message)}"); }
        }

        if (errors.Count > 0)
            throw new AggregateException($"Some providers failed: {string.Join("; ", errors)}");
    }

    /// <summary>A copy of the event with every text field redacted; the producer's object is left as it is.</summary>
    internal static NotificationEvent Redacted(NotificationEvent evt)
    {
        var copy = evt.Copy();
        copy.ContainerId = SinkRedaction.Text(evt.ContainerId) ?? "";
        copy.ContainerName = SinkRedaction.Text(evt.ContainerName) ?? "";
        copy.Image = SinkRedaction.Text(evt.Image) ?? "";
        copy.ImageName = SinkRedaction.Text(evt.ImageName) ?? "";
        copy.ImageInfo = SinkRedaction.Text(evt.ImageInfo);
        copy.ServerName = SinkRedaction.Text(evt.ServerName);
        copy.DeliveryId ??= Guid.NewGuid().ToString("N");   // R1-40: the same for every retry and redelivery
        return copy;
    }

    /// <summary>Persists the event to <c>AlertHistory</c> — the queryable, server-scoped record behind the
    /// in-memory feed. The table existed from the first migration but nothing ever wrote to it, so every
    /// "what fired last week, and on which host?" question was unanswerable. Retention is handled by the
    /// metrics collector's prune. Never let a history write break the actual notification.</summary>
    private async Task PersistHistoryAsync(NotificationEvent evt, CancellationToken ct)
    {
        // Bounded like a channel attempt: a stalled database must not hold the monitoring cycle that sends the event.
        // The producer's stop (R1-41) ends it as well, and that is not logged as a failed write.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_attemptTimeout);
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
            if (evt.EventType == NotificationEventTypes.ServerRecovered && evt.ServerId is { } sid)
            {
                await db.AlertHistory
                    .Where(a => a.ServerId == sid && a.AlertType == NotificationEventTypes.ServerUnreachable && !a.Resolved)
                    .ExecuteUpdateAsync(u => u.SetProperty(a => a.Resolved, true), deadline.Token);
            }

            await db.SaveChangesAsync(deadline.Token).WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Persisting alert history failed for {EventType}", evt.EventType);
        }
    }

    /// <summary>R1-56: whether quiet hours hold <paramref name="evt"/> back from the channels. Only when a window covers it
    /// <b>and</b> it was recorded for the summary: a rule that cannot be evaluated or a record that cannot be written
    /// delivers the event (an alert is never lost to a failed hold), and so does an unknown or never-suppressed type.</summary>
    private async Task<bool> HeldAsync(NotificationEvent evt, CancellationToken ct)
    {
        if (_sp.GetService<IQuietHoursService>() is not { } quietHours
            || _sp.GetService<IAlertSuppressionStore>() is not { } suppressions) return false;
        QuietHoursMatch? match;
        try { match = quietHours.Match(evt); }
        catch (Exception ex)
        {
            _logger.LogWarning("Quiet hours could not be evaluated for {EventType}; it is delivered ({Error})",
                evt.EventType, NotificationOutbox.Describe(ex));
            return false;
        }
        if (match is null) return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_attemptTimeout);
        try
        {
            var held = await suppressions.HoldAsync(match, evt, deadline.Token).WaitAsync(deadline.Token);
            if (held)
                _logger.LogDebug("Notification {EventType} held by quiet hours {Rule} until the summary", evt.EventType, match.RuleName);
            return held;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogWarning("Notification {EventType} could not be held for quiet hours {Rule}; it is delivered ({Error})",
                evt.EventType, match.RuleName, NotificationOutbox.Describe(ex));
            return false;
        }
    }

    /// <summary>Events whose loss matters: administrative ones, errors and approvals waiting for a person. A channel that
    /// still fails them after the retries keeps them in the outbox for redelivery (R1-40).</summary>
    internal static bool IsCritical(NotificationEvent evt) =>
        AdministrativeEvents.Contains(evt.EventType)
        || evt.EventType == NotificationEventTypes.AgentApproval
        // The end of an outage goes where its start went: an "unreachable" redelivered without its recovery reads as
        // an outage still going on.
        || evt.EventType == NotificationEventTypes.ServerRecovered
        || NotificationFormatter.Describe(evt).Severity == "Error";

    /// <summary>One channel: <see cref="MaxAttempts"/> attempts, each within its deadline. A failure the retries could not
    /// overcome is logged and counted with the channel name and the redacted reason only — never the payload, a URL or
    /// a token — and a critical event goes to the outbox. Never throws: a broken channel cannot stop a monitoring loop.</summary>
    private async Task DeliverAsync(INotificationChannel channel, NotificationEvent evt, CancellationToken ct)
    {
        var (ok, last) = await NotificationRetry.TrySendAsync(t => channel.DeliverAsync(evt, t), MaxAttempts,
            _attemptTimeout, _backoff, ct);
        if (ok) return;

        if (ct.IsCancellationRequested)
        {
            // R1-41: the producer was stopped, the channel did not fail. Nothing is counted or reported as a failure; a
            // critical event goes to the outbox so the stop does not lose it, and is redelivered after the restart.
            if (IsCritical(evt) && _outbox is not null)
            {
                try
                {
                    await _outbox.AddAsync(channel.Name, evt, new OperationCanceledException("The delivery was interrupted by a stop."), DateTime.UtcNow);
                    _logger.LogInformation("Notification {EventType} to {Provider} interrupted by a stop; kept for redelivery", evt.EventType, channel.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Notification {EventType} to {Provider} could not be kept for redelivery ({Error})",
                        evt.EventType, channel.Name, NotificationOutbox.Describe(ex));
                }
            }
            return;
        }

        var failure = last ?? new InvalidOperationException("The delivery failed.");
        if (OutboundTargetRefusedException.Find(failure) is { } refused)
        {
            // R1-54: the outbound guard refused the target. Final for this delivery — not retried, not kept for
            // redelivery — and visible like every failure: the reason names host, port and address, never a URL path.
            _logger.LogError("Notification {EventType} to {Provider} was refused ({Error})",
                evt.EventType, channel.Name, NotificationOutbox.Describe(refused));
            _sp.GetService<Whiskers.Services.Observability.SelfMetrics.ISelfMetrics>()?.Count("notification_failed", "*");
            return;
        }
        var critical = IsCritical(evt) && _outbox is not null;
        _logger.LogError("Notification {EventType} to {Provider} failed after {Attempts} attempts ({Error}){Kept}",
            evt.EventType, channel.Name, MaxAttempts, NotificationOutbox.Describe(failure),
            critical ? "; kept for redelivery" : "");
        _sp.GetService<Whiskers.Services.Observability.SelfMetrics.ISelfMetrics>()?.Count("notification_failed", "*");
        if (!critical) return;
        try
        {
            await _outbox!.AddAsync(channel.Name, evt, failure, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError("Notification {EventType} to {Provider} could not be kept for redelivery ({Error})",
                evt.EventType, channel.Name, NotificationOutbox.Describe(ex));
        }
    }

    private async Task DispatchTriggersAsync(NotificationEvent evt)
    {
        try
        {
            await _sp.GetRequiredService<IAiTriggerDispatcher>().OnEventAsync(evt).WaitAsync(_attemptTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError("AI-trigger dispatch for {EventType} failed ({Error})", evt.EventType, NotificationOutbox.Describe(ex));
        }
    }
}
