using System.Diagnostics;
using ServerWatch.Models;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.Server;

public class HostCommandExecutor : IHostCommandExecutor
{
    private readonly ServerConfigService _serverConfigService;
    private readonly ILogger<HostCommandExecutor> _logger;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

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

        return server.ConnectionType switch
        {
            ConnectionType.Local => await ExecuteLocalAsync(command, effectiveTimeout, ct),
            ConnectionType.SSH => await ExecuteSshAsync(server, command, effectiveTimeout, ct),
            _ => new CommandResult { ExitCode = -1, Error = $"Unsupported connection type: {server.ConnectionType}" }
        };
    }

    private async Task<CommandResult> ExecuteLocalAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        // Use nsenter to break out of the container's namespaces into the host
        var nsenterArgs = $"-t 1 -m -u -i -n -p -- sh -c \"{EscapeForShell(command)}\"";

        _logger.LogDebug("Executing local command via nsenter: {Command}", command);

        return await RunProcessAsync("nsenter", nsenterArgs, timeout, ct);
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

        var sshArgs = $"-o StrictHostKeyChecking=no -o ConnectTimeout=10 -p {server.SshPort} -i \"{keyPath}\" {server.SshUser}@{server.SshHost} {command}";

        _logger.LogDebug("Executing SSH command on {Host}: {Command}", server.SshHost, command);

        return await RunProcessAsync("ssh", sshArgs, timeout, ct);
    }

    private async Task<CommandResult> RunProcessAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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
                _logger.LogWarning("Command timed out after {Timeout}: {FileName} {Arguments}", timeout, fileName, arguments);
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

    private static string EscapeForShell(string command)
    {
        // Escape double quotes within the command so it can be passed via sh -c "..."
        return command.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
