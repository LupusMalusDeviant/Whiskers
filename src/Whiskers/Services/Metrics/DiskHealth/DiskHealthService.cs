using System.Collections.Concurrent;

namespace Whiskers.Services.Metrics.DiskHealth;

/// <summary>R1-58 (contract 0005): the read side of disk health, for the UI and the alerts. Holds the latest reading per
/// server as <see cref="DiskHealthMonitor"/> last made it; empty while the feature is off.</summary>
public interface IDiskHealthService
{
    /// <summary>The latest reading of <paramref name="serverId"/>, or null when none was made (feature off, not read yet).</summary>
    DiskHealthReading? Latest(string serverId);

    /// <summary>Every latest reading by server id.</summary>
    IReadOnlyDictionary<string, DiskHealthReading> All();
}

/// <inheritdoc cref="IDiskHealthService"/>
public sealed class DiskHealthService : IDiskHealthService
{
    private readonly ConcurrentDictionary<string, DiskHealthReading> _latest = new(StringComparer.Ordinal);

    public DiskHealthReading? Latest(string serverId) => _latest.TryGetValue(serverId, out var r) ? r : null;

    public IReadOnlyDictionary<string, DiskHealthReading> All() => new Dictionary<string, DiskHealthReading>(_latest, StringComparer.Ordinal);

    /// <summary>The monitor publishes a reading.</summary>
    internal void Set(DiskHealthReading reading) => _latest[reading.ServerId] = reading;

    /// <summary>The monitor drops a server it no longer reads.</summary>
    internal void Remove(string serverId) => _latest.TryRemove(serverId, out _);

    /// <summary>The monitor forgets everything (the feature was switched off).</summary>
    internal void Clear() => _latest.Clear();
}
