using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Utils;

namespace Whiskers.Services.EndpointChecks;

/// <summary>
/// R1-57 (contract 0004): whether a check may be saved — and, with the server as it is now, run. Pure. Everything that
/// later becomes a command operand is decided here, before anything runs: a value that fails is refused, never escaped
/// into shape.
/// </summary>
public static partial class EndpointCheckValidator
{
    public const int MinIntervalSeconds = 60;
    public const int MaxIntervalSeconds = 86_400;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 30;
    public const int MaxNameLength = 80;
    public const int MaxUrlLength = 2048;

    // What a URL may hold, beyond the parsed checks: no quote, no whitespace, no shell metacharacter, no curl glob
    // braces. Square brackets are allowed only around an IPv6 host (checked below).
    [GeneratedRegex(@"^[A-Za-z0-9\-._~:/?#\[\]@+,=%]+\z")]
    private static partial Regex UrlCharacters();

    /// <summary>Every reason the draft cannot be saved (or run); empty when it can.</summary>
    public static IReadOnlyList<string> Validate(EndpointCheckDraft draft, Models.ServerConfig? server)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var errors = new List<string>();

        if (server is null) errors.Add("The server does not exist.");
        else if (!HasShellTransport(server))
            errors.Add($"Server '{server.Name}' has no shell transport (a Kubernetes server needs an SSH host): a check cannot run there.");

        var name = draft.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength) errors.Add($"The name must be 1–{MaxNameLength} characters.");

        if (draft.IntervalSeconds is < MinIntervalSeconds or > MaxIntervalSeconds)
            errors.Add($"The interval must be {MinIntervalSeconds}–{MaxIntervalSeconds} seconds.");
        if (draft.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            errors.Add($"The timeout must be {MinTimeoutSeconds}–{MaxTimeoutSeconds} seconds.");

        switch (draft.Kind)
        {
            case EndpointCheckKind.Http:
                if (draft.TlsWarnDays is not null) errors.Add("Warning days apply to a TLS check only.");
                if (draft.ExpectedStatus is { } status && status is < 100 or > 599)
                    errors.Add("The expected status must be 100–599.");
                break;
            case EndpointCheckKind.Tcp:
                if (draft.ExpectedStatus is not null) errors.Add("An expected status applies to an HTTP check only.");
                if (draft.TlsWarnDays is not null) errors.Add("Warning days apply to a TLS check only.");
                break;
            case EndpointCheckKind.TlsExpiry:
                if (draft.ExpectedStatus is not null) errors.Add("An expected status applies to an HTTP check only.");
                if (draft.TlsWarnDays is { } days && days is < 1 or > 365) errors.Add("Warning days must be 1–365.");
                break;
            default:
                errors.Add("Unknown check kind.");
                break;
        }

        if (Enum.IsDefined(draft.Kind)
            && !TryTarget(draft, out _, out var targetError))
            errors.Add(targetError!);

        foreach (var entry in draft.Exceptions ?? [])
        {
            try { OutboundException.Parse(entry); }
            catch (ArgumentException ex) { errors.Add("Exception: " + ex.Message); }
        }
        if ((draft.Exceptions?.Count ?? 0) > OutboundException.MaxEntries)
            errors.Add($"At most {OutboundException.MaxEntries} exceptions.");

        return errors;
    }

    /// <summary>Whether Whiskers can run a command on <paramref name="server"/>: local, the mTLS Docker connection, or SSH.</summary>
    public static bool HasShellTransport(Models.ServerConfig server) =>
        server.ConnectionType is ConnectionType.Local or ConnectionType.TCP || !string.IsNullOrWhiteSpace(server.SshHost);

    /// <summary>The validated target of a check: the host as it goes into a command, whether it is an IP literal, the
    /// effective port and, for HTTP, the URL.</summary>
    public sealed record Target(string Host, IPAddress? Literal, int Port, string? Url);

    /// <summary>Reads the draft's target; false with the reason when it is not acceptable.</summary>
    public static bool TryTarget(EndpointCheckDraft draft, out Target target, out string? error)
    {
        target = new Target("", null, 0, null);
        error = null;
        var raw = draft.Target?.Trim() ?? "";
        if (raw.Length == 0) { error = "A target is required."; return false; }

        if (draft.Kind == EndpointCheckKind.Http)
        {
            if (raw.Length > MaxUrlLength) { error = $"The URL may be at most {MaxUrlLength} characters."; return false; }
            if (!UrlCharacters().IsMatch(raw))
            {
                error = "The URL holds characters that are not accepted (quotes, whitespace, shell or glob characters).";
                return false;
            }
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                error = "The target must be an absolute http:// or https:// URL.";
                return false;
            }
            if (!raw.StartsWith(url.Scheme + "://", StringComparison.Ordinal))
            {
                error = "The URL must start with http:// or https:// in lower case.";
                return false;
            }
            if (url.UserInfo.Length > 0) { error = "The URL must not hold a user name or password."; return false; }

            // Brackets only around an IPv6 host: anywhere else curl would read them as a glob range.
            var afterHost = raw[(url.Scheme.Length + 3)..];
            var hostEnd = afterHost.IndexOfAny(['/', '?', '#']);
            var authority = hostEnd >= 0 ? afterHost[..hostEnd] : afterHost;
            var rest = hostEnd >= 0 ? afterHost[hostEnd..] : "";
            if (rest.IndexOfAny(['[', ']']) >= 0 || authority.Contains('@'))
            {
                error = "Square brackets are accepted only around an IPv6 address, and the URL must not name a user.";
                return false;
            }

            // The host as it is written, not as Uri normalised it: Uri reads "0x7f.1" as 127.0.0.1 and shortens an IPv6
            // address, and curl and the host's resolver must see exactly what was judged.
            string host;
            if (authority.StartsWith('['))
            {
                var close = authority.IndexOf(']');
                if (close < 0 || url.HostNameType != UriHostNameType.IPv6)
                {
                    error = "Square brackets are accepted only around an IPv6 address.";
                    return false;
                }
                host = authority[1..close];
            }
            else
            {
                if (authority.IndexOfAny(['[', ']']) >= 0)
                {
                    error = "Square brackets are accepted only around an IPv6 address.";
                    return false;
                }
                var colon = authority.IndexOf(':');
                host = colon >= 0 ? authority[..colon] : authority;
            }
            if (!TryHost(host, out var literal, out error)) return false;
            var port = url.Port;
            if (!CommandOperands.IsValidPort(port)) { error = "The URL's port must be 1–65535."; return false; }
            if (draft.Port is { } given && given != port)
            {
                error = $"The port {given} does not match the URL's port {port}.";
                return false;
            }
            target = new Target(literal?.ToString() ?? host.ToLowerInvariant(), literal, port, raw);
            return true;
        }

        if (!TryHost(raw, out var lit, out error)) return false;
        if (draft.Port is not { } p || !CommandOperands.IsValidPort(p))
        {
            error = "A TCP or TLS check needs a port (1–65535).";
            return false;
        }
        target = new Target(lit?.ToString() ?? raw.ToLowerInvariant(), lit, p, null);
        return true;
    }

    /// <summary>A host name (RFC 1123, never an option) or a canonical IP literal. A literal must be written exactly as
    /// it reads back — <c>2130706433</c>, <c>0177.0.0.1</c>, <c>0x7f.1</c>, a zone or brackets are refused, because the
    /// host's resolver would read them as an address Whiskers did not judge.</summary>
    internal static bool TryHost(string host, out IPAddress? literal, out string? error)
    {
        literal = null;
        error = null;
        if (OutboundAddressPolicy.TryParseLiteral(host, out var parsed))
        {
            if (host.Contains('%') || host.StartsWith('[')
                || !string.Equals(parsed.ToString(), host, StringComparison.OrdinalIgnoreCase)
                || !IPAddress.TryParse(host, out var strict) || strict.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            {
                error = $"\"{host}\" is not a canonical IP address (write it as {parsed}).";
                return false;
            }
            literal = new IPAddress(parsed.GetAddressBytes());
            return true;
        }
        if (!CommandOperands.IsSafeHost(host) || host.EndsWith('.') || OutboundException.NormalizeName(host) is null
            || host.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            error = $"\"{host}\" is not a valid host name.";
            return false;
        }
        return true;
    }

    /// <summary>The run's deadline: every attempt's timeout (1 resolve + 4 probes) plus 10 s.</summary>
    public static TimeSpan RunDeadline(int timeoutSeconds) => TimeSpan.FromSeconds(timeoutSeconds * 5 + 10);

    internal static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
