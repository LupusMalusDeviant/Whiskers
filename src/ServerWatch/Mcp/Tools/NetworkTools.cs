using ModelContextProtocol.Server;
using System.ComponentModel;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace ServerWatch.Mcp.Tools;

[McpServerToolType]
public class NetworkTools
{
    [McpServerTool, Description("List all Docker networks on a server. Shows name, driver, scope, subnet, and connected containers.")]
    public static async Task<string> ListNetworks(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Server ID (optional, defaults to local)")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_networks");
        if (denied != null) return denied;

        var networks = await docker.ListNetworksAsync(serverId);
        if (!networks.Any()) return "No networks found.";

        var lines = networks.Select(n =>
        {
            var containers = n.Containers.Any()
                ? $" | Containers: {string.Join(", ", n.Containers.Select(c => c.Name))}"
                : "";
            return $"- {n.Name} (Driver: {n.Driver}, Scope: {n.Scope}, Subnet: {(string.IsNullOrEmpty(n.Subnet) ? "n/a" : n.Subnet)}){containers}";
        });

        return $"Found {networks.Count} networks on {networks.FirstOrDefault()?.ServerName ?? "local"}:\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Create a new Docker network.")]
    public static async Task<string> CreateNetwork(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Network name")] string name,
        [Description("Network driver: bridge, overlay, macvlan (default: bridge)")] string driver = "bridge",
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "create_network");
        if (denied != null) return denied;

        if (string.IsNullOrWhiteSpace(name))
            return "Network name is required.";

        var id = await docker.CreateNetworkAsync(name, driver, serverId);
        return $"Network '{name}' created (ID: {id[..12]}, Driver: {driver}).";
    }

    [McpServerTool, Description("Remove a Docker network by name or ID.")]
    public static async Task<string> RemoveNetwork(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Network name or ID")] string networkId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "remove_network");
        if (denied != null) return denied;

        await docker.RemoveNetworkAsync(networkId, serverId);
        return $"Network '{networkId}' removed.";
    }

    [McpServerTool, Description("Connect a container to a Docker network.")]
    public static async Task<string> ConnectContainerToNetwork(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Network name or ID")] string networkId,
        [Description("Container name or ID")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "connect_container_to_network");
        if (denied != null) return denied;

        await docker.ConnectContainerToNetworkAsync(networkId, containerId, serverId);
        return $"Container '{containerId}' connected to network '{networkId}'.";
    }

    [McpServerTool, Description("Disconnect a container from a Docker network.")]
    public static async Task<string> DisconnectContainerFromNetwork(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Network name or ID")] string networkId,
        [Description("Container name or ID")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "disconnect_container_from_network");
        if (denied != null) return denied;

        await docker.DisconnectContainerFromNetworkAsync(networkId, containerId, serverId);
        return $"Container '{containerId}' disconnected from network '{networkId}'.";
    }
}
