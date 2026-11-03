using System.Text.Json;
using Microsoft.Extensions.Options;
using Whiskers.Utils;

namespace Whiskers.Services.Vpn.Providers;

/// <summary>
/// Tailscale provider. Mirrors the previous entrypoint.sh logic in C#: ensure tailscaled is running,
/// then "tailscale up" with the configured auth key. State persists under the configured StateDir.
/// <para>R1-43: every argument is its own argv element in the <c>--flag=value</c> form; the hostname and the login
/// server are validated first (<see cref="VpnArguments"/>). The auth key goes as <c>--auth-key=file:&lt;path&gt;</c> — the
/// form <c>tailscale up</c> reads a key from (the CLI does not read <c>TS_AUTHKEY</c>; that is the container image's
/// own start script) — from a 0600 file that is deleted when the call returns (<see cref="VpnSecretFile"/>).</para>
/// </summary>
public class TailscaleVpnProvider : IVpnProvider
{
    private readonly VpnSettings _settings;
    private readonly ILogger<TailscaleVpnProvider> _logger;
    private readonly IVpnProcessRunner _runner;
    private readonly Func<bool> _hostInterfacePresent;
    private readonly string? _secretParent;

    public TailscaleVpnProvider(IOptions<VpnSettings> settings, ILogger<TailscaleVpnProvider> logger)
        : this(settings, logger, VpnProcessRunner.Instance)
    {
    }

    /// <summary>For tests: a recorded runner, the host interface check and where the key file goes.</summary>
    internal TailscaleVpnProvider(IOptions<VpnSettings> settings, ILogger<TailscaleVpnProvider> logger,
        IVpnProcessRunner runner, Func<bool>? hostInterfacePresent = null, string? secretParent = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _hostInterfacePresent = hostInterfacePresent ?? HostInterfacePresent;
        _secretParent = secretParent;
    }

    public string Id => "tailscale";
    public string DisplayName => "Tailscale";

    private string SocketArg => "--socket=" + _settings.Tailscale.Socket;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("tailscale", ["version"], ct, 5000);
        return r.ExitCode != 127;
    }

    public async Task<VpnBringUpResult> EnsureUpAsync(CancellationToken ct = default)
    {
        var ts = _settings.Tailscale;

        // 0) Every value that becomes an argument is checked before anything runs.
        string hostname;
        string? loginServer = null;
        try
        {
            hostname = VpnArguments.RequireHostName(
                _settings.Hostname ?? Environment.GetEnvironmentVariable("HOSTNAME") ?? "serverwatch");
            if (!string.IsNullOrWhiteSpace(ts.LoginServer))
                loginServer = VpnArguments.RequireControlUrl(ts.LoginServer, "Tailscale login server");
        }
        catch (UnsafeOperandException ex)
        {
            _logger.LogError("[vpn:tailscale] not brought up: {Reason}", ex.Message);
            return new VpnBringUpResult(VpnBringUpState.InvalidSetting, ex.Message);
        }

        if (!await IsAvailableAsync(ct))
        {
            _logger.LogWarning("[vpn:tailscale] tailscale CLI not found — skipping bring-up");
            return new VpnBringUpResult(VpnBringUpState.NotInstalled, "The tailscale CLI is not installed.");
        }

        // 1) Ensure the daemon is running — unless the HOST already runs Tailscale. In a host-network container
        //    a second tailscaled fights the host daemon for tailscale0 and makes it restart, which dropped every
        //    mesh connection for one to two minutes on each Whiskers restart (Badwolf, 2026-10).
        var status = await _runner.RunAsync("tailscale", [SocketArg, "status", "--json"], ct, 5000);
        var daemonResponds = DaemonResponds(status);
        var decision = DecideDaemon(daemonResponds, _hostInterfacePresent(),
            Environment.GetEnvironmentVariable("VPN_FORCE_LEGACY_TAILSCALE") == "1");
        if (decision == DaemonDecision.HostManaged)
        {
            _logger.LogInformation("[vpn:tailscale] tailscale0 already exists and no daemon answers on {Socket} — " +
                "Tailscale is host-managed, not starting a second tailscaled (VPN_FORCE_LEGACY_TAILSCALE=1 overrides)",
                ts.Socket);
            return new VpnBringUpResult(VpnBringUpState.HostManaged,
                "tailscale0 exists and no daemon answers on the socket: Tailscale is host-managed.");
        }
        if (decision == DaemonDecision.Start)
        {
            Directory.CreateDirectory(ts.StateDir);
            Directory.CreateDirectory(Path.GetDirectoryName(ts.Socket)!);
            _logger.LogInformation("[vpn:tailscale] starting tailscaled");
            if (!_runner.StartDetached("tailscaled",
                    ["--state=" + Path.Combine(ts.StateDir, "tailscaled.state"), "--socket=" + ts.Socket], _logger, "tailscaled"))
                return new VpnBringUpResult(VpnBringUpState.Failed, "tailscaled could not be started.");
            await Task.Delay(2000, ct); // give the daemon a moment to create its socket
        }

        // 2) Bring the connection up.
        var args = new List<string> { SocketArg, "up", "--hostname=" + hostname };
        if (_settings.AcceptRoutes) args.Add("--accept-routes");
        if (loginServer is not null) args.Add("--login-server=" + loginServer);

        var key = string.IsNullOrWhiteSpace(ts.AuthKey) ? null : ts.AuthKey;
        VpnProcessResult up;
        if (key is null)
        {
            up = await _runner.RunAsync("tailscale", args, ct, 60000);
        }
        else
        {
            using var keyFile = VpnSecretFile.Create(key, _secretParent);
            args.Add("--auth-key=file:" + keyFile.Path);
            up = await _runner.RunAsync("tailscale", args, ct, 60000);
        }

        if (up.Success)
        {
            _logger.LogInformation("[vpn:tailscale] connected as {Hostname}", hostname);
            return new VpnBringUpResult(VpnBringUpState.Connected, $"Connected as {hostname}.");
        }
        var error = VpnArguments.Scrub(up.StdErr.Trim(), key);
        if (key is null)
        {
            _logger.LogWarning("[vpn:tailscale] daemon running but not authenticated — configure an auth key or log in on the host");
            return new VpnBringUpResult(VpnBringUpState.NeedsLogin,
                $"tailscale up failed (exit code {up.ExitCode}) and no auth key is configured: {error}");
        }
        _logger.LogWarning("[vpn:tailscale] up failed (exit code {Code}): {Err}", up.ExitCode, error);
        return new VpnBringUpResult(VpnBringUpState.Failed, $"tailscale up failed (exit code {up.ExitCode}): {error}");
    }

    internal enum DaemonDecision { UseRunning, Start, HostManaged }

    /// <summary>The daemon answers when <c>status --json</c> yields a JSON object with a <c>BackendState</c> —
    /// "Running", "NeedsLogin" and "Stopped" all mean a live daemon. The exit code alone is not enough: the CLI
    /// also exits 1 when it cannot reach any daemon, which the old check read as "running but logged out".</summary>
    internal static bool DaemonResponds(VpnProcessResult status)
    {
        if (status.ExitCode is not (0 or 1) || string.IsNullOrWhiteSpace(status.StdOut)) return false;
        try
        {
            using var doc = JsonDocument.Parse(status.StdOut);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("BackendState", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static DaemonDecision DecideDaemon(bool daemonResponds, bool hostInterfacePresent, bool forceLegacy) =>
        daemonResponds ? DaemonDecision.UseRunning
        : hostInterfacePresent && !forceLegacy ? DaemonDecision.HostManaged
        : DaemonDecision.Start;

    private static bool HostInterfacePresent() => Directory.Exists("/sys/class/net/tailscale0");

    public async Task<VpnStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("tailscale", [SocketArg, "status", "--json"], ct, 5000);
        if (r.ExitCode == 127) return VpnStatus.Disconnected("tailscale not installed");
        if (string.IsNullOrWhiteSpace(r.StdOut)) return VpnStatus.Disconnected(SecretRedactor.Redact(r.StdErr.Trim()));

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
        await _runner.RunAsync("tailscale", [SocketArg, "down"], ct, 15000);
    }
}
