using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Workloads;

/// <summary>Resolves the right <see cref="IWorkloadProvider"/> for a server, dispatching on
/// <see cref="ServerConfig.ConnectionType"/> (kubernetesImplement Track B.1.3). Today every
/// connection type is Docker-backed; the Kubernetes provider joins in Track B.2.</summary>
public interface IWorkloadProviderFactory
{
    /// <summary>Provider bound to the given server. Throws for an unknown server id.</summary>
    IWorkloadProvider GetForServer(string serverId);
}

public sealed class WorkloadProviderFactory : IWorkloadProviderFactory
{
    private readonly IServerConfigService _serverConfig;
    private readonly IDockerService _docker;

    public WorkloadProviderFactory(IServerConfigService serverConfig, IDockerService docker)
    {
        _serverConfig = serverConfig;
        _docker = docker;
    }

    public IWorkloadProvider GetForServer(string serverId)
    {
        var server = _serverConfig.GetServer(serverId)
            ?? throw new InvalidOperationException($"Unknown server '{serverId}'.");

        // Providers are cheap stateless adapters — created per call, no caching needed. Connection
        // pooling/self-healing lives below the seam (DockerConnectionManager; K8s client cache in B.2).
        return server.ConnectionType switch
        {
            // Every current type (Local, TCP, SSH) is a Docker host.
            _ => new DockerWorkloadProvider(server.Id, _docker),
        };
    }
}
