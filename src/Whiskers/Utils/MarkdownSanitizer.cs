using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace Whiskers.Utils;

/// <summary>Renders model (LLM) output as Markdown and post-processes the HTML so it can carry no script.
/// <list type="bullet">
/// <item>The pipeline (<see cref="ModelOutputPipeline"/>) escapes raw HTML and enables only pipe tables, task lists,
/// emphasis extras and autolinks — no generic attributes (so <c>[x](https://…){onmouseover=…}</c> attaches nothing),
/// no media links, no custom containers.</item>
/// <item><see cref="Sanitize"/> then removes every attribute whose name starts with <c>on</c> and every <c>style</c>
/// attribute from the rendered tags (text and code are untouched), drops an <c>img src</c> that is neither relative nor
/// <c>data:</c>, and rewrites every href that isn't <c>http(s)://</c>, <c>mailto:</c> or a
/// <c>#</c>-fragment (so <c>javascript:</c>, <c>data:</c>, …) to <c>"#"</c>: <c>DisableHtml()</c> alone still renders
/// <c>[text](javascript:…)</c> as an active anchor.</item>
/// </list></summary>
public static partial class MarkdownSanitizer
{
    [GeneratedRegex(@"href\s*=\s*""(?!https?://|mailto:|#)[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeHrefRegex();

    // A start tag. Markdig escapes '<', '>' and '"' in text and attribute values, so '>' only ever closes a tag.
    [GeneratedRegex(@"<([a-zA-Z][a-zA-Z0-9-]*)([^<>]*)>")]
    private static partial Regex StartTagRegex();

    // One attribute, anchored where the previous one ended, so a quoted value is consumed whole and never re-read.
    [GeneratedRegex(@"\G\s+([^\s=/>""']+)(?:\s*=\s*(""[^""]*""|'[^']*'|[^\s""'=<>`]+))?")]
    private static partial Regex AttributeRegex();

    /// <summary>The Markdown pipeline for model output: the advanced extensions without generic attributes, raw HTML
    /// escaped.</summary>
    public static MarkdownPipeline ModelOutputPipeline { get; } = BuildModelOutputPipeline();

    // An explicit list instead of UseAdvancedExtensions(): no generic attributes, no media links (iframe/video), no
    // custom containers.
    private static MarkdownPipeline BuildModelOutputPipeline() => new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    /// <summary>Renders model output to HTML that is safe to show as a <c>MarkupString</c>.</summary>
    public static string RenderModelOutput(string? markdown)
        => Sanitize(Markdown.ToHtml(markdown ?? "", ModelOutputPipeline));

    /// <summary>Removes event-handler (<c>on*</c>) and <c>style</c> attributes from every tag, then neutralizes unsafe
    /// hrefs. A tag whose attributes cannot be read is emitted without any (fail closed).</summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html ?? "";
        var stripped = StartTagRegex().Replace(html, m => StripAttributes(m.Groups[1].Value, m.Groups[2].Value));
        return NeutralizeUnsafeHrefs(stripped);
    }

    /// <summary>Rewrites every href that isn't <c>http(s)://</c>, <c>mailto:</c> or a <c>#</c>-fragment to <c>"#"</c>.</summary>
    public static string NeutralizeUnsafeHrefs(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html ?? "";
        return UnsafeHrefRegex().Replace(html, "href=\"#\"");
    }

    private static string StripAttributes(string tag, string attributes)
    {
        var selfClosing = attributes.TrimEnd().EndsWith('/');
        var body = selfClosing ? attributes.TrimEnd()[..^1] : attributes;
        var kept = new StringBuilder();
        var position = 0;
        var regex = AttributeRegex();
        for (var m = regex.Match(body, 0); m.Success && m.Index == position; m = regex.Match(body, position))
        {
            var name = m.Groups[1].Value;
            var drop = name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                || name.Equals("style", StringComparison.OrdinalIgnoreCase)
                || (tag.Equals("img", StringComparison.OrdinalIgnoreCase)
                    && name.Equals("src", StringComparison.OrdinalIgnoreCase)
                    && !IsAllowedImageSource(m.Groups[2].Value));
            if (!drop) kept.Append(m.Value);
            position = m.Index + m.Length;
        }
        if (!string.IsNullOrWhiteSpace(body[position..])) kept.Clear();   // unreadable rest: keep no attribute at all
        return "<" + tag + kept + (selfClosing ? " />" : ">");
    }

    // An image in model output loads nothing off-site (no tracking pixel, no request to an address the model chose):
    // a data: URL or a relative path only. Anything with a scheme or a host ("//…", "\\…") is dropped.
    private static bool IsAllowedImageSource(string rawValue)
    {
        var value = WebUtility.HtmlDecode(rawValue.Trim('"', '\'')).Trim();
        if (value.Length == 0) return false;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;   // inert in an img, loads nothing
        if (value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith('\\') || value.StartsWith("/\\", StringComparison.Ordinal))
            return false;
        return !SchemeRegex().IsMatch(value);
    }

    // A URL scheme: letters, then letters/digits/+.- up to the first ':' before any '/', '?' or '#'.
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:")]
    private static partial Regex SchemeRegex();
}
