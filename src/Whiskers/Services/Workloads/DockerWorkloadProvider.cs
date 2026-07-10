using Whiskers.Models;
using Whiskers.Services.Docker;

namespace Whiskers.Services.Workloads;

/// <summary>Thin adapter that exposes the existing <see cref="IDockerService"/> through the
/// backend-neutral <see cref="IWorkloadProvider"/> seam — pure delegation, no logic
/// (kubernetesImplement Track B.1.2). One instance per server, created by the factory.</summary>
public sealed class DockerWorkloadProvider : IWorkloadProvider
{
    private readonly IDockerService _docker;

    public DockerWorkloadProvider(string serverId, IDockerService docker)
    {
        ServerId = serverId;
        _docker = docker;
    }

    public string ServerId { get; }

    public WorkloadCapabilities Capabilities => WorkloadCapabilities.Docker;

    // "local" is represented as a null serverId throughout IDockerService.
    private string? Sid => ServerId == "local" ? null : ServerId;

    public Task<IList<ContainerInfo>> ListWorkloadsAsync(bool all = true, CancellationToken ct = default)
        => _docker.ListContainersAsync(all, Sid);

    public Task<ContainerInfo?> GetWorkloadAsync(string id, CancellationToken ct = default)
        => _docker.GetContainerAsync(id, Sid);

    public Task StartAsync(string id, CancellationToken ct = default)
        => _docker.StartContainerAsync(id, Sid);

    public Task StopAsync(string id, CancellationToken ct = default)
        => _docker.StopContainerAsync(id, Sid);

    public Task RestartAsync(string id, CancellationToken ct = default)
        => _docker.RestartContainerAsync(id, Sid);

    public Task<string> GetLogsAsync(string id, int tailLines = 100, DateTime? since = null, CancellationToken ct = default)
        => _docker.GetContainerLogsAsync(id, tailLines, Sid, since);

    public Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default)
        => _docker.GetContainerStatsAsync(id, Sid);
}
