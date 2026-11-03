using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Whiskers.Services.Notifications;

/// <summary>
/// R1-54 (INV-60): which addresses an outbound notification or webhook may never connect to on its own. The list is
/// fixed in code and cannot be configured — an empty configuration denies all of it; only an exact exception entry
/// (<see cref="IOutboundExceptionService"/>) lets one address through for one port.
///
/// <para>An address is judged in its canonical form: IPv4-mapped (<c>::ffff:a.b.c.d</c>) and IPv4-compatible
/// (<c>::a.b.c.d</c>) IPv6, the well-known NAT64 prefix <c>64:ff9b::/96</c> and 6to4 (<c>2002::/16</c>) are judged by the
/// IPv4 address they embed; a zone (<c>fe80::1%eth0</c>) is dropped before judging. Teredo (<c>2001::/32</c>) embeds two
/// IPv4 addresses, one of them obfuscated, and is denied outright; so is local-use NAT64 (<c>64:ff9b:1::/48</c>), whose
/// IPv4 position depends on the translator's prefix length.</para>
/// </summary>
public static class OutboundAddressPolicy
{
    private sealed record Range(byte[] Prefix, int Bits, string Name)
    {
        public bool Contains(byte[] address)
        {
            if (address.Length != Prefix.Length) return false;
            var full = Bits / 8;
            for (var i = 0; i < full; i++)
                if (address[i] != Prefix[i]) return false;
            var rest = Bits % 8;
            if (rest == 0) return true;
            var mask = (byte)(0xFF << (8 - rest));
            return (address[full] & mask) == (Prefix[full] & mask);
        }
    }

    private static Range Cidr(string cidr, string name)
    {
        var slash = cidr.IndexOf('/');
        return new Range(IPAddress.Parse(cidr[..slash]).GetAddressBytes(),
            int.Parse(cidr[(slash + 1)..], CultureInfo.InvariantCulture), name);
    }

    // Docker's default bridge and overlay pools fall inside RFC 1918; 255.255.255.255 inside 240.0.0.0/4.
    private static readonly Range[] DeniedV4 =
    [
        Cidr("0.0.0.0/8", "unspecified"),
        Cidr("10.0.0.0/8", "private (RFC 1918)"),
        Cidr("100.64.0.0/10", "CGNAT / Tailscale"),
        Cidr("127.0.0.0/8", "loopback"),
        Cidr("169.254.0.0/16", "link-local"),
        Cidr("172.16.0.0/12", "private (RFC 1918)"),
        Cidr("192.0.0.0/24", "IETF protocol assignments"),
        Cidr("192.0.2.0/24", "documentation"),
        Cidr("192.88.99.0/24", "6to4 relay anycast"),
        Cidr("192.168.0.0/16", "private (RFC 1918)"),
        Cidr("198.18.0.0/15", "benchmarking"),
        Cidr("198.51.100.0/24", "documentation"),
        Cidr("203.0.113.0/24", "documentation"),
        Cidr("224.0.0.0/4", "multicast"),
        Cidr("240.0.0.0/4", "reserved / broadcast"),
    ];

    private static readonly Range[] DeniedV6 =
    [
        Cidr("::/128", "unspecified"),
        Cidr("::1/128", "loopback"),
        Cidr("64:ff9b:1::/48", "local-use NAT64"),
        Cidr("100::/64", "discard"),
        Cidr("2001::/32", "Teredo"),
        Cidr("2001:2::/48", "benchmarking"),
        Cidr("2001:20::/28", "ORCHIDv2"),
        Cidr("2001:db8::/32", "documentation"),
        Cidr("fd7a:115c:a1e0::/48", "Tailscale"),     // inside fc00::/7; listed first so the reason names it
        Cidr("fc00::/7", "unique local (ULA)"),
        Cidr("fe80::/10", "link-local"),
        Cidr("fec0::/10", "site-local (deprecated)"),
        Cidr("ff00::/8", "multicast"),
    ];

    private static readonly byte[] Nat64WellKnown = [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The address as it is judged: an IPv6 form that embeds an IPv4 address (mapped, compatible, NAT64
    /// <c>64:ff9b::/96</c>, 6to4) becomes that IPv4 address; a zone is dropped.</summary>
    public static IPAddress Canonicalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return new IPAddress(bytes);
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();

        // IPv4-compatible (::a.b.c.d), except :: and ::1, which are IPv6 addresses of their own.
        if (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && !(bytes[12] == 0 && bytes[13] == 0 && bytes[14] == 0 && bytes[15] <= 1))
            return new IPAddress(bytes.AsSpan(12, 4));
        if (bytes.AsSpan(0, 12).SequenceEqual(Nat64WellKnown)) return new IPAddress(bytes.AsSpan(12, 4));
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return new IPAddress(bytes.AsSpan(2, 4));   // 6to4
        return new IPAddress(bytes);                                                             // without the zone
    }

    /// <summary>The name of the default-denied range <paramref name="address"/> falls in (judged canonically), or
    /// <c>null</c> when it is a public address.</summary>
    public static string? DeniedRange(IPAddress address)
    {
        var canonical = Canonicalize(address);
        var bytes = canonical.GetAddressBytes();
        var ranges = canonical.AddressFamily == AddressFamily.InterNetwork ? DeniedV4 : DeniedV6;
        foreach (var range in ranges)
            if (range.Contains(bytes)) return range.Name;
        return null;
    }

    /// <summary>
    /// Reads <paramref name="host"/> as an IP literal the way a resolver would: brackets and a zone (<c>%eth0</c>) are
    /// removed, IPv4 is accepted in every form <c>inet_aton</c> takes (decimal <c>2130706433</c>, octal <c>0177.0.0.1</c>,
    /// hex <c>0x7f.1</c>, fewer than four parts). The result is not canonicalized. False when the host is a name.
    /// </summary>
    public static bool TryParseLiteral(string? host, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(host)) return false;
        var text = host.Trim();
        if (text.StartsWith('[') && text.EndsWith(']')) text = text[1..^1];
        var zone = text.IndexOf('%');
        if (zone >= 0) text = text[..zone];
        if (text.Contains(':'))
        {
            if (!IPAddress.TryParse(text, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) return false;
            address = new IPAddress(v6.GetAddressBytes());
            return true;
        }
        if (!TryParseIPv4Loose(text, out var v4)) return false;
        address = v4;
        return true;
    }

    private static bool TryParseIPv4Loose(string text, out IPAddress address)
    {
        address = IPAddress.None;
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var values = new ulong[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!TryParsePart(parts[i], out values[i])) return false;

        // The last part fills the bytes the others leave (inet_aton).
        ulong result = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (values[i] > 0xFF) return false;
            result |= values[i] << (8 * (3 - i));
        }
        var lastMax = parts.Length switch { 1 => 0xFFFF_FFFFUL, 2 => 0xFF_FFFFUL, 3 => 0xFFFFUL, _ => 0xFFUL };
        if (values[^1] > lastMax) return false;
        result |= values[^1];
        address = new IPAddress([(byte)(result >> 24), (byte)(result >> 16), (byte)(result >> 8), (byte)result]);
        return true;
    }

    private static bool TryParsePart(string part, out ulong value)
    {
        value = 0;
        if (part.Length == 0 || part.Length > 12) return false;
        if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return part.Length > 2 && ulong.TryParse(part.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        if (part.Length > 1 && part[0] == '0')
        {
            foreach (var c in part)
            {
                if (c is < '0' or > '7') return false;
                value = value * 8 + (ulong)(c - '0');
            }
            return true;
        }
        foreach (var c in part)
            if (c is < '0' or > '9') return false;
        return ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
