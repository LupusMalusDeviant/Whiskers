using System.Globalization;
using System.Net;
using Whiskers.Configuration;

namespace Whiskers.Services.Notifications;

/// <summary>R1-54 (INV-60): the Admin-managed exceptions to the outbound guard. The live list is what
/// <see cref="IOutboundTargetGuard"/> judges every connection by; a save replaces it only after it is written, and the
/// guarded HTTP handler is replaced before the next delivery, so a removed exception also ends the pooled connections it
/// allowed.</summary>
public interface IOutboundExceptionService
{
    /// <summary>The list in force (validated, addresses canonical). Replaced, never changed in place.</summary>
    OutboundExceptionList Current { get; }

    /// <summary>Validates and stores <paramref name="entries"/> as the whole new list, then publishes it. An invalid
    /// entry (a range, a wildcard, a name without addresses, a port out of range) throws
    /// <see cref="ArgumentException"/> before anything is written; a failed write leaves the live list as it was.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SaveAsync(IReadOnlyList<OutboundExceptionEntry> entries, CancellationToken ct = default);
}

/// <summary>One validated exception: <see cref="IsLiteral"/> entries name one IP address (their only
/// <see cref="Addresses"/> entry), name entries a lower-case host name and the exact addresses it may connect to.</summary>
public sealed record OutboundException(string Host, int Port, IReadOnlyList<IPAddress> Addresses, bool IsLiteral)
{
    /// <summary>Most entries one list may hold.</summary>
    public const int MaxEntries = 100;

    /// <summary>Reads and checks one entry; throws <see cref="ArgumentException"/> with a reason that may be shown.</summary>
    public static OutboundException Parse(OutboundExceptionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var host = (entry.Host ?? "").Trim();
        if (host.Length == 0) throw new ArgumentException("A host or IP address is required.");
        if (entry.Port is < 1 or > 65535) throw new ArgumentException($"{host}: the port must be between 1 and 65535.");
        if (host.IndexOfAny(['*', '/', ',', ';', '@', ' ']) >= 0 || IsRange(host))
            throw new ArgumentException($"{host}: ranges, wildcards and lists are not accepted — name one exact host.");

        var addresses = new List<IPAddress>();
        foreach (var raw in entry.Addresses ?? [])
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) continue;
            if (text.IndexOfAny(['*', '/', ',', ' ']) >= 0 || IsRange(text) || !OutboundAddressPolicy.TryParseLiteral(text, out var parsed)
                || !IPAddress.TryParse(StripZone(text), out _))
                throw new ArgumentException($"{host}: \"{text}\" is not one exact IP address (ranges and wildcards are not accepted).");
            var canonical = OutboundAddressPolicy.Canonicalize(parsed);
            if (!addresses.Contains(canonical)) addresses.Add(canonical);
        }

        if (OutboundAddressPolicy.TryParseLiteral(host, out var literal))
        {
            var canonical = OutboundAddressPolicy.Canonicalize(literal);
            if (addresses.Any(a => !a.Equals(canonical)))
                throw new ArgumentException($"{host}: an IP address entry takes no further addresses.");
            return new OutboundException(canonical.ToString(), entry.Port, [canonical], IsLiteral: true);
        }

        var name = NormalizeName(host);
        if (name is null) throw new ArgumentException($"{host}: not a valid host name.");
        if (addresses.Count == 0)
            throw new ArgumentException($"{host}: a host name needs the exact address(es) it may resolve to.");
        return new OutboundException(name, entry.Port, addresses, IsLiteral: false);
    }

    /// <summary>The entry as it is stored.</summary>
    public OutboundExceptionEntry ToEntry() => new()
    {
        Host = Host, Port = Port,
        Addresses = IsLiteral ? [] : Addresses.Select(a => a.ToString()).ToList(),
    };

    /// <summary>How the entry is shown: <c>host:port → addresses</c>.</summary>
    public override string ToString() => IsLiteral
        ? $"{Bracketed(Host)}:{Port.ToString(CultureInfo.InvariantCulture)}"
        : $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)} → {string.Join(", ", Addresses)}";

    /// <summary>A host name as it is compared: lower case, ASCII (IDN), without a trailing dot; <c>null</c> when it is
    /// no valid DNS name.</summary>
    public static string? NormalizeName(string host)
    {
        var name = host.Trim().TrimEnd('.');
        if (name.Length == 0) return null;
        try { name = new IdnMapping().GetAscii(name); }
        catch (ArgumentException) { return null; }
        name = name.ToLowerInvariant();
        return Uri.CheckHostName(name) == UriHostNameType.Dns ? name : null;
    }

    internal static string Bracketed(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static string StripZone(string text)
    {
        var zone = text.IndexOf('%');
        return (zone >= 0 ? text[..zone] : text).Trim('[', ']');
    }

    // "10.0.0.1-10.0.0.9" reads as a (valid) host name; it is a range.
    private static bool IsRange(string text)
    {
        var parts = text.Split('-');
        return parts.Length == 2 && parts.All(p => OutboundAddressPolicy.TryParseLiteral(p, out _));
    }
}

/// <summary>A published list of exceptions. <see cref="Version"/> grows with every publication; the guarded HTTP
/// handler compares it before every request.</summary>
public sealed record OutboundExceptionList(IReadOnlyList<OutboundException> Entries, long Version)
{
    public static OutboundExceptionList Empty { get; } = new([], 0);

    /// <summary>Whether an entry lets a connection for <paramref name="host"/> (as the URL or the SMTP setting names it)
    /// on <paramref name="port"/> through to <paramref name="connected"/>, the address actually connected to. An IP
    /// entry applies when the host is that literal and the connection goes to it; a name entry when the host is that
    /// name and the connected address is one of its addresses — a name that rebinds elsewhere is not excepted.</summary>
    public bool Allows(string host, int port, IPAddress connected)
    {
        var address = OutboundAddressPolicy.Canonicalize(connected);
        if (OutboundAddressPolicy.TryParseLiteral(host, out var literal))
        {
            var canonical = OutboundAddressPolicy.Canonicalize(literal);
            return canonical.Equals(address)
                   && Entries.Any(e => e.IsLiteral && e.Port == port && e.Addresses[0].Equals(address));
        }
        var name = OutboundException.NormalizeName(host);
        return name is not null
               && Entries.Any(e => !e.IsLiteral && e.Port == port && e.Host == name && e.Addresses.Contains(address));
    }

    /// <summary>Reads stored entries; each invalid one is left out and its reason added to <paramref name="errors"/>
    /// (an entry that cannot be read excepts nothing).</summary>
    public static OutboundExceptionList From(IEnumerable<OutboundExceptionEntry> entries, long version, List<string>? errors = null)
    {
        var list = new List<OutboundException>();
        foreach (var entry in entries)
        {
            try
            {
                var parsed = OutboundException.Parse(entry);
                if (!list.Any(e => e.Host == parsed.Host && e.Port == parsed.Port)) list.Add(parsed);
            }
            catch (ArgumentException ex) { errors?.Add(ex.Message); }
        }
        return new OutboundExceptionList(list, version);
    }
}
