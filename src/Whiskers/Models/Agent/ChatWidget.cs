namespace Whiskers.Models.Agent;

/// <summary>The kind of inline widget an agent reply can embed.</summary>
public enum ChatWidgetKind { Chart, Status }

/// <summary>What the widget refers to.</summary>
public enum ChatWidgetTarget { Server, Container }

/// <summary>Which metric a chart widget shows.</summary>
public enum ChatWidgetMetric { Cpu, Memory }

/// <summary>A curated, parsed widget reference embedded in an agent reply via a token such as
/// <c>[[chart:server:&lt;id&gt;:cpu]]</c> or <c>[[status:container:&lt;id&gt;]]</c>.
/// Only this small, fixed set is ever rendered — arbitrary HTML is never honoured.</summary>
public sealed record ChatWidgetSpec(
    ChatWidgetKind Kind,
    ChatWidgetTarget Target,
    string Id,
    ChatWidgetMetric Metric = ChatWidgetMetric.Cpu);

/// <summary>One piece of a parsed agent reply: either a run of text (markdown) or a widget.
/// Exactly one of <see cref="Text"/> / <see cref="Widget"/> is non-null.</summary>
public sealed record ChatSegment(string? Text, ChatWidgetSpec? Widget)
{
    public static ChatSegment OfText(string text) => new(text, null);
    public static ChatSegment OfWidget(ChatWidgetSpec spec) => new(null, spec);
    public bool IsWidget => Widget is not null;
}
