using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Whiskers.Tests.Claims;

// Contract 0011, Part B (INV-56): the mechanical rule of what counts as a claim. Test code; nothing ships.

/// <summary>The fixed parts of the claim rule (D8, D9, D11).</summary>
public static class ClaimRules
{
    /// <summary>The governed documents (D8) and the claim-id prefix of each. Adding a file is one line here plus its
    /// markers.</summary>
    public static readonly IReadOnlyList<(string File, string Prefix)> GovernedFiles =
    [
        ("README.md", "RM"),
        ("SECURITY.md", "SEC"),
        ("docs/security-architecture.md", "SA"),
        ("docs/operations/production-checklist.md", "PC"),
    ];

    public const string Register = "docs/v1/CLAIMS.md";

    /// <summary>A block holding a word that starts with one of these stems (case-insensitive) is claim-shaped (D11).</summary>
    public static readonly string[] Triggers =
    [
        "always", "never", "only", "every", "all", "none", "no", "default", "cannot", "must", "encrypt", "plaintext",
        "hash", "sign", "verif", "refus", "reject", "block", "delet", "remov", "pin", "protect", "owner-only",
        "constant time", "secure",
    ];

    /// <summary>The fixed reasons of a claim-free block (D9).</summary>
    public static readonly string[] Reasons = ["policy", "advice", "limit", "history", "pointer"];

    /// <summary>The fixed kinds of check (D9); a cell may join several with " + ".</summary>
    public static readonly string[] Checks = ["constant", "default", "config", "artifact", "behaviour", "readiness"];

    public static readonly Regex Marker = new(@"<!--\s*claim(?<free>-free)?:\s*(?<value>[^>]*?)\s*-->", RegexOptions.CultureInvariant);
    public static readonly Regex ClaimId = new(@"^(?<prefix>RM|SEC|SA|PC)-\d{2}$", RegexOptions.CultureInvariant);

    private static readonly Regex Link = new(@"!?\[(?<text>[^\]]*)\]\((?<url>[^)]*)\)", RegexOptions.CultureInvariant);
    private static readonly Regex Image = new(@"!\[[^\]]*\]\([^)]*\)",RegexOptions.CultureInvariant);
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.CultureInvariant);
    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex TriggerWord = new(
        @"(?<![\p{L}\p{N}])(?:" + string.Join("|", Triggers.Select(Regex.Escape)) + ")",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The block text with its markers removed and whitespace collapsed: what the fingerprint and the quotes see.</summary>
    public static string Normalize(string text) => Regex.Replace(Marker.Replace(text, " "), @"\s+", " ").Trim();

    /// <summary>First 12 hex digits of SHA-256 over <see cref="Normalize"/>d text (D11).</summary>
    public static string Fingerprint(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))))[..12];

    /// <summary>What the claim-shape test reads: no comments, link text without its destination, no bare URLs.</summary>
    public static string Readable(string text)
        => Url.Replace(Link.Replace(Image.Replace(Comment.Replace(text, " "), " "), m => m.Groups["text"].Value), " ");

    /// <summary>A backticked span, a digit, or a word starting with a trigger stem (D11).</summary>
    public static bool IsClaimShaped(string text)
    {
        var readable = Readable(text);
        return readable.Contains('`') || readable.Any(char.IsAsciiDigit) || TriggerWord.IsMatch(readable);
    }

    /// <summary>A line holding only links or images (and punctuation), markers aside.</summary>
    public static bool IsLinkOnly(string line)
    {
        var rest = Marker.Replace(line, " ");
        if (!Image.IsMatch(rest) && !Link.IsMatch(rest)) return false;
        // A badge is an image inside a link: the image goes first, which leaves an empty link text.
        rest = Link.Replace(Image.Replace(rest, ""), " ");
        return !rest.Any(char.IsLetterOrDigit);
    }

    /// <summary>The claim-id prefix of a governed file, or null.</summary>
    public static string? PrefixOf(string file) => GovernedFiles.FirstOrDefault(g => g.File == file).Prefix;
}
