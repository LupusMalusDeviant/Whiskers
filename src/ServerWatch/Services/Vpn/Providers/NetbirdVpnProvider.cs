using Microsoft.Extensions.Options;

namespace ServerWatch.Services.Vpn.Providers;

/// <summary>
/// NetBird provider (WireGuard mesh with a fully open-source, self-hostable control plane).
/// Brings the peer up with "netbird up", optionally pointing at a self-hosted management URL.
/// Requires the netbird binary + service in the image; degrades gracefully when absent.
/// </summary>
public class NetbirdVpnProvider : IVpnProvider
{
    private readonly VpnSettings _settings;
    private readonly ILogger<NetbirdVpnProvider> _logger;

    public NetbirdVpnProvider(IOptions<VpnSettings> settings, ILogger<NetbirdVpnProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public string Id => "netbird";
    public string DisplayName => "NetBird";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var r = await VpnProcessRunner.RunAsync("netbird", "version", ct, 5000);
        return r.ExitCode != 127;
    }

    public async Task EnsureUpAsync(CancellationToken ct = default)
    {
        var nb = _settings.Netbird;
        if (!await IsAvailableAsync(ct))
        {
            _logger.LogWarning("[vpn:netbird] netbird CLI not found — install it in the image or run NetBird on the host (Provider=none)");
            return;
        }

        // netbird's own service daemon must be running for "up" to work.
        var svc = await VpnProcessRunner.RunAsync("netbird", "service status", ct, 5000);
        if (!svc.Success)
        {
            _logger.LogInformation("[vpn:netbird] installing/starting netbird service");
            await VpnProcessRunner.RunAsync("netbird", "service install", ct, 15000);
            await VpnProcessRunner.RunAsync("netbird", "service start", ct, 15000);
            await Task.Delay(1500, ct);
        }

        var args = "up";
        if (!string.IsNullOrWhiteSpace(nb.ManagementUrl)) args += $" --management-url {nb.ManagementUrl}";
        if (!string.IsNullOrWhiteSpace(_settings.Hostname)) args += $" --hostname {_settings.Hostname}";

        // Pass the setup key via NB_SETUP_KEY (env), never --setup-key in argv (would show in the process list).
        var env = !string.IsNullOrWhiteSpace(nb.SetupKey)
            ? new Dictionary<string, string> { ["NB_SETUP_KEY"] = nb.SetupKey }
            : null;

        var up = await VpnProcessRunner.RunAsync("netbird", args, ct, 60000, env);
        if (up.Success)
            _logger.LogInformation("[vpn:netbird] connected");
        else if (string.IsNullOrWhiteSpace(nb.SetupKey))
            _logger.LogInformation("[vpn:netbird] not enrolled — provide a setup key or run 'netbird up' interactively");
        else
            _logger.LogWarning("[vpn:netbird] up failed: {Err}", up.StdErr.Trim());
    }

    public async Task<VpnStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var r = await VpnProcessRunner.RunAsync("netbird", "status", ct, 5000);
        if (r.ExitCode == 127) return VpnStatus.Disconnected("netbird not installed");

        var text = r.StdOut;
        // "netbird status" output is human-readable; detect the connected marker conservatively.
        var connected = text.Contains("Management: Connected", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Signal: Connected", StringComparison.OrdinalIgnoreCase);
        var addresses = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("NetBird IP:", StringComparison.OrdinalIgnoreCase))
                addresses.Add(trimmed["NetBird IP:".Length..].Trim());
        }
        return new VpnStatus(connected, connected ? "Connected" : "Disconnected", _settings.Hostname, addresses, null);
    }

    public async Task DownAsync(CancellationToken ct = default)
    {
        await VpnProcessRunner.RunAsync("netbird", "down", ct, 15000);
    }
}
