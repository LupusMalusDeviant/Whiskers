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
/// <item><see cref="Sanitize"/> then reads every tag (all three quoting forms) and fails closed: a tag that is not on
/// the list Markdig emits, or that it cannot parse, is escaped to text. From the tags it keeps it removes every
/// <c>on*</c> and <c>style</c> attribute and every URL attribute other than <c>href</c> and an <c>img src</c>
/// (<c>srcset</c>, <c>formaction</c>, <c>action</c>, <c>xlink:href</c>, …); an <c>href</c> that isn't
/// <c>http(s)://</c>, <c>mailto:</c> or a <c>#</c>-fragment becomes <c>"#"</c> (<c>DisableHtml()</c> alone still renders
/// <c>[text](javascript:…)</c> as an active anchor), and an <c>img src</c> that is neither relative nor <c>data:</c> is
/// dropped. Text and code between the tags are untouched.</item>
/// </list></summary>
public static partial class MarkdownSanitizer
{
    // A tag, start or end, whose quoted values are consumed whole (so '<' or '>' inside a value cannot end it early),
    // or else a lone '<' that starts no readable tag.
    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:""[^""]*""|'[^']*'|[^<>""'])*)>|<")]
    private static partial Regex TagRegex();

    // One attribute, anchored where the previous one ended, so a quoted value is consumed whole and never re-read.
    [GeneratedRegex(@"\G\s+([^\s=/>""']+)(?:\s*=\s*(""[^""]*""|'[^']*'|[^\s""'=<>`]+))?")]
    private static partial Regex AttributeRegex();

    // A URL scheme: letters, then letters/digits/+.- up to a ':'.
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:")]
    private static partial Regex SchemeRegex();

    // The elements the model-output pipeline emits. Anything else is escaped, not passed.
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "b", "blockquote", "br", "code", "del", "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "img",
        "input", "ins", "li", "mark", "ol", "p", "pre", "s", "span", "strong", "sub", "sup", "table", "tbody", "td",
        "tfoot", "th", "thead", "tr", "ul",
    };

    // Attributes that load or navigate to a URL, other than href and img src (checked below): always dropped.
    private static readonly HashSet<string> DroppedUrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "srcset", "srcdoc", "formaction", "action", "xlink:href", "poster", "background", "data", "codebase",
        "cite", "ping", "manifest", "lowsrc", "dynsrc",
    };

    /// <summary>The Markdown pipeline for model output: an explicit extension list, raw HTML escaped.</summary>
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

    /// <summary>Sanitizes rendered HTML (see the class summary). Fails closed: what it cannot read becomes text.</summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html ?? "";
        return TagRegex().Replace(html, m =>
        {
            if (!m.Groups[2].Success) return "&lt;";                                  // a '<' that starts no tag
            var tag = m.Groups[2].Value;
            if (!AllowedTags.Contains(tag)) return WebUtility.HtmlEncode(m.Value);
            if (m.Groups[1].Value == "/")
                return string.IsNullOrWhiteSpace(m.Groups[3].Value) ? "</" + tag + ">" : WebUtility.HtmlEncode(m.Value);
            return SanitizeStartTag(tag, m.Groups[3].Value) ?? WebUtility.HtmlEncode(m.Value);
        });
    }

    /// <summary>The start tag with only safe attributes, or null when its attributes cannot be read.</summary>
    private static string? SanitizeStartTag(string tag, string attributes)
    {
        var trimmed = attributes.TrimEnd();
        var selfClosing = trimmed.EndsWith('/') && (trimmed.Length == 1 || char.IsWhiteSpace(trimmed[^2]) || trimmed[^2] is '"' or '\'');
        var body = selfClosing ? trimmed[..^1] : attributes;
        var kept = new StringBuilder();
        var position = 0;
        var regex = AttributeRegex();
        for (var m = regex.Match(body, 0); m.Success && m.Index == position; m = regex.Match(body, position))
        {
            var name = m.Groups[1].Value;
            var value = m.Groups[2].Success ? Unquote(m.Groups[2].Value) : "";
            if (name.Equals("href", StringComparison.OrdinalIgnoreCase))
                kept.Append(IsAllowedHref(value) ? m.Value : " href=\"#\"");
            else if (name.Equals("src", StringComparison.OrdinalIgnoreCase) && tag.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                if (IsAllowedImageSource(value)) kept.Append(m.Value);
            }
            else if (!name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                     && !name.Equals("style", StringComparison.OrdinalIgnoreCase)
                     && !DroppedUrlAttributes.Contains(name))
                kept.Append(m.Value);
            position = m.Index + m.Length;
        }
        if (!string.IsNullOrWhiteSpace(body[position..])) return null;           // unreadable rest: escape the tag
        return "<" + tag + kept + (selfClosing ? " />" : ">");
    }

    private static string Unquote(string raw) =>
        raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0] ? raw[1..^1] : raw;

    // What the browser sees: entities decoded, and the ASCII whitespace/control characters it strips from URLs removed.
    private static string NormalizeUrl(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        var sb = new StringBuilder(decoded.Length);
        foreach (var c in decoded)
            if (c > ' ') sb.Append(c);
        return sb.ToString();
    }

    private static bool IsAllowedHref(string value)
    {
        var url = NormalizeUrl(value);
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith('#');
    }

    // An image in model output loads nothing off-site (no tracking pixel, no request to an address the model chose):
    // a data: URL or a relative path only. Anything with a scheme or a host ("//…", "\\…") is dropped.
    private static bool IsAllowedImageSource(string value)
    {
        var url = NormalizeUrl(value);
        if (url.Length == 0) return false;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;   // inert in an img, loads nothing
        if (url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith('\\') || url.StartsWith("/\\", StringComparison.Ordinal))
            return false;
        return !SchemeRegex().IsMatch(url);
    }
}
