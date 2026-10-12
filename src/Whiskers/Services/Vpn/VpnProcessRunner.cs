using System.Diagnostics;
using System.Text;

namespace Whiskers.Services.Vpn;

/// <summary>Small helper for shelling out to VPN CLIs (tailscale/netbird) and starting daemons.</summary>
internal static class VpnProcessRunner
{
    public record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
    }

    /// <summary>Run a command to completion, capturing output. Returns exit code 127 if the binary is missing.</summary>
    public static async Task<Result> RunAsync(string file, string arguments, CancellationToken ct, int timeoutMs = 30000,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Enrollment secrets are passed via env, never argv, so they don't appear in the process list.
        if (env != null)
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            // Binary not found / not executable — surface as a sentinel so callers can degrade gracefully.
            return new Result(127, "", ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new Result(124, stdout.ToString(), "timeout");
        }

        return new Result(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Start a long-running daemon detached. Its output goes to the debug log (redacted) and an exit is
    /// logged as a warning — a daemon that died right after start used to vanish without a trace. Returns false
    /// if the binary can't be launched.</summary>
    public static bool StartDetached(string file, string arguments, ILogger logger, string? logName = null)
    {
        var name = logName ?? file;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            // Drain the pipes (a full buffer would block the daemon) and keep the lines in the debug log.
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) logger.LogDebug("[{Daemon}] {Line}", name, Utils.SecretRedactor.Redact(e.Data)); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) logger.LogDebug("[{Daemon}] {Line}", name, Utils.SecretRedactor.Redact(e.Data)); };
            proc.Exited += (_, _) =>
            {
                try { logger.LogWarning("[{Daemon}] exited with code {Code}", name, proc.ExitCode); }
                catch (InvalidOperationException) { /* process object already released */ }
            };
            if (!proc.Start()) return false;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start detached process {File}", file);
            return false;
        }
    }
}
