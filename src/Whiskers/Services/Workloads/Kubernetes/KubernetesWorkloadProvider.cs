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

    public Task StartAsync(string id, CancellationToken ct = default) => ScaleAsync(id, up: true, ct);

    public Task StopAsync(string id, CancellationToken ct = default) => ScaleAsync(id, up: false, ct);

    public async Task RestartAsync(string id, CancellationToken ct = default)
    {
        var (ns, name) = RequireId(id);
        await WithClient<object?>(async client =>
        {
            var pod = await client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct);
            var owner = ResolveOwner(pod);

            // Rollout-restart equivalent: patch the controller's pod-template annotation; the
            // controller then replaces the pods with zero-downtime semantics where applicable.
            var patch = new V1Patch(
                $"{{\"spec\":{{\"template\":{{\"metadata\":{{\"annotations\":{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}}}}}}}}}",
                V1Patch.PatchType.MergePatch);

            switch (owner.Kind)
            {
                case "Deployment":
                    await client.AppsV1.PatchNamespacedDeploymentAsync(patch, owner.Name, ns, cancellationToken: ct);
                    break;
                case "StatefulSet":
                    await client.AppsV1.PatchNamespacedStatefulSetAsync(patch, owner.Name, ns, cancellationToken: ct);
                    break;
                case "DaemonSet":
                    await client.AppsV1.PatchNamespacedDaemonSetAsync(patch, owner.Name, ns, cancellationToken: ct);
                    break;
                default:
                    // A bare pod has no controller to bring it back — deleting it would NOT be a
                    // restart. Refuse with an honest message instead of surprising the user.
                    throw new InvalidOperationException(
                        $"Pod '{name}' hat keinen Controller (Deployment/StatefulSet/DaemonSet) — ein Neustart würde ihn dauerhaft entfernen.");
            }
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

    private async Task ScaleAsync(string id, bool up, CancellationToken ct)
    {
        var (ns, name) = RequireId(id);
        await WithClient<object?>(async client =>
        {
            var pod = await client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct);
            var owner = ResolveOwner(pod);
            var patch = new V1Patch($"{{\"spec\":{{\"replicas\":{(up ? 1 : 0)}}}}}", V1Patch.PatchType.MergePatch);

            switch (owner.Kind)
            {
                case "Deployment":
                    // Only scale UP from 0 — never stomp a multi-replica deployment down to 1.
                    if (up)
                    {
                        var scale = await client.AppsV1.ReadNamespacedDeploymentScaleAsync(owner.Name, ns, cancellationToken: ct);
                        if ((scale.Spec.Replicas ?? 0) > 0) return null;
                    }
                    await client.AppsV1.PatchNamespacedDeploymentScaleAsync(patch, owner.Name, ns, cancellationToken: ct);
                    break;
                case "StatefulSet":
                    if (up)
                    {
                        var scale = await client.AppsV1.ReadNamespacedStatefulSetScaleAsync(owner.Name, ns, cancellationToken: ct);
                        if ((scale.Spec.Replicas ?? 0) > 0) return null;
                    }
                    await client.AppsV1.PatchNamespacedStatefulSetScaleAsync(patch, owner.Name, ns, cancellationToken: ct);
                    break;
                case "DaemonSet":
                    throw new InvalidOperationException("DaemonSets laufen auf jedem Node und können nicht skaliert werden.");
                default:
                    if (up)
                        throw new InvalidOperationException(
                            $"Pod '{name}' hat keinen Controller — ein gestoppter (gelöschter) Bare-Pod kann nicht wieder gestartet werden.");
                    // Stop for a bare pod = delete it (that is the only stop Kubernetes offers here).
                    await client.CoreV1.DeleteNamespacedPodAsync(name, ns, cancellationToken: ct);
                    break;
            }
            return null;
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------

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
