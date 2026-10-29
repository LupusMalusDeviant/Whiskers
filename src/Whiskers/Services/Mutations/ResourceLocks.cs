namespace Whiskers.Services.Mutations;

/// <summary>
/// One lock per resource, by key, shared by everything that mutates it in this process (R1-10, INV-42). Webhook
/// container actions, compose webhooks and Git deployments (from the checkout to `up`) all hold the lock of their
/// server (<see cref="Server"/>): whatever names a container, a compose project or a checkout — an id, a symlink, a
/// compose file elsewhere — two of them never act on one server at the same time. A lock exists only while it is held
/// or awaited: the table does not grow with resources that came and went.
/// </summary>
public sealed class ResourceLocks
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    /// <summary>The key of a server: held by every webhook container action and every deployment on it.</summary>
    public static string Server(string serverId) => $"server:{serverId}";


    /// <summary>Waits for the resource's lock; the lease releases it. A cancelled wait holds nothing.</summary>
    public async Task<Lease> AcquireAsync(string key, CancellationToken ct = default)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new Entry();
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }
        return new Lease(() =>
        {
            entry.Gate.Release();
            Leave(key, entry);
        });
    }

    /// <summary>How many resources are locked or awaited now.</summary>
    internal int Count
    {
        get { lock (_entries) return _entries.Count; }
    }

    private void Leave(string key, Entry entry)
    {
        lock (_entries)
            if (--entry.Users == 0) _entries.Remove(key);
    }

    /// <summary>A held lock; disposing it releases the lock once.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly Action _release;
        private int _released;

        internal Lease(Action release) => _release = release;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _release();
        }
    }
}
