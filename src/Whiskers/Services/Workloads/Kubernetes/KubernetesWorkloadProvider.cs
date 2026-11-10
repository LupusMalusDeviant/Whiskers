using k8s;
using k8s.Autorest;
using k8s.Models;
using Whiskers.Models;

namespace Whiskers.Services.Workloads.Kubernetes;

/// <summary>The Kubernetes side of the workload seam (kubernetesImplement Track B.2): pods are
/// presented as workloads in the existing container UX vocabulary. Grouping reuses the compose
/// label — each pod gets <c>com.docker.compose.project</c> set to its owner (Deployment /
/// StatefulSet / DaemonSet / Job name, "Standalone" for bare pods) so the dashboard's
/// group-by-project just works.
///
/// Honest semantics (see the mapping table in kubernetesImplement.md §B.0): stop = scale the owner
/// to 0 (bare pods are deleted), start = scale back to 1, restart = rollout-restart via the
/// pod-template annotation. Stats return null (metrics-server integration is a later step;
/// <see cref="WorkloadCapabilities.Kubernetes"/> flags SupportsStats=false). Interactive exec and
/// the events stream come with Track B.3.
///
/// Workload ids are <c>{namespace}/{podName}</c>. Every failed API call invalidates the cached
/// client (self-healing — next call rebuilds from the vault kubeconfig).</summary>
public sealed class KubernetesWorkloadProvider : IWorkloadProvider
{
    private readonly IKubernetesClientCache _clients;
    private readonly string _serverName;
    private readonly IReadOnlyList<string> _namespaces;

    // Fully qualified: the Whiskers.Services.ServerConfig NAMESPACE shadows the Models type here.
    public KubernetesWorkloadProvider(Whiskers.Models.ServerConfig server, IKubernetesClientCache clients)
    {
        ServerId = server.Id;
        _serverName = server.Name;
        _namespaces = server.KubeNamespaces;
        _clients = clients;
    }

    public string ServerId { get; }
    public WorkloadCapabilities Capabilities => WorkloadCapabilities.Kubernetes;

    public async Task<IList<ContainerInfo>> ListWorkloadsAsync(bool all = true, CancellationToken ct = default)
    {
        return await WithClient(async client =>
        {
            var pods = new List<V1Pod>();
            if (_namespaces.Count == 0)
            {
                var list = await client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: ct);
                pods.AddRange(list.Items);
            }
            else
            {
                foreach (var ns in _namespaces)
                {
                    var list = await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct);
                    pods.AddRange(list.Items);
                }
            }

            IList<ContainerInfo> result = pods
                .Select(p => ToWorkload(p, ServerId, _serverName))
                .Where(w => all || w.State == "running")
                .ToList();
            return result;
        });
    }

    public async Task<ContainerInfo?> GetWorkloadAsync(string id, CancellationToken ct = default)
    {
        if (!TryParseId(id, out var ns, out var name)) return null;
        return await WithClient(async client =>
        {
            try
            {
                var pod = await client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct);
                return ToWorkload(pod, ServerId, _serverName);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (ContainerInfo?)null;
            }
        });
    }

    /// <summary>Contract 0012 (D8): the pod, its namespace and the controller a start / stop / restart acts on, with
    /// their UIDs. A ReplicaSet's owning Deployment is read from the ReplicaSet's owner reference (one call), so the
    /// Deployment's own UID is the precondition — never a name folded from the ReplicaSet's.</summary>
    public async Task<WorkloadIdentity?> GetWorkloadIdentityAsync(string id, CancellationToken ct = default)
    {
        if (!TryParseId(id, out var ns, out var name)) return null;
        return await WithClient(async client =>
        {
            V1Pod pod;
            try
            {
                pod = await client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (WorkloadIdentity?)null;
            }
            var workload = ToWorkload(pod, ServerId, _serverName);
            var owner = pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true)
                        ?? pod.Metadata.OwnerReferences?.FirstOrDefault();
            if (owner is null)
                return new WorkloadIdentity(workload, ns, "None", name, null, pod.Metadata.Uid);
            if (owner.Kind == "ReplicaSet")
            {
                var replicaSet = await client.AppsV1.ReadNamespacedReplicaSetAsync(owner.Name, ns, cancellationToken: ct);
                var deployment = replicaSet.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true && o.Kind == "Deployment");
                return deployment is null
                    ? new WorkloadIdentity(workload, ns, "ReplicaSet", owner.Name, owner.Uid, pod.Metadata.Uid)
                    : new WorkloadIdentity(workload, ns, "Deployment", deployment.Name, deployment.Uid, pod.Metadata.Uid);
            }
            return new WorkloadIdentity(workload, ns, owner.Kind, owner.Name, owner.Uid, pod.Metadata.Uid);
        });
    }

    public Task StartAsync(Whiskers.Services.Mutations.ResolvedWorkload workload, CancellationToken ct = default) =>
        ScaleAsync(workload, up: true, ct);

    public Task StopAsync(Whiskers.Services.Mutations.ResolvedWorkload workload, CancellationToken ct = default) =>
        ScaleAsync(workload, up: false, ct);

    /// <summary>Rollout-restart of exactly the decided controller (D8): its pod-template annotation is patched with
    /// the controller's UID as the precondition, so a controller replaced since the decision is not touched. No pod
    /// is read here.</summary>
    public async Task RestartAsync(Whiskers.Services.Mutations.ResolvedWorkload workload, CancellationToken ct = default)
    {
        var (ns, controller, uid) = Decided(workload);
        if (workload.ControllerKind is not ("Deployment" or "StatefulSet" or "DaemonSet"))
            // A bare pod has no controller to bring it back — deleting it would NOT be a
            // restart. Refuse with an honest message instead of surprising the user.
            throw new InvalidOperationException(
                $"Pod '{workload.Name}' hat keinen Controller (Deployment/StatefulSet/DaemonSet) — ein Neustart würde ihn dauerhaft entfernen.");
        var patch = ControllerPatch(uid,
            $"\"template\":{{\"metadata\":{{\"annotations\":{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}}}}}");
        await WithClient<object?>(async client =>
        {
            await Preconditioned(workload, () => workload.ControllerKind switch
            {
                "Deployment" => (Task)client.AppsV1.PatchNamespacedDeploymentAsync(patch, controller, ns, cancellationToken: ct),
                "StatefulSet" => (Task)client.AppsV1.PatchNamespacedStatefulSetAsync(patch, controller, ns, cancellationToken: ct),
                _ => (Task)client.AppsV1.PatchNamespacedDaemonSetAsync(patch, controller, ns, cancellationToken: ct),
            });
            return null;
        });
    }

    public async Task<string> GetLogsAsync(string id, int tailLines = 100, DateTime? since = null, CancellationToken ct = default)
    {
        var (ns, name) = RequireId(id);
        return await WithClient(async client =>
        {
            var sinceSeconds = since is { } s ? (int?)Math.Max(1, (int)(DateTime.UtcNow - s).TotalSeconds) : null;
            using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(
                name, ns, tailLines: tailLines, sinceSeconds: sinceSeconds, cancellationToken: ct);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        });
    }

    public Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default)
        // Live stats need metrics-server (PodMetrics) — a later Track B step. Capabilities flag
        // SupportsStats=false, so the UI shows its "no metrics" empty state instead of calling this.
        => Task.FromResult<ContainerStats?>(null);

    // ---- scale (start/stop) ----------------------------------------------------------------------

    /// <summary>Scales exactly the decided controller (D8), with its UID as the patch's precondition; a bare pod is
    /// deleted with its own UID as <see cref="V1DeleteOptions.Preconditions"/>. No pod is read here.</summary>
    private async Task ScaleAsync(Whiskers.Services.Mutations.ResolvedWorkload workload, bool up, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workload);
        switch (workload.ControllerKind)
        {
            case "Deployment" or "StatefulSet":
            {
                var (ns, controller, uid) = Decided(workload);
                var patch = ControllerPatch(uid, $"\"replicas\":{(up ? 1 : 0)}");
                await WithClient<object?>(async client =>
                {
                    // Only scale UP from 0 — never stomp a multi-replica controller down to 1. The scale read names
                    // the decided controller; one replaced since (another UID) is refused before anything changes.
                    if (up)
                    {
                        var scale = workload.ControllerKind == "Deployment"
                            ? await client.AppsV1.ReadNamespacedDeploymentScaleAsync(controller, ns, cancellationToken: ct)
                            : await client.AppsV1.ReadNamespacedStatefulSetScaleAsync(controller, ns, cancellationToken: ct);
                        if (!string.IsNullOrEmpty(scale.Metadata?.Uid) && scale.Metadata.Uid != uid) throw Replaced(workload);
                        if ((scale.Spec?.Replicas ?? 0) > 0) return null;
                    }
                    await Preconditioned(workload, () => workload.ControllerKind == "Deployment"
                        ? (Task)client.AppsV1.PatchNamespacedDeploymentAsync(patch, controller, ns, cancellationToken: ct)
                        : client.AppsV1.PatchNamespacedStatefulSetAsync(patch, controller, ns, cancellationToken: ct));
                    return null;
                });
                return;
            }
            case "DaemonSet":
                throw new InvalidOperationException("DaemonSets laufen auf jedem Node und können nicht skaliert werden.");
            case "None":
            {
                if (up)
                    throw new InvalidOperationException(
                        $"Pod '{workload.Name}' hat keinen Controller — ein gestoppter (gelöschter) Bare-Pod kann nicht wieder gestartet werden.");
                var (ns, pod) = RequireId(workload.PodId);
                if (string.IsNullOrEmpty(workload.PodUid))
                    throw new InvalidOperationException($"The UID of pod '{pod}' was not read with the decision; nothing changed.");
                // Stop for a bare pod = delete it (that is the only stop Kubernetes offers here) — exactly the decided pod.
                var options = new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = workload.PodUid } };
                await WithClient<object?>(async client =>
                {
                    await Preconditioned(workload, () => client.CoreV1.DeleteNamespacedPodAsync(pod, ns, options, cancellationToken: ct));
                    return null;
                });
                return;
            }
            default:
                throw new InvalidOperationException(
                    $"'{workload.Name}' is controlled by a {workload.ControllerKind}, which Whiskers does not scale; nothing changed.");
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>Contract 0012 (D8): the decided controller — namespace, name, UID — on this provider's cluster. A
    /// workload decided on another server, or without a controller UID, is refused before any call.</summary>
    private (string Ns, string Controller, string Uid) Decided(Whiskers.Services.Mutations.ResolvedWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        if (!string.Equals(workload.ServerId, ServerId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The workload was decided on '{workload.ServerId}', not on '{ServerId}'; nothing changed.");
        if (string.IsNullOrEmpty(workload.Namespace) || string.IsNullOrEmpty(workload.ControllerName))
            throw new InvalidOperationException($"'{workload.Name}' has no decided controller; nothing changed.");
        if (string.IsNullOrEmpty(workload.ControllerUid))
            throw new InvalidOperationException(
                $"The UID of '{workload.ControllerName}' was not read with the decision; nothing changed.");
        return (workload.Namespace, workload.ControllerName, workload.ControllerUid);
    }

    /// <summary>A merge patch of the controller's <c>spec</c> that carries <c>metadata.uid</c>: the API server refuses
    /// it (409 Conflict) when the object under that name has another UID.</summary>
    internal static V1Patch ControllerPatch(string uid, string spec) =>
        new($"{{\"metadata\":{{\"uid\":{System.Text.Json.JsonSerializer.Serialize(uid)}}},\"spec\":{{{spec}}}}}",
            V1Patch.PatchType.MergePatch);

    /// <summary>A precondition the API server refused (the object was replaced since the decision) is reported as
    /// such: the operation's own failure, nothing changed (D8, D9).</summary>
    private static async Task Preconditioned(Whiskers.Services.Mutations.ResolvedWorkload workload, Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw Replaced(workload);
        }
    }

    private static InvalidOperationException Replaced(Whiskers.Services.Mutations.ResolvedWorkload workload) =>
        new($"'{workload.ControllerName ?? workload.Name}' was replaced after the decision (TargetChanged); nothing changed.");

    private async Task<T> WithClient<T>(Func<IKubernetes, Task<T>> action)
    {
        var client = _clients.GetClient(ServerId);
        try
        {
            return await action(client);
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            // Self-heal: a failed API call may mean a stale/broken client (rotated certs, new API
            // endpoint) — drop it so the next call rebuilds from the vault kubeconfig.
            _clients.Invalidate(ServerId);
            throw;
        }
    }

    private static (string Ns, string Name) RequireId(string id) =>
        TryParseId(id, out var ns, out var name)
            ? (ns, name)
            : throw new ArgumentException($"Invalid workload id '{id}' — expected 'namespace/podName'.", nameof(id));

    /// <summary>Workload ids are <c>{namespace}/{podName}</c>.</summary>
    public static bool TryParseId(string id, out string ns, out string name)
    {
        ns = ""; name = "";
        if (string.IsNullOrWhiteSpace(id)) return false;
        var idx = id.IndexOf('/');
        if (idx <= 0 || idx == id.Length - 1) return false;
        ns = id[..idx];
        name = id[(idx + 1)..];
        return !name.Contains('/');
    }

    /// <summary>Maps a pod onto the shared workload model. Public static so the mapping is
    /// unit-testable without a cluster (KubernetesWorkloadProviderTests).</summary>
    public static ContainerInfo ToWorkload(V1Pod pod, string serverId, string serverName)
    {
        var ns = pod.Metadata.NamespaceProperty ?? "default";
        var name = pod.Metadata.Name ?? "";
        var phase = pod.Status?.Phase ?? "Unknown";
        var containerStatuses = pod.Status?.ContainerStatuses ?? new List<V1ContainerStatus>();
        var readyCount = containerStatuses.Count(cs => cs.Ready);
        var totalCount = Math.Max(containerStatuses.Count, pod.Spec?.Containers?.Count ?? 0);
        var allReady = totalCount > 0 && readyCount == totalCount;

        var state = phase switch
        {
            "Running" => "running",
            "Succeeded" => "exited",
            "Failed" => "exited",
            _ => "created", // Pending / Unknown
        };

        var owner = ResolveOwner(pod);
        var labels = new Dictionary<string, string>(pod.Metadata.Labels ?? new Dictionary<string, string>())
        {
            // Reuse the compose-project label as the grouping key so the existing dashboard
            // group-by-project renders Deployment/StatefulSet/... groups without UI changes.
            ["com.docker.compose.project"] = owner.Kind == "None" ? "Standalone" : owner.Name,
        };

        return new ContainerInfo
        {
            Id = $"{ns}/{name}",
            Name = name,
            Image = pod.Spec?.Containers?.FirstOrDefault()?.Image ?? "",
            State = state,
            Status = phase == "Running" ? $"Running ({readyCount}/{totalCount} ready)" : phase,
            Created = pod.Metadata.CreationTimestamp ?? default,
            HealthStatus = phase == "Running" ? (allReady ? "healthy" : "unhealthy") : "none",
            Labels = labels,
            ServerId = serverId,
            ServerName = serverName,
        };
    }

    /// <summary>Resolves the pod's controlling workload. A ReplicaSet owner is folded to its
    /// Deployment by trimming the trailing "-{hash}" segment of the ReplicaSet name — deliberately
    /// without an extra API call per pod. Kind "None" = bare pod.</summary>
    public static (string Kind, string Name) ResolveOwner(V1Pod pod)
    {
        var owner = pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true)
                    ?? pod.Metadata.OwnerReferences?.FirstOrDefault();
        if (owner is null) return ("None", "");

        if (owner.Kind == "ReplicaSet")
        {
            var idx = owner.Name.LastIndexOf('-');
            return ("Deployment", idx > 0 ? owner.Name[..idx] : owner.Name);
        }
        return (owner.Kind, owner.Name);
    }
}
