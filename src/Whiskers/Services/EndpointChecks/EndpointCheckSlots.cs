namespace Whiskers.Services.EndpointChecks;

/// <summary>
/// R1-57 (decision 5, review m1): the run slots every path takes before it runs a check — the monitor and "Run now"
/// alike — so at most <see cref="MaxConcurrent"/> runs are in flight across the fleet, at most one per server, and a
/// check never runs twice at once.
/// </summary>
public interface IEndpointCheckSlots
{
    /// <summary>Takes the slot of <paramref name="checkId"/> on <paramref name="serverId"/>; false when the check is
    /// running, its server is busy or the fleet-wide limit is reached. A true answer must be paired with
    /// <see cref="Release"/>.</summary>
    bool TryAcquire(string checkId, string serverId);

    /// <summary>Gives the slot of <paramref name="checkId"/> back; nothing when it holds none.</summary>
    void Release(string checkId);

    /// <summary>A snapshot of the running checks: check id → server id.</summary>
    IReadOnlyDictionary<string, string> Running { get; }
}

/// <inheritdoc cref="IEndpointCheckSlots"/>
public sealed class EndpointCheckSlots : IEndpointCheckSlots
{
    /// <summary>The most runs in flight across the fleet.</summary>
    public const int MaxConcurrent = 4;

    private readonly object _sync = new();
    private readonly Dictionary<string, string> _running = new(StringComparer.Ordinal);

    public bool TryAcquire(string checkId, string serverId)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkId);
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        lock (_sync)
        {
            if (_running.Count >= MaxConcurrent || _running.ContainsKey(checkId)
                || _running.ContainsValue(serverId)) return false;
            _running[checkId] = serverId;
            return true;
        }
    }

    public void Release(string checkId)
    {
        lock (_sync) _running.Remove(checkId);
    }

    public IReadOnlyDictionary<string, string> Running
    {
        get { lock (_sync) return new Dictionary<string, string>(_running, StringComparer.Ordinal); }
    }
}
