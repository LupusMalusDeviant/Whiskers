using System.Collections.Concurrent;
using ServerWatch.Models;

namespace ServerWatch.Services.ImageUpdate;

public class ImageUpdateStore : IImageUpdateStore
{
    private readonly ConcurrentDictionary<string, ImageUpdateInfo> _updates = new();
    public DateTime? LastCheckAt { get; set; }
    public bool IsChecking { get; set; }

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
