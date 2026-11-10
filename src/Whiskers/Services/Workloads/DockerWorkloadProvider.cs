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

    // The server this provider was created for, by its id: "local" is a server id like any other (R1-06).
    private string? Sid => string.IsNullOrWhiteSpace(ServerId) ? null : ServerId;

    public Task<IList<ContainerInfo>> ListWorkloadsAsync(bool all = true, CancellationToken ct = default)
        => _docker.ListContainersAsync(all, Sid);

    public Task<ContainerInfo?> GetWorkloadAsync(string id, CancellationToken ct = default)
        => _docker.GetContainerAsync(id, Sid);

    public async Task<WorkloadIdentity?> GetWorkloadIdentityAsync(string id, CancellationToken ct = default)
        => WorkloadIdentity.OfContainer(await _docker.GetContainerAsync(id, Sid));

    // Contract 0012 (D8): the decided container id on this provider's server.
    public Task StartAsync(Whiskers.Services.Mutations.ResolvedWorkload workload, CancellationToken ct = default)
        => _docker.StartContainerAsync(Decided(workload), Sid);

    public Task StopAsync(Whiskers.Services.Mutations.ResolvedWorkload workload, CancellationToken ct = default)
        => _docker.StopContainerAsync(Decided(workload), Sid);

    public Task RestartAsync(Whiskers.Services.Mutations.ResolvedWorkload workload, CancellationToken ct = default)
        => _docker.RestartContainerAsync(Decided(workload), Sid);

    /// <summary>The decided container's id; a workload decided on another server is refused.</summary>
    private string Decided(Whiskers.Services.Mutations.ResolvedWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        if (!string.IsNullOrEmpty(workload.ServerId) && !string.IsNullOrEmpty(Sid)
            && !string.Equals(workload.ServerId, Sid, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The workload was decided on '{workload.ServerId}', not on '{Sid}'; nothing changed.");
        return workload.PodId.Length > 0 ? workload.PodId : workload.Id;
    }

    public Task<string> GetLogsAsync(string id, int tailLines = 100, DateTime? since = null, CancellationToken ct = default)
        => _docker.GetContainerLogsAsync(id, tailLines, Sid, since);

    public Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default)
        => _docker.GetContainerStatsAsync(id, Sid);
}
