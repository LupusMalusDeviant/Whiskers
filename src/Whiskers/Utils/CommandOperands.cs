using System.Net;
using System.Text.RegularExpressions;

namespace Whiskers.Utils;

/// <summary>
/// The one place that decides whether a caller-supplied value may become an operand of a host command (R1-42,
/// INV-41): git refs, SSH user/host/port, nginx site names and container references. <see cref="ShellUtils.Quote"/>
/// only keeps a value away from the <em>shell</em>; once the shell has removed the quotes the program still receives
/// the literal token, and a token that starts with <c>-</c> is parsed as an option (<c>-oProxyCommand=…</c>,
/// <c>--upload-pack=…</c>). So every such value is checked here against a strict allow-list that never admits a
/// leading <c>-</c>, and the command builders additionally put positional operands after <c>--</c> (or in a form
/// that cannot be an option) where the program supports it. The checks run at the execution boundary — the code
/// that builds the command — not only in the UI, so a hand-edited store or an MCP caller meets the same rule.
/// </summary>
public static class CommandOperands
{
    // Branch names: a strict subset of `git check-ref-format --branch`. Leading alphanumeric (never `-` or `.`),
    // a conservative charset, and the structural rules checked in IsSafeGitRef.
    private static readonly Regex GitRefRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled);

    // POSIX-portable user names (useradd's default rule, without the trailing `$`): never a leading `-`, no `@`.
    private static readonly Regex SshUserRegex = new(@"^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    // RFC 1123 host names: labels of letters, digits and inner hyphens, so no label (and no name) starts with `-`.
    private static readonly Regex HostNameRegex = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?$",
        RegexOptions.Compiled);

    // IP literals as ssh takes them (IPv4 dotted, IPv6 without brackets or zone id).
    private static readonly Regex IpLiteralRegex = new(@"^[0-9A-Fa-f.:]+$", RegexOptions.Compiled);

    // nginx site file names: leading alphanumeric, letters/digits/dot/hyphen/underscore, no `..` (checked separately).
    private static readonly Regex SiteNameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    // Docker container ids and names (Docker's own name rule: [a-zA-Z0-9][a-zA-Z0-9_.-]*).
    private static readonly Regex ContainerRefRegex = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    /// <summary>True for a branch name that git takes as a ref and never as an option: leading alphanumeric, only
    /// <c>A–Z a–z 0–9 . _ / -</c>, no <c>..</c>, <c>//</c> or <c>@{</c>, no component starting with <c>.</c>, not
    /// ending in <c>/</c>, <c>.</c> or <c>.lock</c>, at most 255 characters.</summary>
    public static bool IsSafeGitRef(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 255 || !GitRefRegex.IsMatch(value)) return false;
        if (value.Contains("..") || value.Contains("//") || value.Contains("/.")) return false;
        if (value.EndsWith('/') || value.EndsWith('.') || value.EndsWith(".lock", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>True for an SSH login name: letters, digits, <c>. _ -</c>, not starting with <c>-</c> or <c>.</c>,
    /// at most 64 characters.</summary>
    public static bool IsSafeSshUser(string? value) => !string.IsNullOrEmpty(value) && SshUserRegex.IsMatch(value);

    /// <summary>True for a host name (RFC 1123) or an IPv4/IPv6 literal; never a value starting with <c>-</c>, never
    /// one with <c>@</c>, <c>/</c>, whitespace or a scheme.</summary>
    public static bool IsSafeHost(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253) return false;
        if (HostNameRegex.IsMatch(value)) return true;
        return IpLiteralRegex.IsMatch(value) && value.Contains(':') && IPAddress.TryParse(value, out _);
    }

    /// <summary>True for a TCP port number (1–65535).</summary>
    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>True for an nginx site file name: leading alphanumeric, letters, digits, <c>. _ -</c>, no <c>..</c>,
    /// at most 255 characters — so it can neither be an option nor leave its directory.</summary>
    public static bool IsSafeSiteName(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 255 && !value.Contains("..") && SiteNameRegex.IsMatch(value);

    /// <summary>True for a Docker container id or name (Docker's own rule; never a leading <c>-</c>).</summary>
    public static bool IsSafeContainerRef(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 255 && ContainerRefRegex.IsMatch(value);

    /// <summary>Returns <paramref name="value"/> when it is a safe git ref, otherwise throws.</summary>
    public static string RequireGitRef(string? value)
        => IsSafeGitRef(value) ? value! : throw new UnsafeOperandException("git branch", value,
            "it must start with a letter or digit and use only letters, digits and . _ / - (no leading '-', no '..', no spaces)");

    /// <summary>Returns <paramref name="value"/> when it is a safe nginx site name, otherwise throws.</summary>
    public static string RequireSiteName(string? value)
        => IsSafeSiteName(value) ? value! : throw new UnsafeOperandException("nginx site name", value);

    /// <summary>Returns <paramref name="value"/> when it is a safe container reference, otherwise throws.</summary>
    public static string RequireContainerRef(string? value)
        => IsSafeContainerRef(value) ? value! : throw new UnsafeOperandException("container", value);

    /// <summary>The <c>user@host</c> destination for ssh, after checking user, host and port; throws on any of them.
    /// Callers put it after <c>--</c>, behind every option.</summary>
    public static string SshDestination(string? user, string? host, int port)
    {
        if (!IsSafeSshUser(user)) throw new UnsafeOperandException("SSH user", user);
        if (!IsSafeHost(host)) throw new UnsafeOperandException("SSH host", host);
        if (!IsValidPort(port)) throw new UnsafeOperandException("SSH port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "it must be a number from 1 to 65535");
        return $"{user}@{host}";
    }
}

/// <summary>A value refused as a command operand (R1-42). An <see cref="ArgumentException"/>, so callers that already
/// turn invalid arguments into an error answer keep doing so; the message names the field and the rule, the value is
/// shown only shortened (it is configuration, not a secret, but it can be long).</summary>
public sealed class UnsafeOperandException(string field, string? value, string? rule = null)
    : ArgumentException($"Invalid {field} '{Shorten(value)}': {rule ?? DefaultRule}.")
{
    /// <summary>Which operand was refused, e.g. <c>SSH host</c>.</summary>
    public string Field { get; } = field;

    private const string DefaultRule =
        "it must start with a letter or digit and use only letters, digits and . _ - (no leading '-', no '..', no spaces)";

    private static string Shorten(string? value)
        => value is null ? "" : value.Length <= 64 ? value : value[..64] + "…";
}
