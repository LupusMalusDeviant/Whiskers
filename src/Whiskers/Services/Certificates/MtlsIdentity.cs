using System.Net;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Certificates;

/// <summary>Who an mTLS proxy certificate says it is: the common name and the SANs. Every value ends up in a shell
/// line on the host (openssl <c>-subj</c>/<c>-addext</c>), so construction is an allow-list, not a filter.</summary>
public sealed partial record MtlsIdentity
{
    public string CommonName { get; }
    public IReadOnlyList<string> DnsNames { get; }
    public IReadOnlyList<string> IpAddresses { get; }

    public MtlsIdentity(string commonName, IEnumerable<string> dnsNames, IEnumerable<string> ipAddresses)
    {
        if (!NameRegex().IsMatch(commonName ?? ""))
            throw new ArgumentException($"Common name '{commonName}' is not a plain DNS label.", nameof(commonName));
        var dns = dnsNames.ToList();
        var ips = ipAddresses.ToList();
        foreach (var d in dns)
            if (!NameRegex().IsMatch(d)) throw new ArgumentException($"DNS name '{d}' is not allowed.", nameof(dnsNames));
        foreach (var ip in ips)
            if (!IPAddress.TryParse(ip, out _)) throw new ArgumentException($"'{ip}' is not an IP address.", nameof(ipAddresses));
        if (ips.Count == 0)
            throw new ArgumentException("The proxy is reached by IP, so the certificate needs an IP SAN.", nameof(ipAddresses));

        CommonName = commonName!;
        DnsNames = dns;
        IpAddresses = ips.Select(i => IPAddress.Parse(i).ToString()).ToList();
    }

    /// <summary>The identity onboarding gives a new host: <c>{slug}-dockerproxy</c>, reachable as slug and tailnet IP.</summary>
    public static MtlsIdentity ForNewHost(string slug, string tailnetIp) =>
        new($"{slug}-dockerproxy", new[] { slug }, new[] { tailnetIp });

    /// <summary>openssl's <c>subjectAltName=</c> value, e.g. <c>DNS:rabenhof,IP:100.112.9.103</c>.</summary>
    public string OpenSslSubjectAltName =>
        string.Join(",", DnsNames.Select(d => $"DNS:{d}").Concat(IpAddresses.Select(i => $"IP:{i}")));

    // Lowercase labels separated by dots: what Slug() produces and what the existing proxy certificates carry.
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex NameRegex();
}
