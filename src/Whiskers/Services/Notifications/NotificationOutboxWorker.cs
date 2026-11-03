namespace Whiskers.Services.Notifications;

/// <summary>
/// Redelivers what <see cref="INotificationOutbox"/> holds (R1-40) and owns its file: what changed is written
/// (<see cref="INotificationOutbox.FlushAsync"/>) right after an addition, after every single redelivery and at the
/// latest every <see cref="Tick"/> — so nothing waits for a whole backlog to be worked off. Each due entry is checked
/// again just before it is sent (still in the outbox, not too old by then) and given once more to its channel, within
/// <see cref="CompositeNotificationService.AttemptTimeout"/>, as
/// a redelivery (<see cref="Whiskers.Models.NotificationEvent.IsRedelivery"/>: not throttled again, the same
/// <see cref="Whiskers.Models.NotificationEvent.DeliveryId"/>). Delivered it leaves the outbox; its channel switched off or
/// removed, it is dropped and counted; failed, it waits longer — counted from the failure, not from the start of the pass —
/// until the outbox gives up on it.
/// </summary>
public sealed class NotificationOutboxWorker : FleetBackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<INotificationChannel> _channels;
    private readonly INotificationOutbox _outbox;
    private readonly ILogger<NotificationOutboxWorker> _logger;
    private readonly TimeSpan _attemptTimeout;

    /// <summary>The time (tests control it).</summary>
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

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
                await _outbox.FlushAsync(stoppingToken);
                await RedeliverDueAsync(Clock(), stoppingToken);
                await _outbox.FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Notification redelivery failed"); }

            try { await _outbox.WaitForWorkAsync(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        // What changed last is written before the process ends, bounded so a stalled volume cannot hold the stop.
        try { await _outbox.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { _logger.LogError("The notification outbox could not be saved at stop ({Error})", NotificationOutbox.Describe(ex)); }
    }

    /// <summary>One pass over what is due at <paramref name="now"/>. Internal for tests (which may pass a later
    /// <paramref name="now"/>; time passing during the pass is added to it).</summary>
    internal async Task RedeliverDueAsync(DateTime now, CancellationToken ct)
    {
        var started = Clock();
        DateTime Now() => now + (Clock() - started);

        foreach (var due in await _outbox.DueAsync(now))
        {
            ct.ThrowIfCancellationRequested();
            await RedeliverAsync(due.Id, Now, ct);
            await _outbox.FlushAsync(ct);               // each outcome is on disk before the next delivery starts
        }
    }

    private async Task RedeliverAsync(string id, Func<DateTime> now, CancellationToken ct)
    {
        // Again just before it is sent: an earlier delivery of this pass may have taken its time.
        var entry = await _outbox.CurrentAsync(id, now());
        if (entry is null) return;

        var channel = _channels.FirstOrDefault(c => string.Equals(c.Name, entry.Channel, StringComparison.Ordinal));
        if (channel is null)
        {
            await _outbox.CompleteAsync(entry.Id, delivered: false, "the channel no longer exists");
            return;
        }

        var evt = entry.Event.Copy();
        evt.IsRedelivery = true;                    // it passed the throttle when it was first sent
        var sent = false;
        var (ok, failure) = await NotificationRetry.TrySendAsync(async t => sent = await channel.DeliverAsync(evt, t),
            maxAttempts: 1, _attemptTimeout, TimeSpan.Zero, ct);
        if (ok)
        {
            await _outbox.CompleteAsync(entry.Id, sent, sent ? null : "the channel is switched off or not configured");
            if (sent)
                _logger.LogInformation("Undelivered {EventType} redelivered to {Channel}", entry.Event.EventType, entry.Channel);
            return;
        }
        // A stop is no failed attempt — unless the channel got part of the way (recipients reached), which is kept.
        if (ct.IsCancellationRequested && ReferenceEquals(evt.DeliveredRecipients, entry.Event.DeliveredRecipients)) return;
        await _outbox.FailedAsync(entry.Id, failure!, now(), evt);
    }
}
