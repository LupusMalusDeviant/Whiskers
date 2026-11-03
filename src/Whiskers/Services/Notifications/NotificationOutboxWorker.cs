namespace Whiskers.Services.Notifications;

/// <summary>
/// Redelivers what <see cref="INotificationOutbox"/> holds (R1-40): every <see cref="Tick"/>, each due entry is given
/// once more to its channel, within <see cref="CompositeNotificationService.AttemptTimeout"/>. Delivered — or nothing to
/// send any more (the channel was switched off or removed, or the event is throttled because a newer one of its kind
/// got through) — it leaves the outbox; failed, it waits longer, until the outbox gives up on it.
/// </summary>
public sealed class NotificationOutboxWorker : FleetBackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<INotificationChannel> _channels;
    private readonly INotificationOutbox _outbox;
    private readonly ILogger<NotificationOutboxWorker> _logger;
    private readonly TimeSpan _attemptTimeout;

    public NotificationOutboxWorker(IEnumerable<INotificationChannel> channels, INotificationOutbox outbox,
        ILogger<NotificationOutboxWorker> logger)
        : this(channels, outbox, logger, CompositeNotificationService.AttemptTimeout) { }

    internal NotificationOutboxWorker(IEnumerable<INotificationChannel> channels, INotificationOutbox outbox,
        ILogger<NotificationOutboxWorker> logger, TimeSpan attemptTimeout)
    {
        _channels = channels.ToList();
        _outbox = outbox;
        _logger = logger;
        _attemptTimeout = attemptTimeout;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RedeliverDueAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Notification redelivery failed"); }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass. Internal for tests.</summary>
    internal async Task RedeliverDueAsync(DateTime now, CancellationToken ct)
    {
        foreach (var entry in await _outbox.DueAsync(now))
        {
            ct.ThrowIfCancellationRequested();
            var channel = _channels.FirstOrDefault(c => string.Equals(c.Name, entry.Channel, StringComparison.Ordinal));
            if (channel is null)
            {
                _logger.LogWarning("Undelivered {EventType} dropped: the channel {Channel} no longer exists",
                    entry.Event.EventType, entry.Channel);
                await _outbox.CompleteAsync(entry.Id, delivered: false);
                continue;
            }

            var sent = false;
            var (ok, failure) = await NotificationRetry.TrySendAsync(async t => sent = await channel.DeliverAsync(entry.Event, t),
                maxAttempts: 1, _attemptTimeout, TimeSpan.Zero, ct);
            if (ok)
            {
                await _outbox.CompleteAsync(entry.Id, sent);
                if (sent)
                    _logger.LogInformation("Undelivered {EventType} redelivered to {Channel}", entry.Event.EventType, entry.Channel);
                else
                    _logger.LogWarning("Undelivered {EventType} dropped: {Channel} has nothing to send any more (switched off, " +
                                       "or throttled after a newer one of its kind got through)", entry.Event.EventType, entry.Channel);
                continue;
            }
            if (ct.IsCancellationRequested) return;
            await _outbox.FailedAsync(entry.Id, failure!, now);
        }
    }
}
