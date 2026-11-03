using System.Diagnostics;
using System.Text;

namespace Whiskers.Services.Vpn;

/// <summary>What a VPN CLI call returned. Exit code 127: the binary could not be started; 124: it timed out.</summary>
public sealed record VpnProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs the VPN CLIs (tailscale/netbird) and starts their daemons (R1-43, INV-41). Every argument is its own argv
/// element (<see cref="ProcessStartInfo.ArgumentList"/>): nothing is joined into one command string that the runtime
/// would split again, so a value with a space, a quote or a leading <c>-</c> stays one element. No secret is ever an
/// argument: enrollment keys reach the CLIs as a file (<see cref="VpnSecretFile"/>).
/// </summary>
public interface IVpnProcessRunner
{
    /// <summary>Runs <paramref name="file"/> to completion and captures its output.</summary>
    Task<VpnProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct, int timeoutMs = 30000);

    /// <summary>Starts a long-running daemon detached; false when the binary cannot be started.</summary>
    bool StartDetached(string file, IReadOnlyList<string> arguments, ILogger logger, string? logName = null);
}

/// <inheritdoc />
public sealed class VpnProcessRunner : IVpnProcessRunner
{
    /// <summary>The runner the providers use when none is injected.</summary>
    public static readonly VpnProcessRunner Instance = new();

    /// <summary>The start information for one call: each argument a separate argv element. Internal for tests.</summary>
    internal static ProcessStartInfo StartInfo(string file, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument ?? throw new ArgumentException("An argument is null.", nameof(arguments)));
        return psi;
    }

    public async Task<VpnProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct,
        int timeoutMs = 30000)
    {
        using var proc = new Process { StartInfo = StartInfo(file, arguments) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or FileNotFoundException or UnauthorizedAccessException)
        {
            // Binary not found / not executable — a sentinel so callers can tell "not installed" apart.
            return new VpnProcessResult(127, "", ex.Message);
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
            // The host's stop is not a timeout: it ends the caller too.
            ct.ThrowIfCancellationRequested();
            return new VpnProcessResult(124, stdout.ToString(), "timeout");
        }

        return new VpnProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Its output goes to the debug log (redacted) and an exit is logged as a warning — a daemon that died right
    /// after start used to vanish without a trace.</summary>
    public bool StartDetached(string file, IReadOnlyList<string> arguments, ILogger logger, string? logName = null)
    {
        var name = logName ?? file;
        try
        {
            var proc = new Process { StartInfo = StartInfo(file, arguments), EnableRaisingEvents = true };
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or FileNotFoundException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to start detached process {File}", file);
            return false;
        }
    }
}
