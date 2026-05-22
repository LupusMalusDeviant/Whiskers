using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ServerWatch.Models;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.Docker;

public class SshTunnelManager : IDisposable
{
    private readonly ServerConfigService _serverConfig;
    private readonly ILogger<SshTunnelManager> _logger;
    private readonly ConcurrentDictionary<string, SshTunnel> _tunnels = new();

    public SshTunnelManager(ServerConfigService serverConfig, ILogger<SshTunnelManager> logger)
    {
        _serverConfig = serverConfig;
        _logger = logger;
    }

    public async Task<int> EstablishTunnelAsync(Models.ServerConfig server)
    {
        if (_tunnels.TryGetValue(server.Id, out var existing) && existing.IsAlive)
            return existing.LocalPort;

        // Close stale tunnel
        CloseTunnel(server.Id);

        var localPort = GetAvailablePort();
        var keyPath = _serverConfig.GetSshKeyPath(server);

        // Validate the key is present and readable by THIS process before spawning ssh, so a
        // permission/ownership problem surfaces as a clear, actionable error instead of a generic
        // "connection failed" (ssh would silently die and the tunnel port would just be dead).
        if (!string.IsNullOrEmpty(server.SshKeyFileName))
        {
            if (keyPath == null)
                throw new InvalidOperationException(
                    $"SSH key '{server.SshKeyFileName}' for server '{server.Name}' was not found in the ssh-keys directory. Re-upload the key for this server.");

            try
            {
                using var _ = File.OpenRead(keyPath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new InvalidOperationException(
                    $"SSH key for server '{server.Name}' exists ({keyPath}) but is not readable by the ServerWatch process. " +
                    $"Fix its ownership/permissions (the key must be owned by the app user with mode 600). Underlying error: {ex.Message}");
            }
        }

        var args = $"-N -L {localPort}:/var/run/docker.sock {server.SshUser}@{server.SshHost} -p {server.SshPort} -o StrictHostKeyChecking=no -o ServerAliveInterval=30 -o ConnectTimeout=10";
        if (!string.IsNullOrEmpty(keyPath))
            args = $"-i {keyPath} " + args;

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();

        // Wait briefly for the tunnel to establish
        await Task.Delay(1500);

        if (process.HasExited)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"SSH tunnel failed for {server.Name}: {error}");
        }

        var tunnel = new SshTunnel(server.Id, localPort, process);
        _tunnels[server.Id] = tunnel;

        _logger.LogInformation("SSH tunnel established for {ServerName} on local port {Port}", server.Name, localPort);
        return localPort;
    }

    public void CloseTunnel(string serverId)
    {
        if (_tunnels.TryRemove(serverId, out var tunnel))
        {
            tunnel.Dispose();
            _logger.LogInformation("SSH tunnel closed for server {ServerId}", serverId);
        }
    }

    public bool IsTunnelActive(string serverId)
    {
        return _tunnels.TryGetValue(serverId, out var tunnel) && tunnel.IsAlive;
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        foreach (var tunnel in _tunnels.Values)
            tunnel.Dispose();
        _tunnels.Clear();
    }

    private class SshTunnel : IDisposable
    {
        public string ServerId { get; }
        public int LocalPort { get; }
        private readonly Process _process;

        public bool IsAlive => !_process.HasExited;

        public SshTunnel(string serverId, int localPort, Process process)
        {
            ServerId = serverId;
            LocalPort = localPort;
            _process = process;
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
                _process.Dispose();
            }
            catch { }
        }
    }
}
