using ModelContextProtocol.Server;
using Whiskers.Models;
using System.ComponentModel;
using Whiskers.Services.Docker;
using Whiskers.Services.Mcp;
using Whiskers.Services.AuditLog;
using Microsoft.AspNetCore.Http;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;

namespace Whiskers.Mcp.Tools;

[McpServerToolType]
public class NetworkTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List all Docker networks on a server. Shows name, driver, scope, subnet, and connected containers.")]
    public static async Task<string> ListNetworks(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        [Description("Server ID (optional, defaults to local)")] string? serverId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_networks");
        if (denied != null) return denied;

        var networks = await docker.ListNetworksAsync(serverId, cancellationToken);
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

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Create a new Docker network.")]
    public static async Task<string> CreateNetwork(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IToolAuditLog auditLog,
        IServiceProvider services,
        [Description("Network name")] string name,
        [Description("Network driver: bridge (default), overlay, macvlan, ipvlan. macvlan, ipvlan and any other driver put containers on the host's LAN and need a confirmation for the agent.")] string driver = "bridge",
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "create_network");
        if (denied != null) return MutationScope.Fail(denied);

        if (string.IsNullOrWhiteSpace(name))
            return MutationScope.Fail("Network name is required.");

        // Contract 0021: Docker gets the driver as IsLan judged it, trimmed.
        driver = LanDrivers.Normalize(driver);
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        // Contract 0021: the network is created on the server the gateway decided (the scope guard binds the call to it).
        var decided = MutationScope.Current?.DecidedTarget?.ServerId ?? serverId;
        var agent = LanDrivers.DecidedForAgent;
        string id;
        try
        {
            id = await docker.CreateNetworkAsync(name, driver, decided);
        }
        catch
        {
            await auditLog.LogAsync(actor, actorType, "network.create", "network", name, name, details: $"Driver: {driver}", serverId: serverId, success: false);
            throw;
        }
        await auditLog.LogAsync(actor, actorType, "network.create", "network", id, name, details: $"Driver: {driver}", serverId: serverId);
        // Contract 0021 (§ 1.7): a person's LAN network is announced; the agent's path asked for a confirmation instead.
        if (!agent && decided is not null)
            await LanDrivers.NotifyAsync(services.GetService<INotificationService>(), decided, name, driver,
                logger: services.GetService<ILogger<NetworkTools>>());
        return $"Network '{name}' created (ID: {id[..Math.Min(12, id.Length)]}, Driver: {driver}).";
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Remove a Docker network by name or ID.")]
    public static async Task<string> RemoveNetwork(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IToolAuditLog auditLog,
        [Description("Network name or ID")] string networkId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "remove_network");
        if (denied != null) return MutationScope.Fail(denied);

        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        try
        {
            await docker.RemoveNetworkAsync(networkId, serverId);
        }
        catch
        {
            await auditLog.LogAsync(actor, actorType, "network.remove", "network", networkId, networkId, serverId: serverId, success: false);
            throw;
        }
        await auditLog.LogAsync(actor, actorType, "network.remove", "network", networkId, networkId, serverId: serverId);
        return $"Network '{networkId}' removed.";
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Connect a container to a Docker network. Connecting to a macvlan/ipvlan network needs a confirmation for the agent.")]
    public static async Task<string> ConnectContainerToNetwork(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IServiceProvider services,
        [Description("Network name or ID")] string networkId,
        [Description("Container name or ID")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "connect_container_to_network");
        if (denied != null) return MutationScope.Fail(denied);

        // Contract 0021: connect by the network id the gateway resolved and decided (part of the approval binding), never
        // the caller's name: a network removed and re-created under the same name has another id, and the connect fails.
        // The same holds for the container: the resolved id, so a container re-created under the same name after the
        // decision is not connected under it.
        if (MutationScope.Current is not { CapabilityId: "network.connect", DecidedTarget: { Kind: "network", Resolved: true } target }
            || string.IsNullOrEmpty(target.Id))
            return MutationScope.Fail("The network to connect to was not resolved by the Mutation Gateway.");
        if (string.IsNullOrEmpty(target.RelatedContainerId))
            return MutationScope.Fail("The container to connect was not resolved by the Mutation Gateway.");
        var agent = LanDrivers.DecidedForAgent;
        await docker.ConnectContainerToNetworkAsync(target.Id, target.RelatedContainerId, target.ServerId ?? serverId);
        if (!agent && target.ServerId is { } server
            && target.Exposures.Select(ComposeExposures.Parse).FirstOrDefault(e => e?.Kind == ExposureKind.LanNetwork) is { } lan)
            await LanDrivers.NotifyAsync(services.GetService<INotificationService>(), server, lan.Key, lan.Value, lan.Service,
                services.GetService<ILogger<NetworkTools>>());
        return $"Container '{containerId}' connected to network '{networkId}'.";
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Disconnect a container from a Docker network.")]
    public static async Task<string> DisconnectContainerFromNetwork(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        [Description("Network name or ID")] string networkId,
        [Description("Container name or ID")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "disconnect_container_from_network");
        if (denied != null) return MutationScope.Fail(denied);

        await docker.DisconnectContainerFromNetworkAsync(networkId, containerId, serverId);
        return $"Container '{containerId}' disconnected from network '{networkId}'.";
    }
}
