using Whiskers.Utils;

namespace Whiskers.Services.Vpn;

/// <summary>
/// The configured values that become VPN CLI arguments (R1-43, INV-41, E-08): the mesh hostname and the control-plane
/// URLs (Tailscale/Headscale login server, NetBird management URL). They are checked where the command is built, and
/// each is passed as one argv element in the <c>--flag=value</c> form, which the CLIs never read as a further option.
/// </summary>
public static class VpnArguments
{
    /// <summary>The hostname to register: an RFC 1123 name (no leading <c>-</c>, no spaces, no <c>=</c>), else throws.</summary>
    public static string RequireHostName(string? value)
    {
        // CommandOperands also admits IPv6 literals; a mesh hostname is a name only.
        if (!CommandOperands.IsSafeHost(value) || value!.Contains(':') || value.Length > 63 * 4)
            throw new UnsafeOperandException("VPN hostname", value,
                "it must be a host name: letters, digits, inner hyphens and dots (no leading '-', no spaces, no '=')");
        return value;
    }

    /// <summary>An absolute http(s) URL without user info or whitespace, else throws. Returned as configured.</summary>
    public static string RequireControlUrl(string? value, string field)
    {
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
            // The refused value is named without its user info: that part can be a password or a token.
            throw new UnsafeOperandException(field, Whiskers.Services.Mutations.PolicyDecisions.WithoutUrlCredentials(value),
                "it must be an absolute http(s) URL without spaces or user info");
        return value;
    }

    /// <summary>CLI output fit for a log line: known secret shapes redacted, and the key itself removed wherever it
    /// appears, also as a fragment of at least eight characters (a CLI may echo part of it).</summary>
    public static string Scrub(string? text, string? secret)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var scrubbed = SecretRedactor.Redact(text);
        if (string.IsNullOrWhiteSpace(secret)) return scrubbed;
        var key = secret.Trim();
        scrubbed = scrubbed.Replace(key, "[redacted]", StringComparison.Ordinal);
        // Any 8-character window of the key: a prefix or suffix printed by the tool is still part of the secret.
        const int Window = 8;
        for (var i = 0; key.Length >= Window && i <= key.Length - Window; i++)
        {
            var part = key.Substring(i, Window);
            if (scrubbed.Contains(part, StringComparison.Ordinal))
                scrubbed = scrubbed.Replace(part, "[redacted]", StringComparison.Ordinal);
        }
        return scrubbed;
    }
}
