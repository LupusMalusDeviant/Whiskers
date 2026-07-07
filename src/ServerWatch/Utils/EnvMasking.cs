using System.Text.RegularExpressions;

namespace ServerWatch.Utils;

/// <summary>Decides whether a container env-var value should be masked in the UI. Masks a value when its
/// KEY looks sensitive (KEY/SECRET/PASSWORD/TOKEN/…) OR when the VALUE carries inline credentials
/// (<c>scheme://user:pass@host</c>) — so a <c>DATABASE_URL</c>/<c>REDIS_URI</c> no longer shows its password.
/// The credential-in-URL check (rather than blanket URL/URI name keywords) keeps harmless config URLs
/// visible while still masking anything that actually embeds a secret.</summary>
public static partial class EnvMasking
{
    private static readonly string[] SensitiveKeywords =
    {
        "KEY", "SECRET", "PASSWORD", "PASSWD", "TOKEN", "CREDENTIAL", "AUTH",
        "PRIVATE", "PASSPHRASE", "DSN"
    };

    // scheme://[user]:pass@ — user optional so `redis://:pw@host` is caught too; a bare host:port is not.
    [GeneratedRegex(@"://[^/@\s]*:[^/@\s]+@", RegexOptions.Compiled)]
    private static partial Regex CredentialsInUrlRegex();

    public static bool IsSensitiveKey(string key)
    {
        var upper = (key ?? "").ToUpperInvariant();
        return SensitiveKeywords.Any(k => upper.Contains(k, StringComparison.Ordinal));
    }

    public static bool ShouldMask(string key, string? value)
        => IsSensitiveKey(key) || (!string.IsNullOrEmpty(value) && CredentialsInUrlRegex().IsMatch(value));
}
