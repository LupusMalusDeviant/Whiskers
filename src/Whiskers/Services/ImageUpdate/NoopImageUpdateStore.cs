using Whiskers.Models;

namespace Whiskers.Services.ImageUpdate;

/// <summary>The Core's default <see cref="IImageUpdateStore"/> for when the ImageUpdate module is off. The
/// store is consumed by Core: <c>ContainerTools</c> (the <c>check_updates</c>/<c>update_container</c> MCP tools
/// live in that mixed, Core-resident class) and the Dashboard page (update counts) both inject it. So this
/// no-op keeps them resolvable — it holds no updates, so the Dashboard shows none and <c>check_updates</c>
/// reports nothing known. The real <see cref="ImageUpdateStore"/> wins by last-registration when the module is
/// enabled. Soft-dependency-via-no-op-Core-contract pattern (RoadToSAP §2.1).</summary>
public sealed class NoopImageUpdateStore : IImageUpdateStore
{
    public DateTime? LastCheckAt { get; set; }
    public bool IsChecking { get; set; }
    public void Set(ImageUpdateInfo info) { }
    public ImageUpdateInfo? Get(string containerId, string? serverId = null) => null;
    public IReadOnlyList<ImageUpdateInfo> GetUpdatesForServer(string serverId) => Array.Empty<ImageUpdateInfo>();
    public IReadOnlyList<ImageUpdateInfo> GetAllPendingUpdates() => Array.Empty<ImageUpdateInfo>();
    public IReadOnlyList<ImageUpdateInfo> GetAll() => Array.Empty<ImageUpdateInfo>();
    public void Remove(string containerId, string? serverId = null) { }
    public void Clear() { }
}
