using Docker.DotNet;

namespace ServerWatch.Services.Docker;

/// <summary>Provides and caches DockerClient instances per configured server, with self-healing
/// reconnect for dead SSH tunnels.</summary>
public interface IDockerConnectionManager : IDisposable
{
    Task<DockerClient> GetClientAsync(string? serverId = null);
    Task<T> ExecuteAsync<T>(string? serverId, Func<DockerClient, Task<T>> operation);
    void InvalidateClient(string serverId, DockerClient? ifCurrent = null);
}
