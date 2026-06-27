using ServerWatch.Models;

namespace ServerWatch.Services.ImageUpdate;

/// <summary>In-memory store of detected container image updates.</summary>
public interface IImageUpdateStore
{
    DateTime? LastCheckAt { get; set; }
    bool IsChecking { get; set; }
    void Set(ImageUpdateInfo info);
    ImageUpdateInfo? Get(string containerId, string? serverId = null);
    IReadOnlyList<ImageUpdateInfo> GetUpdatesForServer(string serverId);
    IReadOnlyList<ImageUpdateInfo> GetAllPendingUpdates();
    IReadOnlyList<ImageUpdateInfo> GetAll();
    void Remove(string containerId, string? serverId = null);
    void Clear();
}
