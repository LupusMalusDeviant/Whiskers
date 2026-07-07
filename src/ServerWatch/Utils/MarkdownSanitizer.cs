using System.Text.RegularExpressions;

namespace ServerWatch.Utils;

/// <summary>Post-processes rendered (LLM) markdown HTML to neutralize dangerous link targets. Markdig's
/// <c>DisableHtml()</c> escapes raw HTML but still renders <c>[text](javascript:…)</c> as an active anchor —
/// a one-click XSS vector in a high-privilege UI. Any href that isn't <c>http(s)://</c>, <c>mailto:</c> or a
/// <c>#</c>-fragment (so <c>javascript:</c>, <c>data:</c>, etc.) is rewritten to <c>"#"</c>.</summary>
public static partial class MarkdownSanitizer
{
    [GeneratedRegex(@"href\s*=\s*""(?!https?://|mailto:|#)[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeHrefRegex();

    public static string NeutralizeUnsafeHrefs(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html ?? "";
        return UnsafeHrefRegex().Replace(html, "href=\"#\"");
    }
}
