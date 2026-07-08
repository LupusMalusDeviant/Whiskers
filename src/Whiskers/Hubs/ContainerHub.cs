using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Whiskers.Services.Docker;

namespace Whiskers.Hubs;

[Authorize]
public class ContainerHub : Hub
{
    private readonly IDockerService _docker;

    public ContainerHub(IDockerService docker)
    {
        _docker = docker;
    }

    public async Task RequestContainerList(string? serverId = null)
    {
        var containers = await _docker.ListContainersAsync(serverId: serverId);
        await Clients.Caller.SendAsync("ContainerListUpdated", containers);
    }

    public async Task RequestAllContainers()
    {
        var containers = await _docker.ListAllContainersAsync();
        await Clients.Caller.SendAsync("ContainerListUpdated", containers);
    }

    public async Task RequestContainerStats(string containerId, string? serverId = null)
    {
        var stats = await _docker.GetContainerStatsAsync(containerId, serverId);
        if (stats != null)
            await Clients.Caller.SendAsync("ContainerStatsUpdated", stats);
    }
}
