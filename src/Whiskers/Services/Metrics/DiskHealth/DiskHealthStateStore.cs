using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics.DiskHealth;

/// <summary>R1-58: what the disk-health monitor keeps across a restart.</summary>
public sealed class DiskHealthStateFile
{
    /// <summary>The last reading per server — what the next reading compares with (an item gone since is Unknown).</summary>
    public List<DiskHealthReading> Readings { get; set; } = [];

    /// <summary>The disk-health alerts raised and not yet closed (and the last end of each, as R1-56 keeps them).</summary>
    public List<BreachState> Breaches { get; set; } = [];

    /// <summary>The items that read Healthy or DegradedOrFailing and are Unknown now (what arms <c>disk_health_unknown</c>).</summary>
    public List<DiskHealthLostItem> Lost { get; set; } = [];
}

/// <summary>
/// R1-58: keeps the last disk-health readings and open alerts in <c>disk-health.json</c>, so a restart neither forgets a
/// disk that went missing (it stays Unknown) nor raises an open alert a second time. Local to the install, like
/// <c>alert-breaches.json</c> (not part of a self-backup).
/// </summary>
public interface IDiskHealthStateStore
{
    /// <summary>The saved state; an empty one when there is none. Throws when the file cannot be read.</summary>
    Task<DiskHealthStateFile> LoadAsync();

    /// <summary>Replaces the saved state.</summary>
    Task SaveAsync(DiskHealthStateFile state);
}

/// <inheritdoc cref="IDiskHealthStateStore"/>
public sealed class DiskHealthStateStore(string path) : IDiskHealthStateStore
{
    private readonly JsonFileStore<DiskHealthStateFile> _file = new(path,
        problem: f => f.Readings is null || f.Breaches is null || f.Lost is null || f.Lost.Any(l => l is null)
                      || f.Readings.Any(r => r is null || r.Items is null || r.ServerId is null || r.Items.Any(i => i is null))
                      || f.Breaches.Any(b => b is null)
            ? "a list is missing or holds an empty entry" : null);

    public Task<DiskHealthStateFile> LoadAsync() => _file.LoadAsync();

    public Task SaveAsync(DiskHealthStateFile state) => _file.SaveAsync(state);
}
