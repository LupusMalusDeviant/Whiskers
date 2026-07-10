using Docker.DotNet;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Docker.Operations;

/// <summary>
/// Docker network operations (list/create/remove/connect/disconnect) for the <see cref="DockerService"/> facade.
/// </summary>
internal sealed class NetworkOperations
{
    private readonly IDockerConnectionManager _connectionManager;
    private readonly IServerConfigService _serverConfigService;

    public NetworkOperations(
        IDockerConnectionManager connectionManager,
        IServerConfigService serverConfigService)
    {
        _connectionManager = connectionManager;
        _serverConfigService = serverConfigService;
    }

    private async Task<DockerClient> GetClient(string? serverId)
        => await _connectionManager.GetClientAsync(serverId);

    public async Task<IList<NetworkInfo>> ListNetworksAsync(string? serverId = null)
    {
        var server = serverId != null
            ? _serverConfigService.GetServer(serverId)
            : _serverConfigService.GetDefaultServer();
        var client = await GetClient(serverId);
        var networks = await client.Networks.ListNetworksAsync();

        return networks.Select(n =>
        {
            var info = new NetworkInfo
            {
                Id = n.ID,
                Name = n.Name,
                Driver = n.Driver ?? "",
                Scope = n.Scope ?? "",
                Internal = n.Internal,
                ServerId = server?.Id ?? "local",
                ServerName = server?.Name ?? "Local"
            };

            if (n.IPAM?.Config?.Any() == true)
            {
                var ipamConfig = n.IPAM.Config.First();
                info.Subnet = ipamConfig.Subnet ?? "";
                info.Gateway = ipamConfig.Gateway ?? "";
            }

            if (n.Containers != null)
            {
                info.Containers = n.Containers.Select(c => new NetworkContainer
                {
                    ContainerId = c.Key,
                    Name = c.Value.Name ?? "",
                    IPv4Address = c.Value.IPv4Address ?? ""
                }).ToList();
            }

            return info;
        }).ToList();
    }

    public async Task<string> CreateNetworkAsync(string name, string driver = "bridge", string? serverId = null)
    {
        var client = await GetClient(serverId);
        var response = await client.Networks.CreateNetworkAsync(new global::Docker.DotNet.Models.NetworksCreateParameters
        {
            Name = name,
            Driver = driver
        });
        return response.ID;
    }

    public async Task RemoveNetworkAsync(string networkId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Networks.DeleteNetworkAsync(networkId);
    }

    public async Task ConnectContainerToNetworkAsync(string networkId, string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Networks.ConnectNetworkAsync(networkId, new global::Docker.DotNet.Models.NetworkConnectParameters
        {
            Container = containerId
        });
    }

    public async Task DisconnectContainerFromNetworkAsync(string networkId, string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Networks.DisconnectNetworkAsync(networkId, new global::Docker.DotNet.Models.NetworkDisconnectParameters
        {
            Container = containerId
        });
    }
}
