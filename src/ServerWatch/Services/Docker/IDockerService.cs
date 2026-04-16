using ServerWatch.Models;

namespace ServerWatch.Services.Docker;

public interface IDockerService
{
    Task<IList<ContainerInfo>> ListContainersAsync(bool all = true, string? serverId = null);
    Task<IList<ContainerInfo>> ListAllContainersAsync(bool all = true);
    Task<ContainerStats?> GetContainerStatsAsync(string containerId, string? serverId = null);
    Task StartContainerAsync(string containerId, string? serverId = null);
    Task StopContainerAsync(string containerId, string? serverId = null);
    Task RestartContainerAsync(string containerId, string? serverId = null);
    Task RemoveContainerAsync(string containerId, bool force = false, string? serverId = null);
    Task<string> GetContainerLogsAsync(string containerId, int tailLines = 100, string? serverId = null);
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

    // Resource limits
    Task<(long NanoCpus, long MemoryBytes, long MemoryReservation)> GetContainerResourceLimitsAsync(string containerId, string? serverId = null);
    Task UpdateContainerResourcesAsync(string containerId, double? cpuLimit, long? memoryLimitBytes, string? serverId = null);

    // Container exec
    Task<(string StdOut, string StdErr, int ExitCode)> ExecInContainerAsync(string containerId, string[] command, string? serverId = null, TimeSpan? timeout = null);
}
