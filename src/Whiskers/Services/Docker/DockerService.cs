using Microsoft.Extensions.Caching.Memory;
using Whiskers.Models;
using Whiskers.Services.Docker.Operations;
using Whiskers.Services.Metrics;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Docker;

/// <summary>
/// The main Docker operations surface, implemented as a thin facade that delegates to internal
/// collaborator classes in <c>Operations/</c> (containers, lifecycle/rollback, images, networks,
/// host shell, system info).
/// </summary>
public class DockerService : IDockerService
{
    private readonly ContainerOperations _containers;
    private readonly ContainerLifecycleOperations _lifecycle;
    private readonly ImageOperations _images;
    private readonly NetworkOperations _networks;
    private readonly HostShellOperations _hostShell;
    private readonly SystemInfoOperations _systemInfo;

    public DockerService(
        IDockerConnectionManager connectionManager,
        IServerConfigService serverConfigService,
        IPrometheusMetricsSource prometheusMetrics,
        ILogger<DockerService> logger,
        // F8: optional last param (test seams unchanged) — registry credentials for authenticated
        // pulls of UI-managed private registries.
        Whiskers.Services.Registries.IRegistryConfigService? registryConfig = null)
    {
        // One shared cache across the collaborators (stats, host-shell sweep/image markers, host
        // resource usage) — same single MemoryCache instance the pre-split class used; the key
        // prefixes ("stats:", "hostshellsweep:", "hostshellimg:", "hostres:") keep entries apart.
        var statsCache = new MemoryCache(new MemoryCacheOptions());

        _containers = new ContainerOperations(connectionManager, serverConfigService, logger, statsCache);
        _images = new ImageOperations(connectionManager, logger, registryConfig);
        _lifecycle = new ContainerLifecycleOperations(connectionManager, _images, logger);
        _networks = new NetworkOperations(connectionManager, serverConfigService);
        _hostShell = new HostShellOperations(connectionManager, statsCache);
        _systemInfo = new SystemInfoOperations(connectionManager, serverConfigService, prometheusMetrics, _containers, logger, statsCache);
    }

    // === Containers ===

    public Task<IList<ContainerInfo>> ListContainersAsync(bool all = true, string? serverId = null)
        => _containers.ListContainersAsync(all, serverId);

    public Task<IList<ContainerInfo>> ListAllContainersAsync(bool all = true)
        => _containers.ListAllContainersAsync(all);

    public Task<ContainerInfo?> GetContainerAsync(string id, string? serverId = null)
        => _containers.GetContainerAsync(id, serverId);

    public Task<ContainerStats?> GetContainerStatsAsync(string containerId, string? serverId = null)
        => _containers.GetContainerStatsAsync(containerId, serverId);

    public Task StartContainerAsync(string containerId, string? serverId = null)
        => _containers.StartContainerAsync(containerId, serverId);

    public Task StopContainerAsync(string containerId, string? serverId = null)
        => _containers.StopContainerAsync(containerId, serverId);

    public Task RestartContainerAsync(string containerId, string? serverId = null)
        => _containers.RestartContainerAsync(containerId, serverId);

    public Task RemoveContainerAsync(string containerId, bool force = false, string? serverId = null)
        => _containers.RemoveContainerAsync(containerId, force, serverId);

    public Task<string> GetContainerLogsAsync(string containerId, int tailLines = 100, string? serverId = null, DateTime? since = null)
        => _containers.GetContainerLogsAsync(containerId, tailLines, serverId, since);

    public Task<(string State, int ExitCode, bool OomKilled)> InspectContainerStateAsync(string containerId, string? serverId = null)
        => _containers.InspectContainerStateAsync(containerId, serverId);

    public Task<List<KeyValuePair<string, string>>> GetContainerEnvAsync(string containerId, string? serverId = null)
        => _containers.GetContainerEnvAsync(containerId, serverId);

    // === Lifecycle (create/recreate + C12 update-rollback) ===

    public Task<string> CreateContainerAsync(DeploymentRequest request, string? serverId = null)
        => _lifecycle.CreateContainerAsync(request, serverId);

    public Task<string> RecreateContainerAsync(string containerId, string? serverId = null, IProgress<string>? progress = null)
        => _lifecycle.RecreateContainerAsync(containerId, serverId, progress);

    public Task<(string ImageId, string ConfigJson)> CaptureRollbackSnapshotAsync(string containerId, string? serverId = null)
        => _lifecycle.CaptureRollbackSnapshotAsync(containerId, serverId);

    public Task<string> RollbackContainerAsync(string containerName, string imageId, string configJson, string? serverId = null, IProgress<string>? progress = null)
        => _lifecycle.RollbackContainerAsync(containerName, imageId, configJson, serverId, progress);

    // === Images ===

    public Task PullImageAsync(string imageName, IProgress<string>? progress = null, string? serverId = null)
        => _images.PullImageAsync(imageName, progress, serverId);

    public Task<string?> GetImageDigestAsync(string imageRef, string? serverId = null)
        => _images.GetImageDigestAsync(imageRef, serverId);

    // === Networks ===

    public Task<IList<NetworkInfo>> ListNetworksAsync(string? serverId = null)
        => _networks.ListNetworksAsync(serverId);

    public Task<string> CreateNetworkAsync(string name, string driver = "bridge", string? serverId = null)
        => _networks.CreateNetworkAsync(name, driver, serverId);

    public Task RemoveNetworkAsync(string networkId, string? serverId = null)
        => _networks.RemoveNetworkAsync(networkId, serverId);

    public Task ConnectContainerToNetworkAsync(string networkId, string containerId, string? serverId = null)
        => _networks.ConnectContainerToNetworkAsync(networkId, containerId, serverId);

    public Task DisconnectContainerFromNetworkAsync(string networkId, string containerId, string? serverId = null)
        => _networks.DisconnectContainerFromNetworkAsync(networkId, containerId, serverId);

    // === Host shell ===

    public Task<(string Output, string Error, int ExitCode)> RunHostShellAsync(string command, string? serverId = null, TimeSpan? timeout = null)
        => _hostShell.RunHostShellAsync(command, serverId, timeout);

    // === System info ===

    public Task<ServerSystemInfo> GetServerSystemInfoAsync(string? serverId = null)
        => _systemInfo.GetServerSystemInfoAsync(serverId);

    public Task<Dictionary<string, ServerSystemInfo>> GetAllServerSystemInfoAsync()
        => _systemInfo.GetAllServerSystemInfoAsync();
}
