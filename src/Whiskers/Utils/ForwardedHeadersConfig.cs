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
/// client address the login rate limit, the Auth:Disabled network gate, webhooks and the audit log use.
/// This helper therefore never returns an empty list.
/// <para>R1-50 (A-15, maintainer decision 2026-11-05): without configuration only loopback is trusted — a proxy
/// in another container or on another host is trusted only once it is named in
/// <c>ForwardedHeaders:TrustedNetworks</c>. Before, every private range (RFC1918, Tailscale CGNAT) was trusted, so any
/// client in the LAN or the tailnet could claim another address. A configured list that holds an entry which is not a
/// CIDR or address fails closed: the start is refused instead of silently trusting a different set.</para>
/// </remarks>
public static class ForwardedHeadersConfig
{
    /// <summary>The configuration key: an array of CIDRs or single addresses.</summary>
    public const string SectionName = "ForwardedHeaders:TrustedNetworks";

    // Loopback only: a reverse proxy on the same host (or in the same network namespace) forwards from here.
    private static readonly string[] DefaultTrustedCidrs =
    {
        "127.0.0.0/8",    // IPv4 loopback
        "::1/128",        // IPv6 loopback
    };

    /// <summary>
    /// Parses the configured list into <see cref="IPNetwork"/> entries. A null or empty list (or one holding only
    /// blank entries) yields loopback; any entry that is neither a CIDR nor a single address throws
    /// <see cref="InvalidOperationException"/> naming it — the result is never empty and never a guess.
    /// </summary>
    public static IReadOnlyList<IPNetwork> ParseTrustedNetworks(IEnumerable<string>? configured)
    {
        var networks = new List<IPNetwork>();
        var invalid = new List<string>();
        if (configured is not null)
        {
            foreach (var entry in configured)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var value = entry.Trim();
                if (IPNetwork.TryParse(value, out var network))
                    networks.Add(network);
                else if (IPAddress.TryParse(value, out var address))
                    networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
                else
                    invalid.Add(value);
            }
        }

        if (invalid.Count > 0)
            throw new InvalidOperationException(
                $"{SectionName} holds entries that are neither a CIDR nor an address: {string.Join(", ", invalid)}. " +
                "Whiskers does not start with a trusted-proxy list it cannot read (R1-50).");

        if (networks.Count == 0)
        {
            foreach (var cidr in DefaultTrustedCidrs)
                networks.Add(IPNetwork.Parse(cidr));
        }

        return networks;
    }

    /// <summary>Whether <paramref name="peer"/> is one of <paramref name="trusted"/> (an IPv4-mapped IPv6 address
    /// counts as its IPv4 form, as the forwarded-headers middleware treats it).</summary>
    public static bool IsTrusted(IReadOnlyList<IPNetwork> trusted, IPAddress? peer)
    {
        if (peer is null) return false;
        var address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        foreach (var network in trusted)
            if (network.Contains(address)) return true;
        return false;
    }
}
