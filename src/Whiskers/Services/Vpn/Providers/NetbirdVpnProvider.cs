using Microsoft.Extensions.Options;
using Whiskers.Utils;

namespace Whiskers.Services.Vpn.Providers;

/// <summary>
/// NetBird provider (WireGuard mesh with a fully open-source, self-hostable control plane).
/// Brings the peer up with "netbird up", optionally pointing at a self-hosted management URL.
/// Requires the netbird binary + service in the image; degrades gracefully when absent.
/// <para>R1-43: every argument is its own argv element in the <c>--flag=value</c> form; the hostname and the management
/// URL are validated first. The setup key goes as <c>--setup-key-file=&lt;path&gt;</c> from a 0600 file that is deleted
/// when the call returns (<see cref="VpnSecretFile"/>), never as <c>--setup-key</c> and no longer through the
/// environment.</para>
/// </summary>
public class NetbirdVpnProvider : IVpnProvider
{
    private readonly VpnSettings _settings;
    private readonly ILogger<NetbirdVpnProvider> _logger;
    private readonly IVpnProcessRunner _runner;
    private readonly string? _secretParent;

    public NetbirdVpnProvider(IOptions<VpnSettings> settings, ILogger<NetbirdVpnProvider> logger)
        : this(settings, logger, VpnProcessRunner.Instance)
    {
    }

    /// <summary>For tests: a recorded runner and where the key file goes.</summary>
    internal NetbirdVpnProvider(IOptions<VpnSettings> settings, ILogger<NetbirdVpnProvider> logger,
        IVpnProcessRunner runner, string? secretParent = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _secretParent = secretParent;
    }

    public string Id => "netbird";
    public string DisplayName => "NetBird";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("netbird", ["version"], ct, 5000);
        return r.ExitCode != 127;
    }

    public async Task<VpnBringUpResult> EnsureUpAsync(CancellationToken ct = default)
    {
        var nb = _settings.Netbird;
        if (_settings.KeyFileProblem is { } keyFileProblem)
        {
            _logger.LogError("[vpn:netbird] not brought up: {Reason}", keyFileProblem);
            return new VpnBringUpResult(VpnBringUpState.InvalidSetting, keyFileProblem);
        }

        string? hostname = null;
        string? managementUrl = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_settings.Hostname)) hostname = VpnArguments.RequireHostName(_settings.Hostname);
            if (!string.IsNullOrWhiteSpace(nb.ManagementUrl))
                managementUrl = VpnArguments.RequireControlUrl(nb.ManagementUrl, "NetBird management URL");
        }
        catch (UnsafeOperandException ex)
        {
            _logger.LogError("[vpn:netbird] not brought up: {Reason}", ex.Message);
            return new VpnBringUpResult(VpnBringUpState.InvalidSetting, ex.Message);
        }

        if (!await IsAvailableAsync(ct))
        {
            _logger.LogWarning("[vpn:netbird] netbird CLI not found — install it in the image or run NetBird on the host (Provider=none)");
            return new VpnBringUpResult(VpnBringUpState.NotInstalled, "The netbird CLI is not installed.");
        }

        // netbird's own service daemon must be running for "up" to work.
        var svc = await _runner.RunAsync("netbird", ["service", "status"], ct, 5000);
        if (!svc.Success)
        {
            _logger.LogInformation("[vpn:netbird] installing/starting netbird service");
            await _runner.RunAsync("netbird", ["service", "install"], ct, 15000);
            await _runner.RunAsync("netbird", ["service", "start"], ct, 15000);
            await Task.Delay(1500, ct);
        }

        var args = new List<string> { "up" };
        if (managementUrl is not null) args.Add("--management-url=" + managementUrl);
        if (hostname is not null) args.Add("--hostname=" + hostname);

        var key = string.IsNullOrWhiteSpace(nb.SetupKey) ? null : nb.SetupKey;
        VpnProcessResult up;
        if (key is null)
        {
            up = await _runner.RunAsync("netbird", args, ct, 60000);
        }
        else
        {
            using var keyFile = VpnSecretFile.Create(key, _secretParent, _logger);
            args.Add("--setup-key-file=" + keyFile.Path);
            up = await _runner.RunAsync("netbird", args, ct, 60000);
        }

        if (up.Success)
        {
            _logger.LogInformation("[vpn:netbird] connected");
            return new VpnBringUpResult(VpnBringUpState.Connected, "Connected.");
        }
        var error = VpnArguments.Scrub(up.StdErr.Trim(), key);
        if (key is null)
        {
            _logger.LogWarning("[vpn:netbird] not enrolled — provide a setup key or run 'netbird up' on the host");
            return new VpnBringUpResult(VpnBringUpState.NeedsLogin,
                $"netbird up failed (exit code {up.ExitCode}) and no setup key is configured: {error}");
        }
        _logger.LogWarning("[vpn:netbird] up failed (exit code {Code}): {Err}", up.ExitCode, error);
        return new VpnBringUpResult(VpnBringUpState.Failed, $"netbird up failed (exit code {up.ExitCode}): {error}");
    }

    public async Task<VpnStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("netbird", ["status"], ct, 5000);
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
        await _runner.RunAsync("netbird", ["down"], ct, 15000);
    }
}
