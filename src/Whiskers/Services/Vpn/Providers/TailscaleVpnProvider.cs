using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Whiskers.Services.Vpn.Providers;

/// <summary>
/// Tailscale provider. Mirrors the previous entrypoint.sh logic in C#: ensure tailscaled is running,
/// then "tailscale up" with the configured auth key. State persists under the configured StateDir.
/// </summary>
public class TailscaleVpnProvider : IVpnProvider
{
    private readonly VpnSettings _settings;
    private readonly ILogger<TailscaleVpnProvider> _logger;

    public TailscaleVpnProvider(IOptions<VpnSettings> settings, ILogger<TailscaleVpnProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public string Id => "tailscale";
    public string DisplayName => "Tailscale";

    private string SocketArg => $"--socket={_settings.Tailscale.Socket}";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var r = await VpnProcessRunner.RunAsync("tailscale", "version", ct, 5000);
        return r.ExitCode != 127;
    }

    public async Task EnsureUpAsync(CancellationToken ct = default)
    {
        var ts = _settings.Tailscale;
        if (!await IsAvailableAsync(ct))
        {
            _logger.LogWarning("[vpn:tailscale] tailscale CLI not found — skipping bring-up");
            return;
        }

        // 1) Ensure the daemon is running.
        var status = await VpnProcessRunner.RunAsync("tailscale", $"{SocketArg} status", ct, 5000);
        var daemonUp = status.ExitCode is 0 or 1; // 0 = up, 1 = running but logged out; 127/other = no daemon
        if (!daemonUp)
        {
            Directory.CreateDirectory(ts.StateDir);
            Directory.CreateDirectory(Path.GetDirectoryName(ts.Socket)!);
            _logger.LogInformation("[vpn:tailscale] starting tailscaled");
            VpnProcessRunner.StartDetached("tailscaled",
                $"--state={ts.StateDir}/tailscaled.state --socket={ts.Socket}", _logger);
            await Task.Delay(2000, ct); // give the daemon a moment to create its socket
        }

        // 2) Bring the connection up.
        var hostname = _settings.Hostname ?? Environment.GetEnvironmentVariable("HOSTNAME") ?? "serverwatch";
        var args = $"{SocketArg} up --hostname={hostname}";
        if (_settings.AcceptRoutes) args += " --accept-routes";
        if (!string.IsNullOrWhiteSpace(ts.LoginServer)) args += $" --login-server={ts.LoginServer}";

        // Pass the auth key via TS_AUTHKEY (env), never --authkey in argv (would show in the process list).
        var env = !string.IsNullOrWhiteSpace(ts.AuthKey)
            ? new Dictionary<string, string> { ["TS_AUTHKEY"] = ts.AuthKey }
            : null;

        var up = await VpnProcessRunner.RunAsync("tailscale", args, ct, 60000, env);
        if (up.Success)
            _logger.LogInformation("[vpn:tailscale] connected as {Hostname}", hostname);
        else if (string.IsNullOrWhiteSpace(ts.AuthKey))
            _logger.LogInformation("[vpn:tailscale] daemon running but not authenticated — connect via the Settings UI / auth key");
        else
            _logger.LogWarning("[vpn:tailscale] up failed: {Err}", up.StdErr.Trim());
    }

    public async Task<VpnStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var r = await VpnProcessRunner.RunAsync("tailscale", $"{SocketArg} status --json", ct, 5000);
        if (r.ExitCode == 127) return VpnStatus.Disconnected("tailscale not installed");
        if (string.IsNullOrWhiteSpace(r.StdOut)) return VpnStatus.Disconnected(r.StdErr.Trim());

        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var root = doc.RootElement;
            var state = root.TryGetProperty("BackendState", out var bs) ? bs.GetString() : null;
            var connected = string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);

            string? hostname = null;
            var addrs = new List<string>();
            if (root.TryGetProperty("Self", out var self))
            {
                if (self.TryGetProperty("HostName", out var hn)) hostname = hn.GetString();
                if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                    addrs.AddRange(ips.EnumerateArray().Select(a => a.GetString()).Where(a => a != null)!);
            }
            return new VpnStatus(connected, state, hostname, addrs, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[vpn:tailscale] failed to parse status json");
            return VpnStatus.Disconnected("status parse error");
        }
    }

    public async Task DownAsync(CancellationToken ct = default)
    {
        await VpnProcessRunner.RunAsync("tailscale", $"{SocketArg} down", ct, 15000);
    }
}
