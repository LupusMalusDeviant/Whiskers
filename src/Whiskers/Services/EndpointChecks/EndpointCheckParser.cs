using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Services.Notifications;

namespace Whiskers.Services.EndpointChecks;

/// <summary>
/// R1-57 (contract 0004): reads what a template printed. Pure, and it needs positive evidence: a run passes only on the
/// exit marker <c>__rc=0</c> at the very end of the output and the answer the probe exists for (a three-digit status, a
/// certificate's <c>notAfter</c>). A missing tool, a missing marker, a cut-off or unreadable answer is a failure.
/// </summary>
public static partial class EndpointCheckParser
{
    private const string Marker = "__rc=";

    [GeneratedRegex(@"__rc=(\d{1,3})\s*\z")]
    private static partial Regex EndMarker();

    [GeneratedRegex(@"^(\d{3}) (\d+(?:\.\d+)?)\z")]
    private static partial Regex CurlWrite();

    [GeneratedRegex(@"-----BEGIN CERTIFICATE-----\s*([A-Za-z0-9+/=\s]+?)\s*-----END CERTIFICATE-----")]
    private static partial Regex Pem();

    /// <summary>The exit code in the end marker and the output before it; null when there is no marker at the end, or
    /// when the output reached the template's <c>head -c</c> bound (review m3): then the real marker may have been cut
    /// away and whatever the target placed at the cut would read as one — counted in UTF-8 bytes, as <c>head</c> does.</summary>
    internal static (int Code, string Body)? Split(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        if (Encoding.UTF8.GetByteCount(output) >= EndpointCheckCommands.OutputCap) return null;
        var m = EndMarker().Match(output);
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), output[..m.Index]);
    }

    /// <summary><c>getent ahosts</c>'s answer: every line "address SOCKTYPE [name]", unique by canonical form in answer
    /// order, each kept as answered too. Anything else — no marker, a line that does not read, no address at all — is
    /// unusable.</summary>
    public static ResolveAnswer ParseResolve(string? output)
    {
        if (Split(output) is not { } split) return new ResolveAnswer([], "the answer was cut off or empty (no exit marker)");
        if (split.Code == 2) return new ResolveAnswer([], "the name does not resolve on the host");
        if (split.Code == 127) return new ResolveAnswer([], "getent is not installed on the host");
        if (split.Code != 0) return new ResolveAnswer([], $"getent failed (exit {split.Code})");

        var addresses = new List<ResolvedAddress>();
        foreach (var line in split.Body.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 2 or > 3 || parts[1] is not ("STREAM" or "DGRAM" or "RAW")
                || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var parsed)
                || parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                // IPv4 only as a dotted quad: IPAddress.TryParse would also read "1" or "0x7f.1".
                || (parsed.AddressFamily == AddressFamily.InterNetwork && parsed.ToString() != parts[0]))
                return new ResolveAnswer([], "the host's answer could not be read");
            var canonical = OutboundAddressPolicy.Canonicalize(parsed);
            if (!addresses.Any(a => a.Canonical.Equals(canonical)))
                addresses.Add(new ResolvedAddress(new IPAddress(parsed.GetAddressBytes()), canonical));
        }
        return addresses.Count == 0
            ? new ResolveAnswer([], "the name resolved to no address")
            : new ResolveAnswer(addresses, null);
    }

    /// <summary>curl's answer: <c>__rc=0</c> and "status seconds". <paramref name="expectedStatus"/> null accepts any 2xx.</summary>
    public static ProbeResult ParseHttp(string? output, int? expectedStatus = null)
    {
        if (Split(output) is not { } split) return new ProbeResult(false, "no complete answer (cut off or empty)");
        if (split.Code != 0)
            return new ProbeResult(false, CurlFailure(split.Code), Answered: split.Code is 22 or 35 or 52 or 56 or 60);
        var write = CurlWrite().Match(split.Body.Trim());
        if (!write.Success) return new ProbeResult(false, "curl's answer could not be read");
        var status = int.Parse(write.Groups[1].Value, CultureInfo.InvariantCulture);
        var latency = (long)Math.Round(double.Parse(write.Groups[2].Value, CultureInfo.InvariantCulture) * 1000);
        if (status == 0) return new ProbeResult(false, "no HTTP answer", latency);
        var ok = expectedStatus is { } expected ? status == expected : status is >= 200 and <= 299;
        return new ProbeResult(ok,
            ok ? $"HTTP {status} in {latency} ms"
               : $"HTTP {status}, expected {(expectedStatus is { } e ? e.ToString(CultureInfo.InvariantCulture) : "2xx")}",
            latency, status, Answered: true);
    }

    /// <summary>OpenBSD netcat's answer: <c>__rc=0</c> is a completed connect.</summary>
    public static ProbeResult ParseTcp(string? output)
    {
        if (output is not null && output.Contains("__nc=unsupported", StringComparison.Ordinal) && Split(output) is null)
            return new ProbeResult(false, "unsupported nc variant on the host (OpenBSD netcat is needed)");
        if (Split(output) is not { } split) return new ProbeResult(false, "no complete answer (cut off or empty)");
        return split.Code switch
        {
            0 => new ProbeResult(true, "TCP connect succeeded", Answered: true),
            127 => new ProbeResult(false, "nc is not installed on the host"),
            _ => new ProbeResult(false, $"TCP connect failed (nc exit {split.Code})"),
        };
    }

    /// <summary><c>openssl s_client</c>'s answer: <c>__rc=0</c> and a certificate whose <c>notAfter</c> is at least
    /// <paramref name="warnDays"/> days after <paramref name="nowUtc"/>. Only the first certificate (the server's own)
    /// is read; nothing about it but its expiry is judged.</summary>
    public static ProbeResult ParseTls(string? output, DateTime nowUtc, int warnDays)
    {
        if (Split(output) is not { } split) return new ProbeResult(false, "no complete answer (cut off or empty)");
        if (split.Code == 127) return new ProbeResult(false, "openssl is not installed on the host");
        if (split.Code != 0) return new ProbeResult(false, $"TLS handshake failed (openssl exit {split.Code})");
        var pem = Pem().Match(split.Body);
        if (!pem.Success) return new ProbeResult(false, "no server certificate in the answer");

        DateTime notAfter;
        try
        {
            var der = Convert.FromBase64String(Regex.Replace(pem.Groups[1].Value, @"\s", ""));
            using var cert = X509CertificateLoader.LoadCertificate(der);
            notAfter = cert.NotAfter.ToUniversalTime();
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return new ProbeResult(false, "the server certificate could not be read");
        }

        var left = notAfter - nowUtc;
        var stamp = notAfter.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        if (left <= TimeSpan.Zero)
            return new ProbeResult(false, $"certificate expired {stamp}", TlsNotAfterUtc: notAfter, Answered: true);
        var days = (int)Math.Floor(left.TotalDays);
        return left < TimeSpan.FromDays(warnDays)
            ? new ProbeResult(false, $"certificate expires in {days} day(s) ({stamp}), under the {warnDays}-day limit",
                TlsNotAfterUtc: notAfter, Answered: true)
            : new ProbeResult(true, $"certificate valid for {days} more day(s) ({stamp})", TlsNotAfterUtc: notAfter, Answered: true);
    }

    private static string CurlFailure(int code) => code switch
    {
        6 => "could not resolve the host (curl 6)",
        7 => "connection refused or unreachable (curl 7)",
        28 => "timed out (curl 28)",
        35 => "TLS handshake failed (curl 35)",
        52 => "empty reply from the server (curl 52)",
        56 => "connection reset while receiving (curl 56)",
        60 => "the server certificate is not trusted (curl 60)",
        127 => "curl is not installed on the host",
        _ => $"curl failed (exit {code})",
    };
}
