namespace Whiskers.Services.Mutations;

/// <summary>
/// One lock per resource, by key, shared by everything that mutates it in this process (R1-10, INV-42): webhook runs
/// and Git deployments take the lock of the compose project they deploy (<see cref="ComposeProject"/>), so deployments
/// of one project never overlap whichever path starts them; container actions and deployments both take the lock of
/// their server's containers (<see cref="Containers"/>), a deployment after its project's; a deployment from a Git
/// workspace takes the workspace (<see cref="Workspace"/>) before all of them — always in the order workspace,
/// project, containers. A lock exists only while it is held or awaited: the table
/// does not grow with resources that came and went (a container rebuilt under a new id).
/// </summary>
public sealed class ResourceLocks
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    /// <summary>The key of a compose project on a server — compose's own identity of a deployment.</summary>
    public static string ComposeProject(string serverId, string projectName) => $"compose-project:{serverId}|{projectName}";

    /// <summary>The key of a deployment workspace (a Git app's checkout) on a server: held while it is written to and
    /// while a deployment reads its compose files.</summary>
    public static string Workspace(string serverId, string directory) => $"workspace:{serverId}|{directory}";

    /// <summary>The key of a server's containers: held by every webhook container action and every deployment on it.</summary>
    public static string Containers(string serverId) => $"containers:{serverId}";

    /// <summary>Both leases as one, released in the reverse order.</summary>
    public static Lease Both(Lease first, Lease second) => new(() =>
    {
        second.Dispose();
        first.Dispose();
    });

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
