using System.Text.RegularExpressions;
using Whiskers.Models.Agent;

namespace Whiskers.Services.Agent.Chat;

/// <summary>Splits an agent reply into renderable segments, extracting the curated widget tokens
/// (<c>[[chart:…]]</c> / <c>[[status:…]]</c>) from the surrounding markdown text. The token grammar
/// is fixed and closed — anything that doesn't match exactly stays plain text, so the model can never
/// inject arbitrary components or HTML.</summary>
public interface IChatWidgetParser
{
    /// <summary>Parses <paramref name="text"/> into ordered text/widget segments.
    /// Returns a single text segment when there are no widget tokens.</summary>
    IReadOnlyList<ChatSegment> Parse(string? text);

    /// <summary>True if the text contains at least one widget token (cheap pre-check).</summary>
    bool HasWidgets(string? text);
}

public sealed partial class ChatWidgetParser : IChatWidgetParser
{
    // [[chart:server:<id>:cpu]] | [[chart:container:<id>:mem]] | [[status:server:<id>]] | [[status:container:<id>]]
    // <id> excludes ':' and ']' so it can't swallow the closing brackets or the metric segment.
    [GeneratedRegex(@"\[\[(chart|status):(server|container):([^:\]]+?)(?::(cpu|mem|memory|ram))?\]\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public bool HasWidgets(string? text) => !string.IsNullOrEmpty(text) && TokenRegex().IsMatch(text);

    public IReadOnlyList<ChatSegment> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new[] { ChatSegment.OfText(text ?? string.Empty) };

        var segments = new List<ChatSegment>();
        var last = 0;
        foreach (Match m in TokenRegex().Matches(text))
        {
            if (m.Index > last)
                segments.Add(ChatSegment.OfText(text[last..m.Index]));

            segments.Add(ChatSegment.OfWidget(ToSpec(m)));
            last = m.Index + m.Length;
        }

        if (segments.Count == 0) return new[] { ChatSegment.OfText(text) };
        if (last < text.Length) segments.Add(ChatSegment.OfText(text[last..]));
        return segments;
    }

    private static ChatWidgetSpec ToSpec(Match m)
    {
        var kind = m.Groups[1].Value.Equals("status", StringComparison.OrdinalIgnoreCase)
            ? ChatWidgetKind.Status : ChatWidgetKind.Chart;
        var target = m.Groups[2].Value.Equals("server", StringComparison.OrdinalIgnoreCase)
            ? ChatWidgetTarget.Server : ChatWidgetTarget.Container;
        var id = m.Groups[3].Value.Trim();
        var metric = m.Groups[4].Value.ToLowerInvariant() switch
        {
            "mem" or "memory" or "ram" => ChatWidgetMetric.Memory,
            _ => ChatWidgetMetric.Cpu,
        };
        return new ChatWidgetSpec(kind, target, id, metric);
    }
}
