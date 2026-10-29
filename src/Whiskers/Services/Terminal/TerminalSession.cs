using System.Diagnostics;
using Whiskers.Utils;

namespace Whiskers.Services.Terminal;

/// <summary>Who opened a terminal session: the principal id the gateway decided on (<c>user:&lt;email&gt;</c>) and the
/// name the audit shows.</summary>
public sealed record TerminalOwner(string Id, string DisplayName)
{
    /// <summary>The signed-in user, as the gateway names them (the same principal id the open is decided for).</summary>
    public static async Task<TerminalOwner> OfAsync(Whiskers.Services.Auth.ICurrentUserService user)
    {
        var email = await user.GetEmailAsync();
        return string.IsNullOrWhiteSpace(email)
            ? new(Whiskers.Services.Mutations.PrincipalIds.Anonymous, "unknown")
            : new(Whiskers.Services.Mutations.PrincipalIds.User(email), email);
    }
}

/// <summary>What a session is a shell of: a host (<see cref="ContainerId"/> null) or a container on a server.</summary>
public sealed record TerminalTarget(string ServerId, string? ContainerId, string Name)
{
    /// <summary>The audit's target kind: <c>host</c> or <c>container</c>.</summary>
    public string Kind => ContainerId is null ? "host" : "container";
}

/// <summary>Why a session ended; recorded on its one close event (INV-47).</summary>
public enum TerminalCloseReason
{
    /// <summary>The user left the page or closed the terminal.</summary>
    Closed,
    /// <summary>The shell ended by itself.</summary>
    Exited,
    /// <summary>No input or output for longer than the idle timeout.</summary>
    Idle,
    /// <summary>The owner no longer holds Admin (role lowered, removed, not allowed to sign in).</summary>
    Revoked,
    /// <summary>The read-only kill switch was switched on.</summary>
    KillSwitch,
    /// <summary>Terminals were switched off in the settings.</summary>
    Disabled,
    /// <summary>Whiskers shut down.</summary>
    Shutdown,
}

/// <summary>The program a session runs and its arguments (never through a shell on Whiskers' side).</summary>
public sealed record TerminalLaunch(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>A shell in a local container (<c>script</c> gives it a PTY), or on the host itself: Whiskers runs with
    /// <c>pid: host</c>, so <c>nsenter</c> into PID 1's namespaces is a host shell.</summary>
    public static TerminalLaunch Local(string shell, string? containerId) => string.IsNullOrEmpty(containerId)
        ? new("nsenter", ["-t", "1", "-m", "-u", "-i", "-n", "-p", "--", shell, "-i", "-l"])
        : new("docker", ["exec", "-i", containerId, "script", "-qc", $"{shell} -i", "/dev/null"]);

    /// <summary>A shell on a remote host over SSH (<paramref name="keyPath"/> null: keyless, Tailscale SSH).</summary>
    public static TerminalLaunch Ssh(string host, int port, string user, string? keyPath)
    {
        var args = SshBaseArgs(port, keyPath);
        args.Add($"{user}@{host}");
        return new("ssh", args);
    }

    /// <summary>A shell in a container of a remote host: SSH, then <c>docker exec</c>. The remote command is one
    /// argument quoted for the remote shell, so the container id survives whatever it contains.</summary>
    public static TerminalLaunch SshDockerExec(string host, int port, string user, string? keyPath, string containerId)
    {
        var args = SshBaseArgs(port, keyPath);
        args.Add($"{user}@{host}");
        args.Add($"docker exec -it {ShellUtils.Quote(containerId)} /bin/sh -c " +
                 ShellUtils.Quote("command -v bash >/dev/null && exec bash -l || exec sh"));
        return new("ssh", args);
    }

    private static List<string> SshBaseArgs(int port, string? keyPath)
    {
        var args = new List<string> { "-tt", "-o", "ServerAliveInterval=30", "-p", port.ToString() };
        // TOFU host-key verification (HOCH-11 / ADR-0002) — shared policy with tunnel + executor.
        args.AddRange(Whiskers.Services.Server.SshHostKeyPolicy.Options());
        if (!string.IsNullOrEmpty(keyPath))
        {
            args.Add("-i");
            args.Add(keyPath);
        }
        return args;
    }
}

/// <summary>A running shell: its output streams, its input, and whether it ended.</summary>
public interface ITerminalProcess : IAsyncDisposable
{
    StreamReader Output { get; }
    StreamReader Errors { get; }
    bool HasExited { get; }
    Task WriteAsync(string data);
}

/// <summary>Starts the program of a session.</summary>
public interface ITerminalLauncher
{
    ITerminalProcess Start(TerminalLaunch launch);
}

/// <summary>Starts a session as an OS process with redirected streams.</summary>
public sealed class ProcessTerminalLauncher : ITerminalLauncher
{
    public ITerminalProcess Start(TerminalLaunch launch)
    {
        var psi = new ProcessStartInfo
        {
            FileName = launch.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in launch.Arguments)
            psi.ArgumentList.Add(arg);
        psi.Environment["TERM"] = "xterm-256color";
        var process = new Process { StartInfo = psi };
        process.Start();
        return new OsProcess(process);
    }

    private sealed class OsProcess(Process process) : ITerminalProcess
    {
        public StreamReader Output => process.StandardOutput;
        public StreamReader Errors => process.StandardError;
        public bool HasExited => process.HasExited;

        public async Task WriteAsync(string data)
        {
            await process.StandardInput.WriteAsync(data);
            await process.StandardInput.FlushAsync();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// One web-terminal session (R1-17): a host or container shell opened through the Mutation Gateway on the authority
/// of <see cref="Owner"/>. Only <see cref="ITerminalSessionManager"/> creates and ends one; it records the close.
/// What is typed is never recorded (PC-D2).
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly ITerminalProcess _process;

    internal TerminalSession(string sessionId, TerminalOwner owner, TerminalTarget target, ITerminalProcess process,
        DateTime openedAt)
    {
        SessionId = sessionId;
        Owner = owner;
        Target = target;
        _process = process;
        CreatedAt = openedAt;
        LastActivityAt = openedAt;
    }

    /// <summary>A new session id; the page hands it to the gateway so the open and the close event carry it.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    public string SessionId { get; }
    public TerminalOwner Owner { get; }
    public TerminalTarget Target { get; }
    public string? ContainerId => Target.ContainerId;
    public DateTime CreatedAt { get; }
    public DateTime LastActivityAt { get; private set; }
    public bool IsRunning => !_process.HasExited;
    public StreamReader Output => _process.Output;
    public StreamReader Errors => _process.Errors;

    /// <summary>Marks the session active now (input OR output) so the idle sweep doesn't reap it.</summary>
    public void Touch() => LastActivityAt = DateTime.UtcNow;

    public async Task WriteAsync(string data)
    {
        if (_process.HasExited) return;
        Touch();
        await _process.WriteAsync(data);
    }

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
