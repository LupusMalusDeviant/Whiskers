using System.Collections.Immutable;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Mutations;

/// <summary>
/// One lock per resource, by key, shared by everything that mutates it in this process (R1-10, INV-42). Webhook
/// container actions, compose webhooks and Git deployments (from the checkout to `up`) all hold the lock of their
/// server (<see cref="Server"/>): whatever names a container, a compose project or a checkout — an id, a symlink, a
/// compose file elsewhere — two of them never act on one server at the same time. A lock exists only while it is held
/// or awaited: the table does not grow with resources that came and went. A server is locked by its id
/// (<see cref="ServerKey"/>): every alias execution accepts — the id in another case, the display name — is one lock.
/// </summary>
public sealed class ResourceLocks(IServerConfigService? servers = null)
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly AsyncLocal<ImmutableHashSet<string>?> _held = new();

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    /// <summary>The key of a server: held by every webhook container action and every deployment on it.</summary>
    public static string Server(string serverId) => $"server:{serverId}";

    /// <summary>The key of the server a stored id or alias names, resolved as execution resolves it (the id, the id in
    /// another case, the display name). An id no server has stays as it is — nothing runs on it.</summary>
    public string ServerKey(string serverId) => Server(servers?.GetServer(serverId)?.Id ?? serverId);

    /// <summary>Marks the calling flow — and the work it starts from now on — as the holder of <paramref name="key"/>,
    /// whose lease it has: an <see cref="AcquireAsync"/> for it from there (a deployment the run starts) does not
    /// wait for its own lock. Called by the method that holds the lease, which keeps it until that work has ended.
    /// </summary>
    public void Holding(string key) =>
        _held.Value = (_held.Value ?? ImmutableHashSet.Create<string>(StringComparer.Ordinal)).Add(key);

    /// <summary>Waits for the resource's lock; the lease releases it. A cancelled wait holds nothing.</summary>
    public async Task<Lease> AcquireAsync(string key, CancellationToken ct = default)
    {
        if (_held.Value?.Contains(key) == true) return new Lease(() => { });
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
