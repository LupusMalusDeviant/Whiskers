using System.Diagnostics;

namespace ServerWatch.Services.Terminal;

public class TerminalSession : IAsyncDisposable
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string? ContainerId { get; init; }
    public Process? Process { get; private set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public bool IsRunning => Process is { HasExited: false };

    public void Start(string shell, string? containerId = null)
    {
        string fileName;
        string arguments;

        if (!string.IsNullOrEmpty(containerId))
        {
            // Use 'script' to allocate a PTY inside docker exec
            // This gives us proper interactive shell with prompt, colors, etc.
            fileName = "docker";
            arguments = $"exec -i {containerId} script -qc \"{shell} -i\" /dev/null";
        }
        else
        {
            // nsenter into the host PID namespace to get a real host shell
            // (ServerWatch runs as a container with pid:host and privileged)
            fileName = "nsenter";
            arguments = $"-t 1 -m -u -i -n -p -- {shell} -i -l";
        }

        Process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        Process.StartInfo.Environment["TERM"] = "xterm-256color";
        Process.Start();
    }

    public void StartSsh(string host, int port, string user, string? keyPath)
    {
        var args = $"-tt -o StrictHostKeyChecking=no -o ServerAliveInterval=30 -p {port}";
        if (!string.IsNullOrEmpty(keyPath))
            args += $" -i {keyPath}";
        args += $" {user}@{host}";

        Process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        Process.StartInfo.Environment["TERM"] = "xterm-256color";
        Process.Start();
    }

    public void StartSshDockerExec(string host, int port, string user, string? keyPath, string containerId)
    {
        // SSH into remote host, then docker exec into the container
        var args = $"-tt -o StrictHostKeyChecking=no -o ServerAliveInterval=30 -p {port}";
        if (!string.IsNullOrEmpty(keyPath))
            args += $" -i {keyPath}";
        args += $" {user}@{host} docker exec -it {containerId} /bin/sh -c 'command -v bash >/dev/null && exec bash -l || exec sh'";

        Process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        Process.StartInfo.Environment["TERM"] = "xterm-256color";
        Process.Start();
    }

    public async Task WriteAsync(string data)
    {
        if (Process?.StandardInput != null)
        {
            LastActivityAt = DateTime.UtcNow;
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
