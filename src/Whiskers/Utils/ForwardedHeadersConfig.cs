using System.Net;

namespace Whiskers.Utils;

/// <summary>
/// Resolves the set of proxy networks whose forwarded headers (X-Forwarded-For / X-Forwarded-Proto)
/// are trusted by the ForwardedHeaders middleware.
/// </summary>
/// <remarks>
/// ASP.NET Core only checks the connecting peer against the known list when that list is non-empty;
/// leaving <c>KnownIPNetworks</c> and <c>KnownProxies</c> empty makes the middleware apply forwarded
/// headers from ANY source (trust-all), which lets a client spoof X-Forwarded-For and corrupt the
/// SourceIp recorded for webhooks and the audit log. This helper therefore never returns an empty
/// list: missing, empty, or all-invalid configuration falls back to safe defaults (loopback, RFC1918,
/// Tailscale CGNAT) so the known-proxy check stays enabled and public clients are never trusted.
/// </remarks>
public static class ForwardedHeadersConfig
{
    // Loopback + RFC1918 private ranges + 100.64.0.0/10 (CGNAT, used by Tailscale). These cover the
    // reverse-proxy and mesh topologies Whiskers is deployed behind.
    private static readonly string[] DefaultTrustedCidrs =
    {
        "127.0.0.0/8",    // IPv4 loopback
        "::1/128",        // IPv6 loopback
        "10.0.0.0/8",     // RFC1918
        "172.16.0.0/12",  // RFC1918
        "192.168.0.0/16", // RFC1918
        "100.64.0.0/10",  // CGNAT / Tailscale
    };

    /// <summary>
    /// Parses the configured CIDR list into <see cref="IPNetwork"/> entries, skipping malformed ones.
    /// Returns the safe defaults when the input is null, empty, or contains no valid entry — the result
    /// is guaranteed non-empty so the forwarded-headers known-proxy check is never accidentally disabled.
    /// </summary>
    public static IReadOnlyList<IPNetwork> ParseTrustedNetworks(IEnumerable<string>? configured)
    {
        var networks = new List<IPNetwork>();
        if (configured is not null)
        {
            foreach (var entry in configured)
            {
                if (!string.IsNullOrWhiteSpace(entry) && IPNetwork.TryParse(entry.Trim(), out var network))
                    networks.Add(network);
            }
        }

        if (networks.Count == 0)
        {
            foreach (var cidr in DefaultTrustedCidrs)
                networks.Add(IPNetwork.Parse(cidr));
        }

        return networks;
    }
}
