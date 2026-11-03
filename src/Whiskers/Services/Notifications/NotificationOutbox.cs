using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Persistence;
using Whiskers.Utils;

namespace Whiskers.Services.Notifications;

/// <summary>A critical notification a channel could not deliver, waiting for redelivery to that channel (R1-40).</summary>
public sealed class NotificationOutboxEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>The channel's <see cref="INotificationChannel.Name"/>; only that channel gets it again.</summary>
    public string Channel { get; set; } = "";
    /// <summary>The event as the channels got it: already redacted (INV-51).</summary>
    public NotificationEvent Event { get; set; } = new();
    public int Attempts { get; set; }
    public DateTime FirstFailedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    /// <summary>The last failure: exception type and redacted message, never a URL or token.</summary>
    public string? LastError { get; set; }
}

/// <summary>What the outbox holds and has given up on since the start, for the metrics.</summary>
public sealed record NotificationOutboxStatus(int Pending, long Redelivered, long Exhausted, long Dropped);

/// <summary>
/// The bounded, durable outbox of critical notifications (R1-40, F-08): an event a channel still failed after the
/// composite's retries is kept in <c>notification-outbox.json</c> and redelivered to that channel by
/// <see cref="NotificationOutboxWorker"/> — also after a restart — with growing waits, until it is delivered, the
/// channel has nothing to send any more (switched off, throttled), or it is exhausted
/// (<see cref="NotificationOutbox.MaxAttempts"/> redeliveries or <see cref="NotificationOutbox.MaxAge"/>). At most
/// <see cref="NotificationOutbox.MaxEntries"/> are kept; a full outbox drops its oldest entry. Giving up is logged and
/// counted, never silent.
/// </summary>
public interface INotificationOutbox
{
    /// <summary>Keeps <paramref name="evt"/> for redelivery to <paramref name="channel"/>.</summary>
    Task AddAsync(string channel, NotificationEvent evt, Exception failure, DateTime now);

    /// <summary>The entries due at <paramref name="now"/>, oldest first.</summary>
    Task<IReadOnlyList<NotificationOutboxEntry>> DueAsync(DateTime now);

    /// <summary>The entry leaves the outbox: <paramref name="delivered"/> when it was sent, otherwise it has nothing to
    /// deliver any more (its channel switched off or removed, the event throttled).</summary>
    Task CompleteAsync(string id, bool delivered);

    /// <summary>Another redelivery failed. Returns <c>true</c> when the entry is exhausted and was removed.</summary>
    Task<bool> FailedAsync(string id, Exception failure, DateTime now);

    NotificationOutboxStatus Status { get; }
}

/// <inheritdoc cref="INotificationOutbox"/>
public sealed class NotificationOutbox : INotificationOutbox
{
    internal const int MaxEntries = 200;
    internal const int MaxAttempts = 8;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(60);

    private readonly JsonFileStore<OutboxFile>? _file;
    private readonly ILogger<NotificationOutbox> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<NotificationOutboxEntry>? _entries;
    private long _redelivered, _exhausted, _dropped;

    public NotificationOutbox(DataPathOptions paths, ILogger<NotificationOutbox> logger)
        : this(new JsonFileStore<OutboxFile>(paths.NotificationOutboxJson), logger) { }

    internal NotificationOutbox(JsonFileStore<OutboxFile>? file, ILogger<NotificationOutbox> logger)
    {
        _file = file;
        _logger = logger;
    }

    /// <summary>Kept in memory only (tests).</summary>
    internal static NotificationOutbox InMemory(ILogger<NotificationOutbox> logger) => new((JsonFileStore<OutboxFile>?)null, logger);

    public NotificationOutboxStatus Status => new(Volatile.Read(ref _entries)?.Count ?? 0,
        Interlocked.Read(ref _redelivered), Interlocked.Read(ref _exhausted), Interlocked.Read(ref _dropped));

    public async Task AddAsync(string channel, NotificationEvent evt, Exception failure, DateTime now)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = await EntriesAsync();
            while (entries.Count >= MaxEntries)
            {
                var oldest = entries[0];
                entries.RemoveAt(0);
                Interlocked.Increment(ref _dropped);
                _logger.LogError("Notification outbox full ({Max}): dropped the oldest undelivered {EventType} for {Channel}",
                    MaxEntries, oldest.Event.EventType, oldest.Channel);
            }
            entries.Add(new NotificationOutboxEntry
            {
                Channel = channel,
                Event = evt.Copy(),
                Attempts = 0,
                FirstFailedAt = now,
                NextAttemptAt = now + WaitAfter(1),
                LastError = Describe(failure),
            });
            await SaveAsync(entries);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<NotificationOutboxEntry>> DueAsync(DateTime now)
    {
        await _gate.WaitAsync();
        try
        {
            return (await EntriesAsync()).Where(e => e.NextAttemptAt <= now).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(string id, bool delivered)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = await EntriesAsync();
            if (entries.RemoveAll(e => e.Id == id) == 0) return;
            if (delivered) Interlocked.Increment(ref _redelivered);
            await SaveAsync(entries);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> FailedAsync(string id, Exception failure, DateTime now)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = await EntriesAsync();
            var entry = entries.FirstOrDefault(e => e.Id == id);
            if (entry is null) return false;
            entry.Attempts++;
            entry.LastError = Describe(failure);
            var exhausted = entry.Attempts >= MaxAttempts || now - entry.FirstFailedAt >= MaxAge;
            if (exhausted)
            {
                entries.Remove(entry);
                Interlocked.Increment(ref _exhausted);
                _logger.LogError("Notification {EventType} could not be delivered to {Channel}: gave up after {Attempts} " +
                                 "redeliveries since {First:u} (last error: {Error})",
                    entry.Event.EventType, entry.Channel, entry.Attempts, entry.FirstFailedAt, entry.LastError);
            }
            else
            {
                entry.NextAttemptAt = now + WaitAfter(entry.Attempts + 1);
            }
            await SaveAsync(entries);
            return exhausted;
        }
        finally { _gate.Release(); }
    }

    /// <summary>1, 2, 4, … minutes, at most an hour.</summary>
    internal static TimeSpan WaitAfter(int attempt) =>
        TimeSpan.FromMinutes(Math.Min(MaxWait.TotalMinutes, Math.Pow(2, Math.Max(0, attempt - 1))));

    /// <summary>Type and redacted message of a failure: what an admin needs, without the secrets a provider may echo.</summary>
    internal static string Describe(Exception failure)
    {
        var message = SinkRedaction.Text(failure.Message) ?? "";
        if (message.Length > 300) message = message[..300] + "…";
        return $"{failure.GetType().Name}: {message}";
    }

    private async Task<List<NotificationOutboxEntry>> EntriesAsync()
    {
        if (_entries is not null) return _entries;
        List<NotificationOutboxEntry> loaded = [];
        if (_file is not null)
        {
            try
            {
                loaded = (await _file.LoadAsync()).Entries ?? [];
            }
            catch (Exception ex)
            {
                // Unreadable without a usable previous generation: the entries are lost, which is said, and new ones
                // are kept from here on.
                _logger.LogError(ex, "The notification outbox could not be read; its undelivered notifications are lost");
            }
        }
        // Bounded also when the file was larger (edited, or written by a later version with a higher bound).
        if (loaded.Count > MaxEntries) loaded = loaded.OrderBy(e => e.FirstFailedAt).TakeLast(MaxEntries).ToList();
        Volatile.Write(ref _entries, loaded.OrderBy(e => e.FirstFailedAt).ToList());
        return _entries!;
    }

    private async Task SaveAsync(List<NotificationOutboxEntry> entries)
    {
        if (_file is null) return;
        try
        {
            await _file.SaveAsync(new OutboxFile { Entries = entries });
        }
        catch (Exception ex)
        {
            // Kept in memory: redelivered while this process runs, lost only if it stops first.
            _logger.LogError(ex, "The notification outbox could not be saved");
        }
    }

    /// <summary>The file's shape.</summary>
    public sealed class OutboxFile
    {
        public List<NotificationOutboxEntry>? Entries { get; set; } = [];
    }
}
