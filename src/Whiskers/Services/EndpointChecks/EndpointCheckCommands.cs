using System.Net;
using System.Net.Sockets;
using Whiskers.Utils;

namespace Whiskers.Services.EndpointChecks;

/// <summary>
/// R1-57 (contract 0004): the fixed command templates of an endpoint check. Pure. Every operand was validated by
/// <see cref="EndpointCheckValidator"/> before it gets here and is single-quoted all the same; an IP address is always
/// one Whiskers judged (a canonical <see cref="IPAddress"/>), never text from the host. Each template bounds its output
/// with <c>head -c</c> and writes its exit marker <c>__rc=</c> inside that bound, so a cut-off answer has no marker and
/// is read as unusable, never as success.
/// </summary>
public static class EndpointCheckCommands
{
    /// <summary>The most bytes any template lets through.</summary>
    public const int OutputCap = 16_384;

    private static readonly string Cap = $" | head -c {OutputCap}";

    /// <summary><c>getent ahosts</c> for <paramref name="name"/>, on the host, with its own resolver.</summary>
    public static string Resolve(string name)
    {
        RequireName(name);
        return $"( getent ahosts {ShellUtils.Quote(name)} 2>&1; echo \"__rc=$?\" ){Cap}";
    }

    /// <summary>One HTTP request to <paramref name="url"/>, pinned to <paramref name="address"/>: for a name with
    /// <c>--resolve</c> (curl does no lookup of its own), for an IP-literal URL directly. No redirects (<c>-L</c> is
    /// absent), only http/https (<c>--proto</c>), no proxy, no <c>.curlrc</c> (<c>-q</c> first).</summary>
    public static string Http(string url, string host, IPAddress address, int port, int timeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (!url.StartsWith("http://", StringComparison.Ordinal) && !url.StartsWith("https://", StringComparison.Ordinal))
            throw new ArgumentException("Only an http or https URL.", nameof(url));
        RequirePort(port);
        RequireTimeout(timeoutSeconds);
        var resolve = IsLiteral(host)
            ? ""
            : $" --resolve {ShellUtils.Quote($"{RequireName(host)}:{port}:{Bracketed(address)}")}";
        // -g (--globoff): no URL globbing, on top of the validator's bracket and brace checks.
        return "( curl -q -g -s -S -o /dev/null -w '%{http_code} %{time_total}' --proto '=http,https' " +
               $"--max-time {timeoutSeconds}{resolve} --noproxy '*' {ShellUtils.Quote(url)} 2>&1; echo \"__rc=$?\" ){Cap}";
    }

    /// <summary>A TCP connect to <paramref name="address"/>:<paramref name="port"/> with OpenBSD netcat. Another nc
    /// variant answers <c>__nc=unsupported</c> (its flags differ, so its answer is not guessed at); no nc at all
    /// answers <c>__rc=127</c>.</summary>
    public static string Tcp(IPAddress address, int port, int timeoutSeconds)
    {
        RequirePort(port);
        RequireTimeout(timeoutSeconds);
        return "( if ! command -v nc >/dev/null 2>&1; then echo \"__rc=127\"; " +
               "elif nc -h 2>&1 | grep -q 'OpenBSD netcat'; then " +
               $"nc -z -w {timeoutSeconds} {ShellUtils.Quote(Canonical(address))} {ShellUtils.Quote(port.ToString(System.Globalization.CultureInfo.InvariantCulture))} 2>&1; echo \"__rc=$?\"; " +
               $"else echo \"__nc=unsupported\"; fi ){Cap}";
    }

    /// <summary>A TLS handshake with <paramref name="address"/>:<paramref name="port"/>; the certificate is read in
    /// Whiskers. <paramref name="serverName"/> is sent as SNI for a name, never for an IP literal. A handshake that hangs
    /// is ended by the executor call's own timeout.</summary>
    public static string Tls(string serverName, IPAddress address, int port)
    {
        RequirePort(port);
        var sni = IsLiteral(serverName) ? "" : $" -servername {ShellUtils.Quote(RequireName(serverName))}";
        var connect = ShellUtils.Quote($"{Bracketed(address)}:{port}");
        return $"( openssl s_client -connect {connect}{sni} -showcerts </dev/null 2>&1; echo \"__rc=$?\" ){Cap}";
    }

    private static bool IsLiteral(string host) => IPAddress.TryParse(host, out _);

    private static string RequireName(string name)
    {
        if (!CommandOperands.IsSafeHost(name) || name.StartsWith('-'))
            throw new UnsafeOperandException("host", name, "an RFC 1123 host name");
        return name;
    }

    private static void RequirePort(int port)
    {
        if (!CommandOperands.IsValidPort(port)) throw new ArgumentOutOfRangeException(nameof(port));
    }

    private static void RequireTimeout(int seconds)
    {
        if (seconds is < EndpointCheckValidator.MinTimeoutSeconds or > EndpointCheckValidator.MaxTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds));
    }

    private static string Canonical(IPAddress address) => new IPAddress(address.GetAddressBytes()).ToString();

    private static string Bracketed(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Canonical(address)}]" : Canonical(address);
}
