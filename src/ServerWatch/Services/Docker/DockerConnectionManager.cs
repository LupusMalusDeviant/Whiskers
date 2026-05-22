using System.Collections.Concurrent;
using Docker.DotNet;
using ServerWatch.Models;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.Docker;

public class DockerConnectionManager : IDisposable
{
    private readonly ServerConfigService _serverConfig;
    private readonly SshTunnelManager _sshTunnelManager;
    private readonly ILogger<DockerConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, DockerClient> _clients = new();

    public DockerConnectionManager(
        ServerConfigService serverConfig,
        SshTunnelManager sshTunnelManager,
        ILogger<DockerConnectionManager> logger)
    {
        _serverConfig = serverConfig;
        _sshTunnelManager = sshTunnelManager;
        _logger = logger;
    }

    /// <summary>
    /// Get or create a DockerClient for the given server.
    /// If serverId is null, returns the default server's client.
    /// </summary>
    public async Task<DockerClient> GetClientAsync(string? serverId = null)
    {
        var server = serverId != null
            ? _serverConfig.GetServer(serverId)
            : _serverConfig.GetDefaultServer();

        if (server == null)
            throw new InvalidOperationException($"Server '{serverId ?? "default"}' not found");

        if (_clients.TryGetValue(server.Id, out var existing))
            return existing;

        var client = await CreateClientAsync(server);
        _clients[server.Id] = client;
        return client;
    }

    /// <summary>
    /// Backward compatibility — returns default server's client synchronously.
    /// Used only for the local socket which doesn't need async setup.
    /// </summary>
    public DockerClient Client
    {
        get
        {
            var server = _serverConfig.GetDefaultServer();
            if (server == null)
                throw new InvalidOperationException("No default server configured");

            return _clients.GetOrAdd(server.Id, _ =>
            {
                return new DockerClientConfiguration(new Uri(server.SocketPath)).CreateClient();
            });
        }
    }

    public void InvalidateClient(string serverId)
    {
        if (_clients.TryRemove(serverId, out var client))
        {
            client.Dispose();
            _sshTunnelManager.CloseTunnel(serverId);
        }
    }

    private async Task<DockerClient> CreateClientAsync(Models.ServerConfig server)
    {
        switch (server.ConnectionType)
        {
            case ConnectionType.Local:
                return new DockerClientConfiguration(new Uri(server.SocketPath)).CreateClient();

            case ConnectionType.TCP:
            {
                var uri = server.TcpUseTls
                    ? $"https://{server.TcpHost}:{server.TcpPort}"
                    : $"http://{server.TcpHost}:{server.TcpPort}";
                return new DockerClientConfiguration(new Uri(uri)).CreateClient();
            }

            case ConnectionType.SSH:
            {
                var localPort = await _sshTunnelManager.EstablishTunnelAsync(server);
                return new DockerClientConfiguration(new Uri($"http://127.0.0.1:{localPort}")).CreateClient();
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(server.ConnectionType));
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }
}
