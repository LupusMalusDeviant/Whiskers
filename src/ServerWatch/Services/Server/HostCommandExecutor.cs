using System.Diagnostics;
using ServerWatch.Models;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.Server;

public class HostCommandExecutor : IHostCommandExecutor
{
    private readonly ServerConfigService _serverConfigService;
    private readonly ILogger<HostCommandExecutor> _logger;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Directory holding SSH connection-multiplexing control sockets. Reusing a master connection
    // across calls avoids a fresh TCP+auth handshake on every command (hundreds of ms each).
    private static readonly string ControlDir =
        Path.Combine(Path.GetTempPath(), "serverwatch-ssh");

    public HostCommandExecutor(ServerConfigService serverConfigService, ILogger<HostCommandExecutor> logger)
    {
        _serverConfigService = serverConfigService;
        _logger = logger;
    }

    public async Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverId))
            throw new ArgumentException("serverId cannot be null or empty", nameof(serverId));
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("command cannot be null or empty", nameof(command));

        var server = _serverConfigService.GetServer(serverId);
        if (server == null)
            return new CommandResult { ExitCode = -1, Error = $"Server '{serverId}' not found" };

        var effectiveTimeout = timeout ?? DefaultTimeout;

        // The shell plane is independent of the Docker connection transport: a server can talk Docker
        // over mTLS (ConnectionType.TCP) while shell commands still go over SSH. So use SSH for any
        // non-local server that has SSH configured, regardless of ConnectionType. (Hardening the SSH
        // shell plane itself — short-lived certs — is a separate later step.)
        return server.ConnectionType switch
        {
            ConnectionType.Local => await ExecuteLocalAsync(command, effectiveTimeout, ct),
            _ when !string.IsNullOrWhiteSpace(server.SshHost)
                => await ExecuteSshAsync(server, command, effectiveTimeout, ct),
            _ => new CommandResult { ExitCode = -1, Error = $"No shell transport for server (ConnectionType={server.ConnectionType}, no SSH host configured)" }
        };
    }

    private async Task<CommandResult> ExecuteLocalAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        // Break out of the container's namespaces into the host, then hand the command verbatim to a
        // shell as a single argument. Passing it as one argv element (not a concatenated string) means
        // .NET does no word-splitting and the shell sees exactly what the caller wrote — pipes,
        // quotes, $vars and && all behave as intended.
        var args = new[] { "-t", "1", "-m", "-u", "-i", "-n", "-p", "--", "sh", "-c", command };

        _logger.LogDebug("Executing local command via nsenter: {Command}", command);

        return await RunProcessAsync("nsenter", args, timeout, ct);
    }

    private async Task<CommandResult> ExecuteSshAsync(Models.ServerConfig server, string command, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.SshHost))
            return new CommandResult { ExitCode = -1, Error = "SSH host is not configured" };
        if (string.IsNullOrWhiteSpace(server.SshUser))
            return new CommandResult { ExitCode = -1, Error = "SSH user is not configured" };

        var keyPath = _serverConfigService.GetSshKeyPath(server);
        if (string.IsNullOrWhiteSpace(keyPath))
            return new CommandResult { ExitCode = -1, Error = "SSH key not found for server" };

        try { Directory.CreateDirectory(ControlDir); } catch { /* best effort; ssh falls back to no mux */ }

        var args = new List<string>
        {
            "-o", "StrictHostKeyChecking=no",
            "-o", "ConnectTimeout=10",
            // Connection multiplexing: the first call opens a master, subsequent calls within
            // ControlPersist reuse it. %C is a safe hash of host/port/user, so no escaping concerns.
            "-o", "ControlMaster=auto",
            "-o", "ControlPersist=60s",
            "-o", $"ControlPath={Path.Combine(ControlDir, "cm-%C")}",
            "-p", server.SshPort.ToString(),
            "-i", keyPath,
            $"{server.SshUser}@{server.SshHost}",
            // The command is a single argv element: ssh forwards it verbatim to the remote login
            // shell, so a full shell command string (pipes, redirects, &&) works as written.
            command
        };

        _logger.LogDebug("Executing SSH command on {Host}: {Command}", server.SshHost, command);

        return await RunProcessAsync("ssh", args, timeout, ct);
    }

    private async Task<CommandResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                return new CommandResult
                {
                    ExitCode = process.ExitCode,
                    Output = stdout,
                    Error = stderr
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timed out (not externally cancelled)
                _logger.LogWarning("Command timed out after {Timeout}: {FileName} {Arguments}", timeout, fileName, string.Join(' ', psi.ArgumentList));
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new CommandResult { ExitCode = -1, Error = $"Command timed out after {timeout.TotalSeconds}s" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start process {FileName}", fileName);
            return new CommandResult { ExitCode = -1, Error = ex.Message };
        }
    }
}
