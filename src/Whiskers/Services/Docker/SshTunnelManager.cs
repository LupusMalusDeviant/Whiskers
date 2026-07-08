using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;
using Whiskers.Utils;

namespace Whiskers.Services.Docker;

public class SshTunnelManager : ISshTunnelManager
{
    private readonly IServerConfigService _serverConfig;
    private readonly ILogger<SshTunnelManager> _logger;
    private readonly ConcurrentDictionary<string, SshTunnel> _tunnels = new();

    public SshTunnelManager(IServerConfigService serverConfig, ILogger<SshTunnelManager> logger)
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
                    $"SSH key for server '{server.Name}' exists ({keyPath}) but is not readable by the Whiskers process. " +
                    $"Fix its ownership/permissions (the key must be owned by the app user with mode 600). Underlying error: {ex.Message}");
            }
        }

        // Picking a free port (GetAvailablePort) releases it again before ssh binds it — a TOCTOU race
        // where another process can grab the port in between. Retry a few times with a fresh port when ssh
        // reports a bind failure (ExitOnForwardFailure makes it exit immediately in that case).
        const int maxAttempts = 3;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var localPort = GetAvailablePort();
            var process = StartSshProcess(server, keyPath, localPort);

            // Wait until the forwarded port actually accepts connections (up to ~10s) rather than
            // guessing with a fixed delay — a slow handshake would otherwise leave us with a client
            // pointed at a port that isn't listening yet.
            if (await WaitForPortAsync(localPort, process, TimeSpan.FromSeconds(10)))
            {
                // Drain stderr for the tunnel's lifetime. Without this the pipe buffer fills on a chatty
                // ssh (keepalive/known-hosts warnings) and the process blocks on write — the tunnel then
                // looks alive (HasExited == false) while forwarding nothing, the exact "all SSH servers
                // unreachable" freeze. Redact each line in case ssh echoes a sensitive argument.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (await process.StandardError.ReadLineAsync() is { } line)
                            _logger.LogDebug("ssh[{Server}]: {Line}", server.Name, SecretRedactor.Redact(line));
                    }
                    catch { /* stream closed on tunnel teardown */ }
                });

                var tunnel = new SshTunnel(server.Id, localPort, process);
                _tunnels[server.Id] = tunnel;
                _logger.LogInformation("SSH tunnel established for {ServerName} on local port {Port}", server.Name, localPort);
                return localPort;
            }

            var error = process.HasExited ? await process.StandardError.ReadToEndAsync() : "tunnel did not open the local port in time";
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            lastError = new InvalidOperationException($"SSH tunnel failed for {server.Name}: {error}");
            _logger.LogWarning("SSH tunnel attempt {Attempt}/{Max} for {ServerName} on port {Port} failed: {Error}",
                attempt, maxAttempts, server.Name, localPort, SecretRedactor.Redact(error));

            var portRace = error.Contains("bind", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
                || error.Contains("cannot listen", StringComparison.OrdinalIgnoreCase);
            if (!portRace)
                break; // not a port collision — a fresh port won't help, fail fast
        }

        throw lastError ?? new InvalidOperationException($"SSH tunnel failed for {server.Name}");
    }

    /// <summary>Builds and starts one ssh port-forward process. Options chosen so a broken tunnel dies
    /// fast and cleanly: ExitOnForwardFailure (exit immediately if the local forward can't bind),
    /// ServerAlive* keepalives (terminate a half-dead link after ~45s), BatchMode (never block on a
    /// prompt). StrictHostKeyChecking stays as-is (HOCH-11 / ADR-0002).</summary>
    private static Process StartSshProcess(Models.ServerConfig server, string? keyPath, int localPort)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(keyPath))
        {
            args.Add("-i");
            args.Add(keyPath);
        }
        args.AddRange(new[]
        {
            "-N",
            "-L", $"{localPort}:/var/run/docker.sock",
            $"{server.SshUser}@{server.SshHost}",
            "-p", server.SshPort.ToString(),
            "-o", "StrictHostKeyChecking=no",
            "-o", "BatchMode=yes",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "-o", "TCPKeepAlive=yes",
            "-o", "ConnectTimeout=10",
        });

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
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

    /// <summary>
    /// Polls the local forward port until it accepts a TCP connection, the ssh process exits, or the
    /// timeout elapses. Returns true only when the port is actually listening.
    /// </summary>
    private static async Task<bool> WaitForPortAsync(int port, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                return false;

            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                return true;
            }
            catch
            {
                await Task.Delay(200);
            }
        }
        return false;
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
