using k8s;

namespace Whiskers.Services.Workloads.Kubernetes;

/// <summary>Per-server cache of <see cref="IKubernetes"/> clients, built in-memory from the vault
/// kubeconfig — the kubeconfig never touches disk. Mirrors <c>DockerConnectionManager</c>'s
/// self-healing idea: a failed API call invalidates the cached client so the next call rebuilds it.</summary>
public interface IKubernetesClientCache
{
    /// <summary>The cached (or freshly built) client for the server. Throws with a clear message
    /// when the server is unknown, not a Kubernetes server, or has no vault kubeconfig.</summary>
    IKubernetes GetClient(string serverId);

    /// <summary>Drops the cached client (call after a failed API call or a config change).</summary>
    void Invalidate(string serverId);
}
