using Whiskers.Models;

namespace Whiskers.Services.Docker;

public interface IDockerService
{
    Task<IList<ContainerInfo>> ListContainersAsync(bool all = true, string? serverId = null, CancellationToken ct = default);
    Task<IList<ContainerInfo>> ListAllContainersAsync(bool all = true);

    /// <summary>Fleet-wide list PLUS which servers answered. Callers that keep per-server state across
    /// cycles need that distinction — an unreachable host and an empty host look identical in the plain
    /// list. See <see cref="FleetContainerListing"/>.</summary>
    Task<FleetContainerListing> ListAllContainersDetailedAsync(bool all = true);
    Task<ContainerInfo?> GetContainerAsync(string id, string? serverId = null);
    Task<ContainerStats?> GetContainerStatsAsync(string containerId, string? serverId = null);
    Task StartContainerAsync(string containerId, string? serverId = null);
    Task StopContainerAsync(string containerId, string? serverId = null);
    Task RestartContainerAsync(string containerId, string? serverId = null);
    Task RemoveContainerAsync(string containerId, bool force = false, string? serverId = null);
    Task<string> GetContainerLogsAsync(string containerId, int tailLines = 100, string? serverId = null, DateTime? since = null, CancellationToken ct = default);
    Task<string> CreateContainerAsync(DeploymentRequest request, string? serverId = null);
    Task PullImageAsync(string imageName, IProgress<string>? progress = null, string? serverId = null);
    Task<(string State, int ExitCode, bool OomKilled)> InspectContainerStateAsync(string containerId, string? serverId = null);
    Task<ServerSystemInfo> GetServerSystemInfoAsync(string? serverId = null, CancellationToken ct = default);
    Task<Dictionary<string, ServerSystemInfo>> GetAllServerSystemInfoAsync();
    Task<string?> GetImageDigestAsync(string imageRef, string? serverId = null);

    /// <summary>The image reference a container was created from (its <c>Config.Image</c>), or null when it
    /// cannot be inspected. Needed because a listing shows a bare image ID once the tag has moved on.</summary>
    Task<string?> GetConfiguredImageAsync(string containerId, string? serverId = null);

    /// <summary>What an image declares about how it runs — for measuring update risk before recreating
    /// anything (GAP-6). Null when the image cannot be inspected.</summary>
    Task<ImageUpdate.ImageContract?> GetImageContractAsync(string imageRef, string? serverId = null);
    Task<string> RecreateContainerAsync(string containerId, string? serverId = null, IProgress<string>? progress = null);
    /// <summary>The container's log-driver configuration and log file path, or null when Docker reports
    /// none. Used by the log-hygiene inventory (Plan-0007 WP3).</summary>
    Task<ContainerLogConfiguration?> GetLogConfigurationAsync(string containerId, string? serverId = null);

    Task<List<KeyValuePair<string, string>>> GetContainerEnvAsync(string containerId, string? serverId = null);

    // C12 update-rollback: capture the pre-update snapshot (old image ID + full container config as JSON) so a
    // failed update can be undone; RollbackContainerAsync recreates the container from that snapshot's old image.
    Task<(string ImageId, string ConfigJson)> CaptureRollbackSnapshotAsync(string containerId, string? serverId = null);
    Task<string> RollbackContainerAsync(string containerName, string imageId, string configJson, string? serverId = null, IProgress<string>? progress = null);

    // Networks
    Task<IList<NetworkInfo>> ListNetworksAsync(string? serverId = null);
    Task<string> CreateNetworkAsync(string name, string driver = "bridge", string? serverId = null);
    Task RemoveNetworkAsync(string networkId, string? serverId = null);
    Task ConnectContainerToNetworkAsync(string networkId, string containerId, string? serverId = null);
    Task DisconnectContainerFromNetworkAsync(string networkId, string containerId, string? serverId = null);

    // Run a shell command on the HOST via a short-lived privileged nsenter container over the Docker
    // API. This is the SSH-free shell plane for TCP+mTLS servers — same effect as nsenter -t 1 locally,
    // but driven through the mTLS Docker connection instead of SSH.
    Task<(string Output, string Error, int ExitCode)> RunHostShellAsync(string command, string? serverId = null, TimeSpan? timeout = null);
}
