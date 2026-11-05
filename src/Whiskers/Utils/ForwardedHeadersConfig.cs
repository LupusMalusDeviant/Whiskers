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
    /// blank entries) yields loopback; any entry that is not a canonical CIDR or single address throws
    /// <see cref="InvalidOperationException"/> naming it and the reason — the result is never empty and never a guess.
    /// </summary>
    public static IReadOnlyList<IPNetwork> ParseTrustedNetworks(IEnumerable<string?>? configured)
    {
        var networks = new List<IPNetwork>();
        var invalid = new List<string>();
        if (configured is not null)
        {
            foreach (var entry in configured)
            {
                if (entry is null) { invalid.Add("(an entry without a value, e.g. a nested object)"); continue; }
                if (string.IsNullOrWhiteSpace(entry)) continue;
                if (Parse(entry.Trim(), out var network, out var problem)) networks.Add(network);
                else invalid.Add(problem);
            }
        }

        if (invalid.Count > 0)
            throw new InvalidOperationException(
                $"{SectionName} holds entries Whiskers cannot read: {string.Join("; ", invalid)}. " +
                "Whiskers does not start with a trusted-proxy list it cannot read (R1-50).");

        if (networks.Count == 0)
        {
            foreach (var cidr in DefaultTrustedCidrs)
                networks.Add(IPNetwork.Parse(cidr));
        }

        return networks;
    }

    private static bool Parse(string value, out IPNetwork network, out string problem)
    {
        network = default;
        var slash = value.IndexOf('/');
        var addressText = slash < 0 ? value : value[..slash];
        // Only the canonical form of an address counts: IPAddress.TryParse also takes "172.18.0" (= 172.18.0.0),
        // "10" (= 0.0.0.10) or "0x7f000001", so a typo would quietly trust a different address.
        if (!IPAddress.TryParse(addressText, out var address))
        {
            problem = $"'{value}' is neither a CIDR nor an address";
            return false;
        }
        if (!string.Equals(address.ToString(), addressText, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"'{value}' is not written canonically (write '{address}{(slash < 0 ? "" : value[slash..])}' if that is what you mean)";
            return false;
        }
        var bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (slash < 0)
        {
            network = new IPNetwork(address, bits);
            problem = "";
            return true;
        }
        if (!int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix)
            || prefix > bits)
        {
            problem = $"'{value}' has no valid prefix length (0–{bits})";
            return false;
        }
        // Checked here, not left to IPNetwork.TryParse: a CIDR with host bits set (172.18.0.2/16, copied from
        // `docker network inspect`) must not quietly become a different set of addresses.
        if (!NetworkOf(address, prefix).Equals(address))
        {
            problem = $"'{value}' has host bits set (use '{NetworkOf(address, prefix)}/{prefix}' for the network or '{address}/{bits}' for the one address)";
            return false;
        }
        network = new IPNetwork(address, prefix);
        problem = "";
        return true;
    }

    private static IPAddress NetworkOf(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }

    /// <summary>Reads <see cref="SectionName"/> from <paramref name="configuration"/>: the indexed entries
    /// (<c>__0</c>, <c>__1</c>, …), or — when the key is set as one value — a comma-separated list. Parsed by
    /// <see cref="ParseTrustedNetworks"/>, so an unreadable entry (also a nested object) throws.</summary>
    public static IReadOnlyList<IPNetwork> FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var entries = section.GetChildren().Any()
            ? section.GetChildren().Select(c => c.Value)
            : (section.Value ?? "").Split(',');
        return ParseTrustedNetworks(entries);
    }

    /// <summary>The framework's own forwarded-headers switch (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c>, read as
    /// <c>FORWARDEDHEADERS_ENABLED</c>) inserts a forwarded-headers step ahead of the pipeline that trusts every peer —
    /// any client could then claim any address before the R1-50 list is consulted. Throws when it is set (fail closed).</summary>
    public static void RefuseFrameworkTrustAll(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        if (string.Equals(configuration["FORWARDEDHEADERS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED=true makes ASP.NET Core trust forwarded headers from every peer. " +
                $"Remove it and list your reverse proxy in {SectionName} instead (R1-50).");
    }

    /// <summary>Whether <paramref name="peer"/> is one of <paramref name="trusted"/>, judged as the forwarded-headers
    /// middleware judges it: an IPv4-mapped IPv6 address matches a network of either form. A null peer is not trusted
    /// here; the pipeline does not mark a request without a peer address (a local transport, where the middleware applies
    /// the headers).</summary>
    public static bool IsTrusted(IReadOnlyList<IPNetwork> trusted, IPAddress? peer)
    {
        if (peer is null) return false;
        var mapped = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : null;
        foreach (var network in trusted)
            if (network.Contains(peer) || (mapped is not null && network.Contains(mapped))) return true;
        return false;
    }
}
