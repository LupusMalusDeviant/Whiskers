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

    /// <summary><c>HttpContext.Items</c> key set when a request carried forwarded headers from a peer that is not a
    /// trusted proxy (the headers were ignored). The Auth:Disabled gate refuses such a request.</summary>
    public const string IgnoredItemKey = "Whiskers.ForwardedHeadersIgnored";

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
                var slash = value.IndexOf('/');
                // Only the canonical form of an address counts: IPAddress.TryParse also takes "172.18.0" (= 172.18.0.0),
                // "10" (= 0.0.0.10) or "0x7f000001", so a typo would quietly trust a different address.
                if (!IsCanonicalAddress(slash < 0 ? value : value[..slash]))
                    invalid.Add(value);
                else if (slash >= 0 && IPNetwork.TryParse(value, out var network))
                    networks.Add(network);
                else if (slash < 0 && IPAddress.TryParse(value, out var address))
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

    /// <summary>Reads <see cref="SectionName"/> from <paramref name="configuration"/>: the indexed entries
    /// (<c>__0</c>, <c>__1</c>, …), or — when the key is set as one value — a comma-separated list. Parsed by
    /// <see cref="ParseTrustedNetworks"/>, so an unreadable entry throws.</summary>
    public static IReadOnlyList<IPNetwork> FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var entries = section.GetChildren().Any()
            ? section.GetChildren().Select(c => c.Value ?? "")
            : (section.Value ?? "").Split(',');
        return ParseTrustedNetworks(entries);
    }

    private static bool IsCanonicalAddress(string text) =>
        IPAddress.TryParse(text, out var address)
        && string.Equals(address.ToString(), text, StringComparison.OrdinalIgnoreCase);

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
