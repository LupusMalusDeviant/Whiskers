using System.Diagnostics;
using Whiskers.Utils;

namespace Whiskers.Services.Terminal;

public class TerminalSession : IAsyncDisposable
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string? ContainerId { get; init; }
    public Process? Process { get; private set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public bool IsRunning => Process is { HasExited: false };

    /// <summary>Marks the session active now (input OR output) so the idle sweep doesn't reap it.</summary>
    public void Touch() => LastActivityAt = DateTime.UtcNow;

    public void Start(string shell, string? containerId = null)
    {
        string fileName;
        string[] args;

        if (!string.IsNullOrEmpty(containerId))
        {
            // Use 'script' to allocate a PTY inside docker exec
            // This gives us proper interactive shell with prompt, colors, etc.
            fileName = "docker";
            args = new[] { "exec", "-i", containerId, "script", "-qc", $"{shell} -i", "/dev/null" };
        }
        else
        {
            // nsenter into the host PID namespace to get a real host shell
            // (Whiskers runs as a container with pid:host and privileged)
            fileName = "nsenter";
            args = new[] { "-t", "1", "-m", "-u", "-i", "-n", "-p", "--", shell, "-i", "-l" };
        }

        StartProcess(fileName, args);
    }

    public void StartSsh(string host, int port, string user, string? keyPath)
    {
        var args = SshBaseArgs(port, keyPath);
        args.Add($"{user}@{host}");

        StartProcess("ssh", args);
    }

    public void StartSshDockerExec(string host, int port, string user, string? keyPath, string containerId)
    {
        // SSH into remote host, then docker exec into the container. The remote command is built as a
        // single argv element and quoted for the *remote* shell — ssh forwards it verbatim, so the
        // inner quoting (and the container id) survive intact regardless of special characters.
        var args = SshBaseArgs(port, keyPath);
        args.Add($"{user}@{host}");
        args.Add(
            $"docker exec -it {ShellUtils.Quote(containerId)} /bin/sh -c " +
            ShellUtils.Quote("command -v bash >/dev/null && exec bash -l || exec sh"));

        StartProcess("ssh", args);
    }

    private static List<string> SshBaseArgs(int port, string? keyPath)
    {
        var args = new List<string>
        {
            "-tt",
            "-o", "ServerAliveInterval=30",
            "-p", port.ToString(),
        };
        // TOFU host-key verification (HOCH-11 / ADR-0002) — shared policy with tunnel + executor.
        args.AddRange(Whiskers.Services.Server.SshHostKeyPolicy.Options());
        if (!string.IsNullOrEmpty(keyPath))
        {
            args.Add("-i");
            args.Add(keyPath);
        }
        return args;
    }

    private void StartProcess(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process = new Process { StartInfo = psi };
        Process.StartInfo.Environment["TERM"] = "xterm-256color";
        Process.Start();
    }

    public async Task WriteAsync(string data)
    {
        if (Process?.StandardInput != null)
        {
            Touch();
            await Process.StandardInput.WriteAsync(data);
            await Process.StandardInput.FlushAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Process != null)
        {
            try
            {
                if (!Process.HasExited)
                    Process.Kill(entireProcessTree: true);
            }
            catch { }
            Process.Dispose();
        }
        GC.SuppressFinalize(this);
        await Task.CompletedTask;
    }
}
