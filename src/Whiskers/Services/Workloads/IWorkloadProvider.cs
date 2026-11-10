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

    // SupportsExec stays false until pod exec over the WebSocket API exists — the flag claimed it while nothing
    // implemented it, and a capability that lies is worse than one that is missing.
    public static readonly WorkloadCapabilities Kubernetes = new(
        SupportsCompose: false, SupportsHostShell: false, SupportsNetworks: false,
        SupportsResourceEdit: false, SupportsExec: false, SupportsStats: false,
        StartStopSemantics: "scale");
}

/// <summary>A workload as a mutation's target resolves it (contract 0012, D8): the listed workload, its namespace and
/// the controller a start / stop / restart acts on, with the UIDs that are that action's preconditions.</summary>
/// <param name="ControllerKind"><c>Deployment</c>, <c>StatefulSet</c>, <c>DaemonSet</c>, <c>ReplicaSet</c>, <c>None</c>
/// (a bare pod) or <c>Container</c> (Docker).</param>
/// <param name="ControllerUid">The controller's UID; <c>null</c> when it could not be read (an action then refuses).</param>
public sealed record WorkloadIdentity(ContainerInfo Workload, string Namespace, string ControllerKind,
    string ControllerName, string? ControllerUid, string? PodUid)
{
    /// <summary>A container that is its own controller (Docker): its id stands for both UIDs.</summary>
    public static WorkloadIdentity? OfContainer(ContainerInfo? container) =>
        container is null ? null : new(container, "", "Container", container.Name, container.Id, container.Id);
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
    [Whiskers.Services.Mutations.InventoryLookup]
    Task<ContainerInfo?> GetWorkloadAsync(string id, CancellationToken ct = default);

    /// <summary>Contract 0012 (D8): the workload with the controller it belongs to and their UIDs, for the gateway to
    /// resolve a <c>workload.*</c> target. <c>null</c> when it does not exist. A provider without controllers (Docker)
    /// answers with the container as its own controller (<see cref="WorkloadIdentity.OfContainer"/>).</summary>
    [Whiskers.Services.Mutations.InventoryLookup]
    Task<WorkloadIdentity?> GetWorkloadIdentityAsync(string id, CancellationToken ct = default);

    /// <summary>Docker: start container. Kubernetes: scale the decided controller back to 1 replica (only from 0).</summary>
    [Whiskers.Services.Mutations.MutatingOperation("workload.start")]
    Task StartAsync([Whiskers.Services.Mutations.MutationTarget] Whiskers.Services.Mutations.ResolvedWorkload workload,
        CancellationToken ct = default);

    /// <summary>Docker: stop container. Kubernetes: scale the decided controller to 0 (a bare pod: delete it, with its
    /// UID as the precondition).</summary>
    [Whiskers.Services.Mutations.MutatingOperation("workload.stop")]
    Task StopAsync([Whiskers.Services.Mutations.MutationTarget] Whiskers.Services.Mutations.ResolvedWorkload workload,
        CancellationToken ct = default);

    /// <summary>Docker: restart container. Kubernetes: rollout-restart the decided controller.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("workload.restart")]
    Task RestartAsync([Whiskers.Services.Mutations.MutationTarget] Whiskers.Services.Mutations.ResolvedWorkload workload,
        CancellationToken ct = default);

    Task<string> GetLogsAsync(string id, int tailLines = 100, DateTime? since = null, CancellationToken ct = default);

    /// <summary>Live resource stats; null when unavailable (K8s without metrics-server → check
    /// <see cref="WorkloadCapabilities.SupportsStats"/> and show an empty state).</summary>
    Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default);
}
