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
    /// <summary>The event as the channels got it: already redacted (INV-51), with its <see cref="NotificationEvent.DeliveryId"/>.</summary>
    public NotificationEvent Event { get; set; } = new();
    public int Attempts { get; set; }
    public DateTime FirstFailedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    /// <summary>The last failure: exception type and redacted message, never a URL or token.</summary>
    public string? LastError { get; set; }

    internal NotificationOutboxEntry Clone()
    {
        var copy = (NotificationOutboxEntry)MemberwiseClone();
        copy.Event = Event.Copy();
        return copy;
    }
}

/// <summary>What the outbox holds and has given up on since the start, for the metrics. <paramref name="Unsaved"/>: a
/// change is not on disk yet (the file is unavailable, or its last write failed); it is retried every pass.</summary>
public sealed record NotificationOutboxStatus(int Pending, long Redelivered, long Exhausted, long Dropped, bool Unsaved = false);

/// <summary>
/// The bounded, durable outbox of critical notifications (R1-40, F-08): an event a channel still failed after the
/// composite's retries is kept in <c>notification-outbox.json</c> and redelivered to that channel by
/// <see cref="NotificationOutboxWorker"/> — also after a restart — with growing waits, until it is delivered, its channel
/// is switched off or removed, or it is exhausted (<see cref="NotificationOutbox.MaxAttempts"/> redeliveries or
/// <see cref="NotificationOutbox.MaxAge"/>). At most <see cref="NotificationOutbox.MaxEntries"/> are kept; a full outbox
/// drops its oldest entry. Giving up is logged and counted, never silent.
///
/// <para>Every change is made in memory, at once and without I/O, so a producer is never held up by the disk; the worker
/// writes it in <see cref="FlushAsync"/>. A change the file could not take stays pending and is written by the next
/// pass. A file that cannot be read for the moment is read again later — nothing is written over it meanwhile.</para>
/// </summary>
public interface INotificationOutbox
{
    /// <summary>Keeps <paramref name="evt"/> for redelivery to <paramref name="channel"/>. In memory, without waiting
    /// for the disk.</summary>
    Task AddAsync(string channel, NotificationEvent evt, Exception failure, DateTime now);

    /// <summary>The entries due at <paramref name="now"/>, oldest first. An entry past <see cref="NotificationOutbox.MaxAge"/>
    /// is given up on here, before it is sent.</summary>
    Task<IReadOnlyList<NotificationOutboxEntry>> DueAsync(DateTime now);

    /// <summary>The entry leaves the outbox: <paramref name="delivered"/> when it was sent, otherwise
    /// <paramref name="reason"/> says why it is dropped (its channel switched off or removed); a drop is counted.</summary>
    Task CompleteAsync(string id, bool delivered, string? reason = null);

    /// <summary>Another redelivery failed. Returns <c>true</c> when the entry is exhausted and was removed.</summary>
    Task<bool> FailedAsync(string id, Exception failure, DateTime now);

    /// <summary>Reads the file if it was not read yet and writes what changed since the last successful write. Returns
    /// <c>false</c> when something could not be read or written; it is retried by the next call.</summary>
    Task<bool> FlushAsync(CancellationToken ct = default);

    /// <summary>Waits until something was added or <paramref name="max"/> passed.</summary>
    Task WaitForWorkAsync(TimeSpan max, CancellationToken ct);

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
    private readonly object _sync = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly SemaphoreSlim _work = new(0, int.MaxValue);
    // Null until the file was read: entries added before that wait in _incoming and are merged when it is.
    private List<NotificationOutboxEntry>? _entries;
    private readonly List<NotificationOutboxEntry> _incoming = [];
    private bool _dirty;
    private long _redelivered, _exhausted, _dropped;

    public NotificationOutbox(DataPathOptions paths, ILogger<NotificationOutbox> logger)
        : this(new JsonFileStore<OutboxFile>(paths.NotificationOutboxJson), logger) { }

    internal NotificationOutbox(JsonFileStore<OutboxFile>? file, ILogger<NotificationOutbox> logger)
    {
        _file = file;
        _logger = logger;
        if (file is null) _entries = [];
    }

    /// <summary>Kept in memory only (tests).</summary>
    internal static NotificationOutbox InMemory(ILogger<NotificationOutbox> logger) => new((JsonFileStore<OutboxFile>?)null, logger);

    public NotificationOutboxStatus Status
    {
        get
        {
            lock (_sync)
                return new((_entries?.Count ?? 0) + _incoming.Count, Interlocked.Read(ref _redelivered),
                    Interlocked.Read(ref _exhausted), Interlocked.Read(ref _dropped), _dirty && _file is not null);
        }
    }

    public Task AddAsync(string channel, NotificationEvent evt, Exception failure, DateTime now)
    {
        lock (_sync)
        {
            var target = _entries ?? _incoming;
            while ((_entries?.Count ?? 0) + _incoming.Count >= MaxEntries)
                Drop(_entries is { Count: > 0 } ? _entries : _incoming, 0, "the outbox is full");
            target.Add(new NotificationOutboxEntry
            {
                Channel = channel,
                Event = evt.Copy(),
                Attempts = 0,
                FirstFailedAt = now,
                NextAttemptAt = now + WaitAfter(1),
                LastError = Describe(failure),
            });
            _dirty = true;
        }
        _work.Release();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NotificationOutboxEntry>> DueAsync(DateTime now)
    {
        lock (_sync)
        {
            if (_entries is null) return Task.FromResult<IReadOnlyList<NotificationOutboxEntry>>([]);
            // Too old to be of use: given up on before it is sent (a stale approval request, an alert from yesterday).
            foreach (var old in _entries.Where(e => now - e.FirstFailedAt >= MaxAge).ToList())
            {
                _entries.Remove(old);
                Exhausted(old, "it is older than " + MaxAge.TotalHours + " h");
            }
            return Task.FromResult<IReadOnlyList<NotificationOutboxEntry>>(
                _entries.Where(e => e.NextAttemptAt <= now).Select(e => e.Clone()).ToList());
        }
    }

    public Task CompleteAsync(string id, bool delivered, string? reason = null)
    {
        lock (_sync)
        {
            var index = _entries?.FindIndex(e => e.Id == id) ?? -1;
            if (index < 0) return Task.CompletedTask;
            if (delivered)
            {
                _entries!.RemoveAt(index);
                Interlocked.Increment(ref _redelivered);
                _dirty = true;
            }
            else Drop(_entries!, index, reason ?? "it has nothing to deliver any more");
        }
        return Task.CompletedTask;
    }

    public Task<bool> FailedAsync(string id, Exception failure, DateTime now)
    {
        lock (_sync)
        {
            var entry = _entries?.FirstOrDefault(e => e.Id == id);
            if (entry is null) return Task.FromResult(false);
            entry.Attempts++;
            entry.LastError = Describe(failure);
            _dirty = true;
            var exhausted = entry.Attempts >= MaxAttempts || now - entry.FirstFailedAt >= MaxAge;
            if (exhausted)
            {
                _entries!.Remove(entry);
                Exhausted(entry, $"after {entry.Attempts} redeliveries");
            }
            else entry.NextAttemptAt = now + WaitAfter(entry.Attempts + 1);
            return Task.FromResult(exhausted);
        }
    }

    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        if (_file is null) return true;
        await _io.WaitAsync(ct);
        try
        {
            bool loaded;
            lock (_sync) loaded = _entries is not null;
            if (!loaded && !await LoadAsync()) return false;

            OutboxFile snapshot;
            lock (_sync)
            {
                if (!_dirty) return true;
                snapshot = new OutboxFile { Entries = _entries!.Select(e => e.Clone()).ToList() };
                _dirty = false;
            }
            try
            {
                await _file.SaveAsync(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                // Still in memory and still to be written: the next pass tries again. Lost only if Whiskers stops first.
                lock (_sync) _dirty = true;
                _logger.LogError("The notification outbox could not be saved; it is retried ({Error})", Describe(ex));
                return false;
            }
        }
        finally { _io.Release(); }
    }

    public async Task WaitForWorkAsync(TimeSpan max, CancellationToken ct)
    {
        if (await _work.WaitAsync(max, ct))
            while (_work.CurrentCount > 0 && _work.Wait(0)) { }   // one pass serves every addition so far
    }

    /// <summary>Reads the file once. A file that cannot be read <i>now</i> (permissions, a lock, a volume away) is read
    /// again by the next pass and is never written over meanwhile; a file that is corrupt with no readable previous
    /// generation has been preserved by the store and is replaced.</summary>
    private async Task<bool> LoadAsync()
    {
        List<NotificationOutboxEntry> loaded;
        try
        {
            loaded = (await _file!.LoadAsync()).Entries ?? [];
        }
        catch (JsonStoreCorruptException ex)
        {
            _logger.LogError("The notification outbox is corrupt and has no readable previous generation; its undelivered " +
                             "notifications are lost. {Detail}", ex.Message);
            loaded = [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The notification outbox cannot be read right now; it is read again on the next pass ({Error})",
                Describe(ex));
            return false;
        }

        lock (_sync)
        {
            var merged = loaded.Concat(_incoming).OrderBy(e => e.FirstFailedAt).ToList();
            _incoming.Clear();
            _entries = merged;
            // Bounded also when the file was larger (edited, or written by a later version with a higher bound).
            while (_entries.Count > MaxEntries) Drop(_entries, 0, "the outbox is full");
            _dirty = true;
        }
        return true;
    }

    /// <summary>Removes and counts one entry that leaves undelivered; said, never silent. Under <see cref="_sync"/>.</summary>
    private void Drop(List<NotificationOutboxEntry> from, int index, string reason)
    {
        var entry = from[index];
        from.RemoveAt(index);
        _dirty = true;
        Interlocked.Increment(ref _dropped);
        _logger.LogError("Undelivered {EventType} for {Channel} dropped: {Reason}", entry.Event.EventType, entry.Channel, reason);
    }

    private void Exhausted(NotificationOutboxEntry entry, string why)
    {
        _dirty = true;
        Interlocked.Increment(ref _exhausted);
        _logger.LogError("Notification {EventType} could not be delivered to {Channel}: gave up {Why}, first failed " +
                         "{First:u} (last error: {Error})", entry.Event.EventType, entry.Channel, why, entry.FirstFailedAt,
            entry.LastError);
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

    /// <summary>The file's shape.</summary>
    public sealed class OutboxFile
    {
        public List<NotificationOutboxEntry>? Entries { get; set; } = [];
    }
}
