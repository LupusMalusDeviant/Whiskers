using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Workloads.Kubernetes;

namespace Whiskers.Services.Workloads;

/// <summary>Resolves the right <see cref="IWorkloadProvider"/> for a server, dispatching on
/// <see cref="ServerConfig.ConnectionType"/> (kubernetesImplement Track B.1.3): Kubernetes clusters
/// get the <see cref="KubernetesWorkloadProvider"/>, everything else is a Docker host.</summary>
public interface IWorkloadProviderFactory
{
    /// <summary>Provider bound to the given server. Throws for an unknown server id.</summary>
    IWorkloadProvider GetForServer(string serverId);
}

public sealed class WorkloadProviderFactory : IWorkloadProviderFactory
{
    private readonly IServerConfigService _serverConfig;
    private readonly IDockerService _docker;
    private readonly IKubernetesClientCache _kubernetesClients;
    private readonly Whiskers.Services.Mutations.IMutationScopeMonitor _monitor;

    /// <param name="monitor">The scope guard's monitor (R1-48). The providers are made per call, outside the
    /// container, so the factory wraps each in the guard itself: a workload start, stop or restart runs only inside a
    /// <c>workload.*</c> decision.</param>
    public WorkloadProviderFactory(IServerConfigService serverConfig, IDockerService docker,
        IKubernetesClientCache kubernetesClients, Whiskers.Services.Mutations.IMutationScopeMonitor monitor)
    {
        _serverConfig = serverConfig;
        _docker = docker;
        _kubernetesClients = kubernetesClients;
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    public IWorkloadProvider GetForServer(string serverId)
    {
        var server = _serverConfig.GetServer(serverId)
            ?? throw new InvalidOperationException($"Unknown server '{serverId}'.");

        // Providers are cheap stateless adapters — created per call, no caching needed. Connection
        // pooling/self-healing lives below the seam (DockerConnectionManager / KubernetesClientCache).
        IWorkloadProvider provider = server.ConnectionType switch
        {
            ConnectionType.Kubernetes => new KubernetesWorkloadProvider(server, _kubernetesClients),
            // Every other type (Local, TCP, SSH) is a Docker host.
            _ => new DockerWorkloadProvider(server.Id, _docker),
        };
        return Whiskers.Services.Mutations.MutationScopeProxy<IWorkloadProvider>.Wrap(provider, _monitor);
    }
}
