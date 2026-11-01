using System.Collections.Concurrent;
using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Services.ImageUpdate;

/// <summary>
/// What the image-update checker knows, per container.
///
/// <para><b>Persisted since 2026-08-28.</b> This lived only in memory, and with a six-hour check interval
/// that meant every restart left Whiskers answering "no updates available" for hours — not because there were
/// none, but because it had not looked yet. On a day with ten deploys it never got to look at all. An empty
/// answer that is indistinguishable from a checked one is the failure this whole project keeps removing.</para>
/// </summary>
public class ImageUpdateStore : IImageUpdateStore
{
    private readonly ConcurrentDictionary<string, ImageUpdateInfo> _updates = new();
    private readonly string _persistPath;
    private readonly ILogger<ImageUpdateStore>? _logger;

    public DateTime? LastCheckAt { get; set; }
    public bool IsChecking { get; set; }

    private sealed class PersistModel
    {
        public Dictionary<string, ImageUpdateInfo> Updates { get; set; } = new();
        public DateTime? LastCheckAt { get; set; }
    }

    public ImageUpdateStore(ILogger<ImageUpdateStore>? logger = null, DataPathOptions? dataPaths = null)
    {
        _logger = logger;
        _persistPath = (dataPaths ?? DataPathOptions.Default).ImageUpdatesJson;
        try
        {
            if (!File.Exists(_persistPath)) return;
            var model = JsonSerializer.Deserialize<PersistModel>(File.ReadAllText(_persistPath));
            if (model is null) return;
            foreach (var kv in model.Updates) _updates[kv.Key] = kv.Value;
            LastCheckAt = model.LastCheckAt;
            _logger?.LogInformation(
                "Loaded persisted image-update state: {Count} container(s), last checked {Last}",
                _updates.Count, LastCheckAt);
        }
        catch (Exception ex)
        {
            // Starting empty is correct here — but it must be said, or the next answer looks checked.
            _logger?.LogWarning(ex, "Could not load persisted image-update state — starting empty");
        }
    }

    /// <summary>Writes the current state so a restart does not erase what has already been checked.</summary>
    public async Task SaveAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var model = new PersistModel
            {
                Updates = new Dictionary<string, ImageUpdateInfo>(_updates),
                LastCheckAt = LastCheckAt
            };
            await Whiskers.Services.Persistence.DurableFile.WriteAllTextAsync(_persistPath, JsonSerializer.Serialize(model));   // never half written (R1-35)
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not persist image-update state");
        }
    }

    /// <summary>Key = serverId:containerId</summary>
    public void Set(ImageUpdateInfo info)
    {
        var key = $"{info.ServerId}:{info.ContainerId}";
        _updates[key] = info;
    }

    public ImageUpdateInfo? Get(string containerId, string? serverId = null)
    {
        var key = $"{serverId ?? "local"}:{containerId}";
        return _updates.TryGetValue(key, out var info) ? info : null;
    }

    public IReadOnlyList<ImageUpdateInfo> GetUpdatesForServer(string serverId)
        => _updates.Values.Where(u => u.ServerId == serverId).ToList();

    public IReadOnlyList<ImageUpdateInfo> GetAllPendingUpdates()
        => _updates.Values.Where(u => u.UpdateAvailable).ToList();

    public IReadOnlyList<ImageUpdateInfo> GetAll()
        => _updates.Values.ToList();

    public void Remove(string containerId, string? serverId = null)
    {
        var key = $"{serverId ?? "local"}:{containerId}";
        _updates.TryRemove(key, out _);
    }

    public void Clear() => _updates.Clear();
}
