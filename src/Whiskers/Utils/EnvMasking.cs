using System.Text.RegularExpressions;

namespace Whiskers.Utils;

/// <summary>Decides whether a container env-var value is masked — the one rule of the web UI and of the MCP tool
/// <c>get_container_env</c> (INV-16, INV-24; contract 0017 D). Masks a value when its KEY looks sensitive
/// (KEY/SECRET/PASS/PWD/TOKEN/…) OR when the VALUE carries credentials: inline in a URL
/// (<c>scheme://user:pass@host</c>, so a <c>DATABASE_URL</c>/<c>REDIS_URI</c> no longer shows its password) or as a
/// <c>Password=</c>/<c>Pwd=</c> pair of a connection string. The credential checks on the value (rather than blanket
/// URL/URI name keywords) keep harmless config URLs visible while still masking anything that embeds a secret. The
/// shell's own <c>PWD</c> and <c>OLDPWD</c> (working directories) are no secret and stay visible.</summary>
public static partial class EnvMasking
{
    private static readonly string[] SensitiveKeywords =
    {
        "KEY", "SECRET", "PASS", "PWD", "TOKEN", "CREDENTIAL", "AUTH",
        "PRIVATE", "DSN"
        // PASS covers PASSWORD, PASSWD and PASSPHRASE.
    };

    /// <summary>Shell variables that contain a keyword but hold a directory, compared as whole names.</summary>
    private static readonly HashSet<string> ShellDirectories = new(StringComparer.Ordinal) { "PWD", "OLDPWD" };

    // scheme://[user]:pass@ — user optional so `redis://:pw@host` is caught too; a bare host:port is not.
    [GeneratedRegex(@"://[^/@\s]*:[^/@\s]+@", RegexOptions.Compiled)]
    private static partial Regex CredentialsInUrlRegex();

    // A connection string's password pair: "Password=…", "Pwd=…" (ADO.NET, ODBC), case-insensitive, also as the end
    // of a longer key (trustStorePassword=, SslPassword=, keyPassword=).
    [GeneratedRegex(@"(?i)(?:password|pwd)\s*=", RegexOptions.Compiled)]
    private static partial Regex ConnectionStringPasswordRegex();

    public static bool IsSensitiveKey(string key)
    {
        var upper = (key ?? "").ToUpperInvariant();
        if (ShellDirectories.Contains(upper)) return false;
        return SensitiveKeywords.Any(k => upper.Contains(k, StringComparison.Ordinal));
    }

    public static bool ShouldMask(string key, string? value)
        => IsSensitiveKey(key)
           || (!string.IsNullOrEmpty(value)
               && (CredentialsInUrlRegex().IsMatch(value) || ConnectionStringPasswordRegex().IsMatch(value)));
}
