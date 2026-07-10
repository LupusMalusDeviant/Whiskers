using Whiskers.Models;

namespace Whiskers.Services.Workloads;

/// <summary>What a workload backend can do — UI and MCP tools hide actions the backend does not
/// support instead of sprinkling <c>if (isK8s)</c> checks (kubernetesImplement Track B.1.4).
/// Docker supports everything; Kubernetes maps a subset (compose, host shell and network management
/// are Docker-only and stay on <c>IDockerService</c>).</summary>
public sealed record WorkloadCapabilities(
    bool SupportsCompose,
    bool SupportsHostShell,
    bool SupportsNetworks,
    bool SupportsResourceEdit,
    bool SupportsExec,
    bool SupportsStats,
    /// <summary>Honest UI label semantics: Docker start/stop acts on the container; Kubernetes
    /// "stop" scales a controller to 0 (or deletes a bare pod, which its controller recreates).</summary>
    string StartStopSemantics)
{
    public static readonly WorkloadCapabilities Docker = new(
        SupportsCompose: true, SupportsHostShell: true, SupportsNetworks: true,
        SupportsResourceEdit: true, SupportsExec: true, SupportsStats: true,
        StartStopSemantics: "container");

    public static readonly WorkloadCapabilities Kubernetes = new(
        SupportsCompose: false, SupportsHostShell: false, SupportsNetworks: false,
        SupportsResourceEdit: false, SupportsExec: true, SupportsStats: false,
        StartStopSemantics: "scale");
}

/// <summary>The backend-neutral workload surface for ONE server (kubernetesImplement Track B.1):
/// the subset of operations that make sense for both Docker containers and Kubernetes pods, in the
/// existing container UX vocabulary (<see cref="ContainerInfo"/> stays the shared model — pods map
/// onto it). Obtain instances via <see cref="IWorkloadProviderFactory.GetForServer"/>; the provider
/// is already bound to its server, so no serverId parameters here.
///
/// Docker-only operations (compose deploy, volume backups, host shell via nsenter, image pull,
/// network management, recreate/rollback) intentionally stay on <c>IDockerService</c> — consult
/// <see cref="Capabilities"/> before offering them. Exec and an events stream are added with the
/// Kubernetes provider (Track B.2) / F6 respectively.</summary>
public interface IWorkloadProvider
{
    string ServerId { get; }
    WorkloadCapabilities Capabilities { get; }

    Task<IList<ContainerInfo>> ListWorkloadsAsync(bool all = true, CancellationToken ct = default);
    Task<ContainerInfo?> GetWorkloadAsync(string id, CancellationToken ct = default);

    /// <summary>Docker: start container. Kubernetes: scale the owner back to its previous/1 replica.</summary>
    Task StartAsync(string id, CancellationToken ct = default);

    /// <summary>Docker: stop container. Kubernetes: scale the owner to 0 (bare pods: delete).</summary>
    Task StopAsync(string id, CancellationToken ct = default);

    /// <summary>Docker: restart container. Kubernetes: rollout-restart the owner.</summary>
    Task RestartAsync(string id, CancellationToken ct = default);

    Task<string> GetLogsAsync(string id, int tailLines = 100, DateTime? since = null, CancellationToken ct = default);

    /// <summary>Live resource stats; null when unavailable (K8s without metrics-server → check
    /// <see cref="WorkloadCapabilities.SupportsStats"/> and show an empty state).</summary>
    Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default);
}
