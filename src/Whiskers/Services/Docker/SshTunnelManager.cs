using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;
using Whiskers.Utils;

namespace Whiskers.Services.Docker;

public class SshTunnelManager : ISshTunnelManager
{
    private readonly IServerConfigService _serverConfig;
    private readonly ILogger<SshTunnelManager> _logger;
    private readonly ConcurrentDictionary<string, SshTunnel> _tunnels = new();

    // Contract 0015 (D2): the key is sealed at rest; ssh gets a lease in a memory-backed directory for its handshake.
    private readonly Whiskers.Services.Server.ISshKeyLeases? _keyLeases;

    /// <summary>Where the forwarded sockets live: a directory only Whiskers' user can enter (0700), each socket 0600
    /// (Fable review M: a loopback TCP port in front of the host's real docker.sock is root on that host for anything
    /// that can connect to 127.0.0.1, e.g. another process or a host-network container).</summary>
    internal string SocketDir { get; init; } = Path.Combine(Path.GetTempPath(), $"whiskers-tunnels-{Environment.ProcessId}");

    /// <summary>Unix sockets where the platform has them for ssh and the Docker client; a loopback TCP port only on
    /// Windows (development).</summary>
    internal bool UseUnixSockets { get; init; } = !OperatingSystem.IsWindows();

    public SshTunnelManager(IServerConfigService serverConfig, ILogger<SshTunnelManager> logger,
        Whiskers.Services.Server.ISshKeyLeases? keyLeases = null)
    {
        _serverConfig = serverConfig;
        _logger = logger;
        _keyLeases = keyLeases;
    }

    public async Task<Uri> EstablishTunnelAsync(Models.ServerConfig server)
    {
        if (_tunnels.TryGetValue(server.Id, out var existing) && existing.IsAlive)
            return existing.Endpoint;

        // Close stale tunnel
        CloseTunnel(server.Id);

        var keyPath = _serverConfig.GetSshKeyPath(server);

        // Validate the key is present before spawning ssh, so a missing key surfaces as a clear, actionable error
        // instead of a generic "connection failed" (ssh would silently die and the tunnel port would just be dead).
        // Whether it can be opened is said by the lease below (sealed key, vault locked, no memory-backed directory).
        if (!string.IsNullOrEmpty(server.SshKeyFileName) && keyPath == null)
            throw new InvalidOperationException(
                $"SSH key '{server.SshKeyFileName}' for server '{server.Name}' was not found in the ssh-keys directory. Re-upload the key for this server.");
        if (keyPath is not null && _keyLeases is null)
            throw new InvalidOperationException("SSH keys are sealed at rest and no key lease service is configured.");

        // A TCP port is picked and released again before ssh binds it — a TOCTOU race where another process can grab
        // it in between. Retry a few times with a fresh port when ssh reports a bind failure (ExitOnForwardFailure
        // makes it exit immediately in that case). A socket path is fresh per attempt.
        const int maxAttempts = 3;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var forward = NewForward(server.Id);
            // The lease lives until ssh has authenticated (the forward is up) or has given up — never longer.
            Process process;
            bool up;
            var lease = keyPath is null ? null : await _keyLeases!.AcquireAsync(server, CancellationToken.None);
            try
            {
                process = StartSshProcess(server, lease?.Path, forward.SshSpec);

                // Wait until the forward actually accepts connections (up to ~10s) rather than guessing with a fixed
                // delay. ssh opens a local forward only after it authenticated.
                up = await WaitForForwardAsync(forward, process, TimeSpan.FromSeconds(10));
            }
            finally
            {
                if (lease is not null) await lease.DisposeAsync();
            }
            if (up)
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

                var tunnel = new SshTunnel(server.Id, forward, process);
                _tunnels[server.Id] = tunnel;
                _logger.LogInformation("SSH tunnel established for {ServerName} at {Endpoint}", server.Name, forward.Endpoint);
                return forward.Endpoint;
            }

            var error = process.HasExited ? await process.StandardError.ReadToEndAsync() : "tunnel did not open its local forward in time";
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            forward.Remove();
            lastError = new InvalidOperationException($"SSH tunnel failed for {server.Name}: {error}");
            _logger.LogWarning("SSH tunnel attempt {Attempt}/{Max} for {ServerName} at {Endpoint} failed: {Error}",
                attempt, maxAttempts, server.Name, forward.Endpoint, SecretRedactor.Redact(error));

            var portRace = error.Contains("bind", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
                || error.Contains("cannot listen", StringComparison.OrdinalIgnoreCase);
            if (!portRace)
                break; // not a port collision — a fresh port won't help, fail fast
        }

        throw lastError ?? new InvalidOperationException($"SSH tunnel failed for {server.Name}");
    }

    /// <summary>One local end of a tunnel: what ssh is told (<c>-L spec:/var/run/docker.sock</c>) and where the Docker
    /// client connects.</summary>
    internal sealed record LocalForward(string SshSpec, Uri Endpoint, string? SocketPath, int? Port)
    {
        public void Remove()
        {
            if (SocketPath is null) return;
            try { File.Delete(SocketPath); } catch { /* best effort */ }
        }
    }

    internal LocalForward NewForward(string serverId)
    {
        if (!UseUnixSockets)
        {
            var port = GetAvailablePort();
            return new LocalForward(port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Uri($"http://127.0.0.1:{port}"), null, port);
        }

        if (!OperatingSystem.IsWindows())
            Directory.CreateDirectory(SocketDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(SocketDir);
        // Short and free of the id's characters: a socket path is limited to ~108 bytes.
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverId)))[..16].ToLowerInvariant()
                   + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + ".sock";
        var path = Path.Combine(SocketDir, name);
        return new LocalForward(path, new Uri("unix://" + path), path, null);
    }

    /// <summary>Builds and starts one ssh forward process. Options chosen so a broken tunnel dies
    /// fast and cleanly: ExitOnForwardFailure (exit immediately if the local forward can't bind),
    /// ServerAlive* keepalives (terminate a half-dead link after ~45s), BatchMode (never block on a
    /// prompt). Host keys are verified TOFU-style via <see cref="Services.Server.SshHostKeyPolicy"/>
    /// (HOCH-11 fixed per user approval 2026-07-11; see ADR-0002).</summary>
    private static Process StartSshProcess(Models.ServerConfig server, string? keyPath, string localForward)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in BuildTunnelArguments(server, keyPath, localForward))
            psi.ArgumentList.Add(arg);
        Whiskers.Utils.SecretEnvironment.Scrub(psi);   // R1-43: no VPN key in a child's environment

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    /// <summary>The ssh arguments of a tunnel (R1-42): every option first, then <c>--</c> and the checked
    /// <c>user@host</c> destination. Throws <see cref="Whiskers.Utils.UnsafeOperandException"/> for a user, host or
    /// port that <see cref="Whiskers.Utils.CommandOperands"/> refuses — no process is started for it.
    /// <paramref name="localForward"/> is a socket path (the socket is made 0600) or, on Windows, a loopback port.</summary>
    internal static List<string> BuildTunnelArguments(Models.ServerConfig server, string? keyPath, string localForward)
    {
        var destination = Whiskers.Utils.CommandOperands.SshDestination(server.SshUser, server.SshHost, server.SshPort);
        var args = new List<string>();
        if (!string.IsNullOrEmpty(keyPath))
        {
            args.Add("-i");
            args.Add(keyPath);
        }
        args.AddRange(Services.Server.SshHostKeyPolicy.Options());
        args.AddRange(new[]
        {
            "-N",
            "-L", $"{localForward}:/var/run/docker.sock",
            "-o", "StreamLocalBindMask=0177",
            "-o", "StreamLocalBindUnlink=yes",
            "-p", server.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-o", "BatchMode=yes",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "-o", "TCPKeepAlive=yes",
            "-o", "ConnectTimeout=10",
            // "--" ends ssh's options; the destination is an operand whatever it contains.
            "--",
            destination,
        });
        return args;
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
    /// Polls the local forward until it accepts a connection, the ssh process exits, or the timeout elapses. Returns
    /// true only when the forward is actually listening.
    /// </summary>
    private static async Task<bool> WaitForForwardAsync(LocalForward forward, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                return false;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                if (forward.SocketPath is { } path)
                {
                    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token);
                }
                else
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, forward.Port!.Value, cts.Token);
                }
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
        private readonly LocalForward _forward;
        private readonly Process _process;

        public Uri Endpoint => _forward.Endpoint;
        public bool IsAlive => !_process.HasExited;

        public SshTunnel(string serverId, LocalForward forward, Process process)
        {
            ServerId = serverId;
            _forward = forward;
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
            _forward.Remove();
        }
    }
}
