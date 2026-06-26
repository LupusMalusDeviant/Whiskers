using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;
using Docker.DotNet.X509;
using ServerWatch.Models;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.Docker;

public class DockerConnectionManager : IDisposable
{
    private readonly ServerConfigService _serverConfig;
    private readonly SshTunnelManager _sshTunnelManager;
    private readonly ILogger<DockerConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, DockerClient> _clients = new();

    // Serializes client/tunnel creation per server so concurrent callers (the several background
    // pollers) can't each spawn a tunnel for the same server at once — that would leak orphaned
    // ssh processes and waste local ports.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

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

        // Fast path: a cached client whose underlying transport is still alive.
        if (TryGetLiveClient(server, out var live))
            return live!;

        // Slow path: build (or rebuild) the client under a per-server lock so we never create two
        // tunnels for the same server concurrently.
        var gate = _locks.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // Re-check under the lock — another caller may have just built it.
            if (TryGetLiveClient(server, out var live2))
                return live2!;

            // Tear down any stale client + dead tunnel before rebuilding.
            InvalidateClient(server.Id);

            var client = await CreateClientAsync(server);
            _clients[server.Id] = client;
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Returns a cached client only if its transport is still usable. For SSH servers the client
    /// points at a local SSH-tunnel port; if that tunnel has died (network blip, remote sshd
    /// restart, keepalive timeout) the cached client is pinned to a dead port and every call fails
    /// with "connection refused" forever. Treating a dead tunnel as "not live" forces a rebuild on
    /// the next call, so the app self-heals within one poll cycle instead of needing a restart.
    /// </summary>
    private bool TryGetLiveClient(Models.ServerConfig server, out DockerClient? client)
    {
        if (_clients.TryGetValue(server.Id, out client))
        {
            if (server.ConnectionType != ConnectionType.SSH || _sshTunnelManager.IsTunnelActive(server.Id))
                return true;
            _logger.LogWarning("SSH tunnel for '{ServerName}' is no longer alive; connection will be rebuilt", server.Name);
        }
        client = null;
        return false;
    }

    /// <summary>
    /// Runs a Docker operation and, if it fails with a transport-level error (a dead tunnel that
    /// died mid-flight, a half-open connection the liveness check couldn't catch), invalidates the
    /// connection and retries exactly once against a freshly established tunnel.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(string? serverId, Func<DockerClient, Task<T>> operation)
    {
        var client = await GetClientAsync(serverId);
        try
        {
            return await operation(client);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            var id = serverId
                ?? _serverConfig.GetDefaultServer()?.Id
                ?? throw new InvalidOperationException("No default server configured");
            _logger.LogWarning(ex,
                "Docker operation on '{ServerId}' failed with a connection error; rebuilding tunnel and retrying once", id);
            InvalidateClient(id);
            client = await GetClientAsync(serverId);
            return await operation(client);
        }
    }

    private static bool IsConnectionFailure(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException or HttpRequestException or TimeoutException or IOException)
                return true;
        }
        return false;
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
                var uri = new Uri(server.TcpUseTls
                    ? $"https://{server.TcpHost}:{server.TcpPort}"
                    : $"http://{server.TcpHost}:{server.TcpPort}");
                // mTLS path: present a client cert and verify the server against the CA. No SSH key.
                if (server.TcpUseTls && !string.IsNullOrEmpty(server.TcpClientCertPath))
                    return new DockerClientConfiguration(uri, BuildMtlsCredentials(server)).CreateClient();
                return new DockerClientConfiguration(uri).CreateClient();
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

    /// <summary>
    /// Builds mutual-TLS credentials for the TCP path: presents the client certificate and verifies
    /// the server's certificate chain against the configured CA (custom root trust, no reliance on
    /// the system trust store). PEM client cert+key are round-tripped through PKCS#12 so the private
    /// key is usable for TLS client auth across platforms.
    /// </summary>
    private static CertificateCredentials BuildMtlsCredentials(Models.ServerConfig server)
    {
        using var ephemeral = X509Certificate2.CreateFromPemFile(server.TcpClientCertPath!, server.TcpClientKeyPath);
        var clientCert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);

        var credentials = new CertificateCredentials(clientCert);

        if (!string.IsNullOrEmpty(server.TcpCaCertPath))
        {
            var caCert = X509CertificateLoader.LoadCertificateFromFile(server.TcpCaCertPath);
            credentials.ServerCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(caCert);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)));
            };
        }

        return credentials;
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }
}
