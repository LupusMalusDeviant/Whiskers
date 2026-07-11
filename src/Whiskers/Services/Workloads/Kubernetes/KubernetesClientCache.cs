using System.Collections.Concurrent;
using k8s;
using k8s.KubeConfigModels;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Workloads.Kubernetes;

/// <summary>Builds and caches one <see cref="IKubernetes"/> client per Kubernetes server. The
/// kubeconfig is loaded from the vault (<c>kubeconfig:{serverId}</c>) and parsed IN-MEMORY via
/// <see cref="KubernetesYaml.LoadFromString{T}"/> + BuildConfigFromConfigObject — it is never
/// written to a file. Vault key name: see <see cref="KubeconfigVaultKey"/>.</summary>
public sealed class KubernetesClientCache : IKubernetesClientCache, IDisposable
{
    private readonly IServerConfigService _serverConfig;
    private readonly IVaultService _vault;
    private readonly ConcurrentDictionary<string, Lazy<IKubernetes>> _clients = new();

    public KubernetesClientCache(IServerConfigService serverConfig, IVaultService vault)
    {
        _serverConfig = serverConfig;
        _vault = vault;
    }

    /// <summary>The vault key that holds a server's kubeconfig YAML.</summary>
    public static string KubeconfigVaultKey(string serverId) => $"kubeconfig:{serverId}";

    public IKubernetes GetClient(string serverId)
    {
        // Lazy so concurrent first calls build the client exactly once per server.
        var lazy = _clients.GetOrAdd(serverId, id => new Lazy<IKubernetes>(() => Build(id)));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // A failed build must not stay poisoned in the cache (Lazy caches the exception).
            _clients.TryRemove(serverId, out _);
            throw;
        }
    }

    public void Invalidate(string serverId)
    {
        if (_clients.TryRemove(serverId, out var removed) && removed.IsValueCreated)
            (removed.Value as IDisposable)?.Dispose();
    }

    private IKubernetes Build(string serverId)
    {
        var server = _serverConfig.GetServer(serverId)
            ?? throw new InvalidOperationException($"Unknown server '{serverId}'.");
        if (server.ConnectionType != ConnectionType.Kubernetes)
            throw new InvalidOperationException($"Server '{server.Name}' is not a Kubernetes cluster.");
        if (!_vault.IsEnabled)
            throw new InvalidOperationException("Vault ist deaktiviert (VAULT_KEY fehlt) — das kubeconfig kann nicht gelesen werden.");

        var kubeconfigYaml = _vault.GetSecret(KubeconfigVaultKey(serverId));
        if (string.IsNullOrWhiteSpace(kubeconfigYaml))
            throw new InvalidOperationException($"Kein kubeconfig im Vault für Server '{server.Name}' — im Server-Dialog neu hinterlegen.");

        var kubeConfig = KubernetesYaml.LoadFromString<K8SConfiguration>(kubeconfigYaml);
        var clientConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(
            kubeConfig, currentContext: string.IsNullOrWhiteSpace(server.KubeContext) ? null : server.KubeContext);
        return new k8s.Kubernetes(clientConfig);
    }

    public void Dispose()
    {
        foreach (var entry in _clients.Values)
            if (entry.IsValueCreated)
                (entry.Value as IDisposable)?.Dispose();
        _clients.Clear();
    }
}
