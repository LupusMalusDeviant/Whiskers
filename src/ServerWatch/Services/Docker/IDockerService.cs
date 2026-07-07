using ServerWatch.Models;

namespace ServerWatch.Services.Docker;

public interface IDockerService
{
    Task<IList<ContainerInfo>> ListContainersAsync(bool all = true, string? serverId = null);
    Task<IList<ContainerInfo>> ListAllContainersAsync(bool all = true);
    Task<ContainerInfo?> GetContainerAsync(string id, string? serverId = null);
    Task<ContainerStats?> GetContainerStatsAsync(string containerId, string? serverId = null);
    Task StartContainerAsync(string containerId, string? serverId = null);
    Task StopContainerAsync(string containerId, string? serverId = null);
    Task RestartContainerAsync(string containerId, string? serverId = null);
    Task RemoveContainerAsync(string containerId, bool force = false, string? serverId = null);
    Task<string> GetContainerLogsAsync(string containerId, int tailLines = 100, string? serverId = null, DateTime? since = null);
    Task<string> CreateContainerAsync(DeploymentRequest request, string? serverId = null);
    Task PullImageAsync(string imageName, IProgress<string>? progress = null, string? serverId = null);
    Task<(string State, int ExitCode, bool OomKilled)> InspectContainerStateAsync(string containerId, string? serverId = null);
    Task<ServerSystemInfo> GetServerSystemInfoAsync(string? serverId = null);
    Task<Dictionary<string, ServerSystemInfo>> GetAllServerSystemInfoAsync();
    Task<string?> GetImageDigestAsync(string imageRef, string? serverId = null);
    Task<string> RecreateContainerAsync(string containerId, string? serverId = null, IProgress<string>? progress = null);
    Task<List<KeyValuePair<string, string>>> GetContainerEnvAsync(string containerId, string? serverId = null);

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
