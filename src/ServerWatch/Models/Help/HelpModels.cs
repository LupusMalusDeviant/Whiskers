namespace ServerWatch.Models.Help;

/// <summary>One top-level chapter of the in-app user handbook (e.g. "CVE-Monitor").</summary>
public sealed record HelpChapter(
    string Id,
    string Title,
    string Icon,
    string Summary,
    IReadOnlyList<HelpSection> Sections);

/// <summary>A section within a chapter: a heading, Markdown prose, and an optional figure.</summary>
public sealed record HelpSection(
    string Heading,
    string Markdown,
    HelpFigure? Figure = null);

public enum HelpFigureKind
{
    /// <summary>Inline SVG illustration/diagram (theme-aware via CSS variables).</summary>
    Svg,
    /// <summary>A real screenshot image referenced by its public path (<see cref="HelpFigure.Image"/>).</summary>
    Image,
    /// <summary>A labelled placeholder where a real screenshot can be dropped in later.</summary>
    Screenshot,
}

/// <summary>An illustration attached to a section: an inline SVG diagram, a real screenshot image,
/// or a placeholder marking where a real screenshot belongs.</summary>
public sealed record HelpFigure(
    HelpFigureKind Kind,
    string Caption,
    string? Svg = null,
    string? Image = null);
