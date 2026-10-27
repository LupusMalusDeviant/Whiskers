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

    /// <summary>The vault key that holds a server's kubeconfig YAML before R1-11, and the prefix of the newer keys.</summary>
    public static string KubeconfigVaultKey(string serverId) => $"kubeconfig:{serverId}";

    /// <summary>The vault key that holds <paramref name="server"/>'s kubeconfig: its own reference, or the old key.</summary>
    public static string KubeconfigKeyOf(Whiskers.Models.ServerConfig server) => server.KubeconfigRef ?? KubeconfigVaultKey(server.Id);

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
        var (server, kubeconfigYaml) = ReadKubeconfig(serverId);

        var kubeConfig = KubernetesYaml.LoadFromString<K8SConfiguration>(kubeconfigYaml);
        var clientConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(
            kubeConfig, currentContext: string.IsNullOrWhiteSpace(server.KubeContext) ? null : server.KubeContext);
        return new k8s.Kubernetes(clientConfig);
    }

    /// <summary>The server as stored and its kubeconfig. A rotation may publish a new record and remove the key this
    /// read captured in between (R1-11); a vanished key whose server meanwhile names another one is read again.</summary>
    internal (Whiskers.Models.ServerConfig Server, string Yaml) ReadKubeconfig(string serverId)
    {
        for (var attempt = 0; ; attempt++)
        {
            var server = _serverConfig.GetServer(serverId)
                ?? throw new InvalidOperationException($"Unknown server '{serverId}'.");
            if (server.ConnectionType != ConnectionType.Kubernetes)
                throw new InvalidOperationException($"Server '{server.Name}' is not a Kubernetes cluster.");
            if (!_vault.IsEnabled)
                throw new InvalidOperationException("Vault ist deaktiviert (VAULT_KEY fehlt) — das kubeconfig kann nicht gelesen werden.");

            var key = KubeconfigKeyOf(server);
            var yaml = _vault.GetSecret(key);
            if (!string.IsNullOrWhiteSpace(yaml)) return (server, yaml);
            if (attempt >= 2 || _serverConfig.GetServer(serverId) is not { } now || KubeconfigKeyOf(now) == key)
                throw new InvalidOperationException($"Kein kubeconfig im Vault für Server '{server.Name}' — im Server-Dialog neu hinterlegen.");
        }
    }

    public void Dispose()
    {
        foreach (var entry in _clients.Values)
            if (entry.IsValueCreated)
                (entry.Value as IDisposable)?.Dispose();
        _clients.Clear();
    }
}
