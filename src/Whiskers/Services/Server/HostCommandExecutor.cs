using System.Diagnostics;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.ServerConfig;
using Whiskers.Utils;

namespace Whiskers.Services.Server;

public class HostCommandExecutor : IHostCommandExecutor
{
    private readonly IServerConfigService _serverConfigService;
    private readonly IDockerService _docker;
    private readonly ILogger<HostCommandExecutor> _logger;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Cap how much we buffer per stream so a runaway command (e.g. `cat` on a huge file) can't OOM
    // the process. Output beyond this is drained but discarded, with a marker appended.
    private const int MaxOutputChars = 1024 * 1024; // ~1 MB per stream
    private const string TruncationMarker = "… (Ausgabe gekürzt)";

    // Directory holding SSH connection-multiplexing control sockets. Reusing a master connection
    // across calls avoids a fresh TCP+auth handshake on every command (hundreds of ms each).
    private static readonly string ControlDir =
        Path.Combine(Path.GetTempPath(), "serverwatch-ssh");

    public HostCommandExecutor(IServerConfigService serverConfigService, IDockerService docker, ILogger<HostCommandExecutor> logger)
    {
        _serverConfigService = serverConfigService;
        _docker = docker;
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

        // Shell transport, chosen independently of the Docker connection:
        //   Local → nsenter into the host directly.
        //   TCP   → SSH-free: run the command in a one-shot privileged nsenter container over the
        //           mTLS Docker connection (no SSH key involved).
        //   else  → SSH, if an SSH host is configured (legacy / bootstrap).
        return server.ConnectionType switch
        {
            ConnectionType.Local => await ExecuteLocalAsync(command, effectiveTimeout, ct),
            ConnectionType.TCP => await ExecuteViaDockerAsync(server.Id, command, effectiveTimeout),
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

        _logger.LogDebug("Executing local command via nsenter: {Command}", SecretRedactor.Redact(command));

        return await RunProcessAsync("nsenter", args, timeout, ct);
    }

    // SSH-free shell for TCP+mTLS servers: drive a one-shot privileged nsenter container over the
    // mTLS Docker connection. Same host-namespace effect as ExecuteLocalAsync, but remote and
    // without any SSH key.
    private async Task<CommandResult> ExecuteViaDockerAsync(string serverId, string command, TimeSpan timeout)
    {
        _logger.LogDebug("Executing host command on {ServerId} via mTLS Docker (nsenter container): {Command}", serverId, SecretRedactor.Redact(command));
        try
        {
            var (output, error, exitCode) = await _docker.RunHostShellAsync(command, serverId, timeout);
            return new CommandResult { ExitCode = exitCode, Output = output, Error = error };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Host command via Docker failed on {ServerId}", serverId);
            return new CommandResult { ExitCode = -1, Error = ex.Message };
        }
    }

    private async Task<CommandResult> ExecuteSshAsync(Models.ServerConfig server, string command, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.SshHost))
            return new CommandResult { ExitCode = -1, Error = "SSH host is not configured" };
        if (string.IsNullOrWhiteSpace(server.SshUser))
            return new CommandResult { ExitCode = -1, Error = "SSH user is not configured" };

        // Bootstrap auth: a transient root/SSH password (one-time onboarding) takes precedence;
        // otherwise the stored key. Password is fed to sshpass via the SSHPASS env var below.
        var usePassword = !string.IsNullOrEmpty(server.SshPassword);
        string? keyPath = null;
        if (!usePassword)
        {
            keyPath = _serverConfigService.GetSshKeyPath(server);
            if (string.IsNullOrWhiteSpace(keyPath))
                return new CommandResult { ExitCode = -1, Error = "SSH key not found for server" };
        }

        try { Directory.CreateDirectory(ControlDir); } catch { /* best effort; ssh falls back to no mux */ }

        // TOFU host-key verification (HOCH-11 / ADR-0002) — shared policy with tunnel + terminal.
        var args = new List<string>(SshHostKeyPolicy.Options())
        {
            "-o", "ConnectTimeout=10",
            // Connection multiplexing: the first call opens a master, subsequent calls within
            // ControlPersist reuse it. %C is a safe hash of host/port/user, so no escaping concerns.
            "-o", "ControlMaster=auto",
            "-o", "ControlPersist=60s",
            "-o", $"ControlPath={Path.Combine(ControlDir, "cm-%C")}",
            "-p", server.SshPort.ToString(),
        };
        if (usePassword)
        {
            args.AddRange(new[] { "-o", "PreferredAuthentications=password", "-o", "PubkeyAuthentication=no", "-o", "NumberOfPasswordPrompts=1" });
        }
        else
        {
            args.Add("-i");
            args.Add(keyPath!);
        }
        args.Add($"{server.SshUser}@{server.SshHost}");
        // The command is a single argv element: ssh forwards it verbatim to the remote login shell,
        // so a full shell command string (pipes, redirects, &&) works as written.
        args.Add(command);

        _logger.LogDebug("Executing SSH command on {Host} ({Auth}): {Command}",
            server.SshHost, usePassword ? "password" : "key", SecretRedactor.Redact(command));

        if (usePassword)
            // sshpass -e reads the password from SSHPASS — never on the command line / process list.
            return await RunProcessAsync("sshpass", new[] { "-e", "ssh" }.Concat(args), timeout, ct,
                env: ("SSHPASS", server.SshPassword!));

        return await RunProcessAsync("ssh", args, timeout, ct);
    }

    private async Task<CommandResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct,
        (string Key, string Value)? env = null)
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
        if (env is { } e)
            psi.Environment[e.Key] = e.Value;

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var stdoutTask = ReadCappedAsync(process.StandardOutput, cts.Token);
            var stderrTask = ReadCappedAsync(process.StandardError, cts.Token);

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
            catch (OperationCanceledException)
            {
                // Cancelled — by our timeout (CancelAfter) OR by the caller's token. Either way kill the
                // whole process tree so an aborted `ufw disable` / `systemctl stop` can't keep running,
                // and observe the read tasks so they don't fault unobserved.
                var timedOut = !ct.IsCancellationRequested;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                try { await Task.WhenAll(stdoutTask, stderrTask); } catch { /* reads cancel with the token */ }

                if (timedOut)
                {
                    _logger.LogWarning("Command timed out after {Timeout}: {FileName} {Arguments}",
                        timeout, fileName, SecretRedactor.Redact(string.Join(' ', psi.ArgumentList)));
                    return new CommandResult { ExitCode = -1, Error = $"Command timed out after {timeout.TotalSeconds}s" };
                }

                _logger.LogWarning("Command cancelled: {FileName}", fileName);
                return new CommandResult { ExitCode = -1, Error = "Command cancelled" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start process {FileName}", fileName);
            return new CommandResult { ExitCode = -1, Error = ex.Message };
        }
    }

    // Reads a stream but buffers at most MaxOutputChars; any further output is read and discarded
    // (so the child process never blocks on a full pipe) and a marker is appended. Applies to every
    // caller of RunProcessAsync (local nsenter + SSH), so firewall/nginx/systemd/etc. are all capped.
    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[8192];
        var sb = new System.Text.StringBuilder();
        var truncated = false;

        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (!truncated)
            {
                var remaining = MaxOutputChars - sb.Length;
                if (read <= remaining)
                {
                    sb.Append(buffer, 0, read);
                }
                else
                {
                    if (remaining > 0)
                        sb.Append(buffer, 0, remaining);
                    truncated = true;
                }
            }
            // Once truncated, keep draining the stream to unblock the writer but store nothing.
        }

        if (truncated)
            sb.Append('\n').Append(TruncationMarker);

        return sb.ToString();
    }
}
