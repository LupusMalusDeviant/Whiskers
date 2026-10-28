using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Guardrails;

/// <summary>
/// Finds a protected resource in a guardrail request (INV-24, R1-16). Every argument value is matched whole, as
/// before; a free-text argument — a command, SQL, a compose file — is also split into words, and each word (and each
/// segment of a path) is matched on its own, so <c>docker stop whiskers</c> or <c>DROP DATABASE whiskers</c> is caught.
/// Free text that builds what it acts on at run time (command substitution, <c>eval</c>, decoding, dynamic SQL, a
/// shell reached from SQL) cannot be analysed: it counts as touching a protected resource, so it is refused or needs
/// the same explicit approval.
/// </summary>
public static class ProtectedResourceMatcher
{
    /// <summary>Arguments whose value is free text (the same names the audit withholds as payloads, R1-12).</summary>
    public static readonly IReadOnlySet<string> FreeTextArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "command", "query", "sql", "script", "content", "body", "compose", "composeContent", "yaml",
    };

    /// <summary>A hit: the value (a word of free text, or a whole argument) and the pattern it matched; for free text
    /// that cannot be analysed, the argument's name and no pattern.</summary>
    public sealed record Hit(string Value, string? Pattern, string? UnanalyzableArgument = null);

    /// <summary>The first protected resource the request touches, or <c>null</c>. <paramref name="patterns"/> are
    /// matched against names, <paramref name="pathPatterns"/> against path-like words of free text only.</summary>
    public static Hit? FirstHit(IReadOnlyDictionary<string, JsonElement> arguments, IReadOnlyList<string> patterns,
        IReadOnlyList<string> pathPatterns, bool analyseFreeText)
    {
        if (patterns.Count == 0 && pathPatterns.Count == 0) return null;
        foreach (var (key, element) in arguments)
        {
            var value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
            if (Match(value, patterns) is { } whole) return new Hit(value, whole);
            if (!analyseFreeText || !FreeTextArguments.Contains(key) || value.Length == 0) continue;
            foreach (var word in Words(value))
            {
                if (Match(word, patterns) is { } named) return new Hit(word, named);
                if (word.Contains('/') && Match(word.TrimEnd('/'), pathPatterns) is { } path) return new Hit(word, path);
            }
            if (IsUnanalyzable(value)) return new Hit("", null, key);
        }
        return null;
    }

    private static string? Match(string value, IReadOnlyList<string> patterns)
    {
        if (value.Length == 0) return null;
        foreach (var pattern in patterns)
            if (GuardrailRuleHelpers.GlobMatch(pattern, value)) return pattern;
        return null;
    }

    // Shell and SQL punctuation that separates words; '/' separates path segments (a path is also kept whole).
    private static readonly char[] Separators =
        [' ', '\t', '\r', '\n', ';', '|', '&', '(', ')', '<', '>', '\'', '"', '`', ',', '=', '{', '}', '[', ']', ':', '$', '\\', '!', '*', '?', '#', '+'];

    /// <summary>The words of a free-text value, and each segment of a path-like word.</summary>
    public static IEnumerable<string> Words(string text)
    {
        foreach (var word in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return word;
            if (!word.Contains('/')) continue;
            foreach (var segment in word.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (segment != word) yield return segment;
        }
    }

    // What makes free text say something other than what it reads: the shell building a word at run time, decoding,
    // dynamic SQL, SQL reaching the file system or a shell.
    private static readonly Regex Unanalyzable = new(
        @"\$\(|`|\$\{[^}]*[:#%/^,@!]|\beval\b|\bbase64\b|\bxxd\b|\bopenssl\s+enc\b|\\x[0-9a-f]{2}|\\[0-7]{3}|\bprintf\b[^\n]*%b"
        + @"|\bexecute\b|\bexec\s*\(|\bprepare\b|\bcopy\b[^;]*\bprogram\b|\bpg_read_(binary_)?file\b|\blo_(import|export)\b"
        + @"|\bdblink|\bchr\s*\(|\bconvert_from\b|\bdecode\s*\(|\bxp_cmdshell\b|\bload_file\b|\binto\s+(out|dump)file\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Whether the free text builds what it acts on at run time. A pattern that takes too long counts as
    /// unanalysable (fail closed).</summary>
    public static bool IsUnanalyzable(string text)
    {
        try { return Unanalyzable.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return true; }
    }
}
