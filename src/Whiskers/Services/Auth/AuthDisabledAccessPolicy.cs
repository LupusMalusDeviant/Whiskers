using System.Net;

namespace Whiskers.Services.Auth;

/// <summary>
/// Who may use an instance running with <c>Auth:Disabled</c> (audit TA-11). In that mode every request is an
/// Admin, so the mode is only defensible on a trusted network. This policy enforces that, instead of trusting
/// the operator's firewall:
/// <list type="bullet">
/// <item>the client address must be in <c>Auth:DisabledAllowedNetworks</c> (default: loopback, RFC1918, CGNAT/Tailscale,
/// IPv6 ULA) — a public address is refused even if the port is exposed by mistake;</item>
/// <item>the <c>Host</c> header must be an IP literal, <c>localhost</c>, or listed in <c>Auth:DisabledAllowedHosts</c> —
/// this defeats DNS rebinding, where a hostile site resolves its own name to the instance's address;</item>
/// <item>an <c>Origin</c> header, when present, must name the same host — this stops other sites from driving the
/// instance through the user's browser (cross-site WebSocket hijacking of the Blazor and SignalR hubs).</item>
/// </list>
/// </summary>
public sealed class AuthDisabledAccessPolicy
{
    private static readonly string[] DefaultNetworks =
    {
        "127.0.0.0/8", "::1/128",                                   // loopback
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",            // RFC1918 (incl. Docker bridge networks)
        "100.64.0.0/10",                                            // CGNAT / Tailscale
        "fc00::/7",                                                 // IPv6 unique local
    };

    private readonly IReadOnlyList<IPNetwork> _networks;
    private readonly HashSet<string> _hosts;

    public AuthDisabledAccessPolicy(IEnumerable<string>? allowedNetworks, IEnumerable<string>? allowedHosts)
    {
        var networks = (allowedNetworks ?? Array.Empty<string>())
            .Select(n => IPNetwork.TryParse(n?.Trim(), out var net) ? net : (IPNetwork?)null)
            .OfType<IPNetwork>()
            .ToList();
        _networks = networks.Count > 0 ? networks : DefaultNetworks.Select(IPNetwork.Parse).ToList();
        _hosts = new HashSet<string>(
            (allowedHosts ?? Array.Empty<string>()).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()),
            StringComparer.OrdinalIgnoreCase) { "localhost" };
    }

    public static AuthDisabledAccessPolicy FromConfiguration(IConfiguration config) => new(
        config.GetSection("Auth:DisabledAllowedNetworks").Get<string[]>(),
        config.GetSection("Auth:DisabledAllowedHosts").Get<string[]>());

    /// <summary>Returns null when the request may proceed, otherwise the reason it is refused.</summary>
    public string? Check(IPAddress? remote, string? host, string? origin)
    {
        // No remote address only happens on transports that are local by construction (a Unix domain socket, the
        // in-memory test server); every TCP connection carries one.
        if (remote is not null)
        {
            if (remote.IsIPv4MappedToIPv6)
                remote = remote.MapToIPv4();
            if (!_networks.Any(n => n.Contains(remote)))
                return "client address is not in Auth:DisabledAllowedNetworks";
        }

        var hostName = HostWithoutPort(host);
        if (hostName is null || !(IPAddress.TryParse(hostName, out _) || _hosts.Contains(hostName)))
            return "Host header is neither an IP address nor listed in Auth:DisabledAllowedHosts";

        if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, "null", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)
                || !string.Equals(o.Authority, host, StringComparison.OrdinalIgnoreCase))
                return "cross-site request (Origin does not match Host)";
        }
        return null;
    }

    private static string? HostWithoutPort(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (host.StartsWith('['))                                   // [::1]:8080
        {
            var end = host.IndexOf(']');
            return end > 1 ? host[1..end] : null;
        }
        var colon = host.LastIndexOf(':');
        return colon > 0 && host.IndexOf(':') == colon ? host[..colon] : host;
    }
}
