# Services/Workloads

The **backend-neutral workload seam** (kubernetesImplement Track B.1): the subset of container
operations that make sense for both Docker containers and Kubernetes pods, keyed per server and
expressed in the existing container UX vocabulary (`ContainerInfo` stays the shared model — pods
map onto it, grouped by owner instead of compose project).

```
                      ┌─ DockerWorkloadProvider (thin adapter over IDockerService)
IWorkloadProvider ────┤
  (per ServerConfig)  └─ KubernetesWorkloadProvider (Kubernetes/ — Track B.2)
```

Docker-only operations (compose deploy, host shell/nsenter, image pull, networks, volume backups,
recreate/rollback) intentionally **stay on `IDockerService`** — UI/MCP consult
`WorkloadCapabilities` before offering them, instead of scattering `if (isK8s)` branches.

## Files

| File | Purpose |
|---|---|
| `IWorkloadProvider.cs` | The seam: list/get/start/stop/restart/logs/stats for ONE server + `WorkloadCapabilities` (flags incl. honest start/stop semantics — K8s "stop" = scale to 0). |
| `IWorkloadProviderFactory.cs` / `WorkloadProviderFactory.cs` | `GetForServer(serverId)` — dispatches on `ServerConfig.ConnectionType`. Providers are cheap per-call adapters; pooling/self-healing lives below the seam (`DockerConnectionManager`). |
| `DockerWorkloadProvider.cs` | Pure delegation to `IDockerService` (no logic). |
| `FakeWorkloadProvider.cs` | In-memory double for unit tests + the future demo mode (W4): seeded workloads, recorded calls. |
| `Kubernetes/KubernetesClientCache.cs` | Per-server `IKubernetes` client cache, built **in-memory** from the vault kubeconfig (`kubeconfig:{serverId}` — never on disk). Self-healing: a failed API call invalidates, the next call rebuilds. |
| `Kubernetes/KubernetesWorkloadProvider.cs` | Pods as workloads: id = `{namespace}/{podName}`, owner grouping via the compose-project label (ReplicaSet folds to its Deployment name), honest scale/rollout semantics (stop = scale 0, bare pod = delete; restart = rollout-restart; DaemonSets/bare-pod-start refuse with a message). Stats: none until metrics-server lands. |

**Kubernetes onboarding:** paste a kubeconfig in the server dialog (vault required); least-privilege
ServiceAccount manifest + guide in [`deploy/k8s/`](../../../../deploy/k8s/). Interactive exec and MCP
tool coverage for K8s follow in Track B.3. Docker-side aggregations (`ListAllContainersAsync`,
`GetAllServerSystemInfoAsync`, metrics/CVE loops, `DockerConnectionManager`) skip/refuse Kubernetes
servers — the seam is their only path.

## Related

- Docker backend: [`../Docker/`](../Docker/)
- Roadmap: `docs/roadmap/kubernetesImplement.md` (Track B)
