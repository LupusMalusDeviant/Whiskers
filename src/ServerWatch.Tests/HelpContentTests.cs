using ServerWatch.Models.Help;
using ServerWatch.Services.Help;

namespace ServerWatch.Tests;

public class HelpContentTests
{
    private readonly IReadOnlyList<HelpChapter> _chapters = new HelpContentService().GetChapters();

    [Fact]
    public void Has_a_substantial_set_of_chapters()
    {
        Assert.True(_chapters.Count >= 10, $"expected a full handbook, got {_chapters.Count} chapters");
    }

    [Fact]
    public void Chapter_ids_are_unique()
    {
        var ids = _chapters.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Every_chapter_has_title_icon_and_sections()
    {
        foreach (var c in _chapters)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Id));
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Icon));
            Assert.NotEmpty(c.Sections);
        }
    }

    [Fact]
    public void Every_section_has_heading_and_prose()
    {
        foreach (var s in _chapters.SelectMany(c => c.Sections))
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Heading));
            Assert.False(string.IsNullOrWhiteSpace(s.Markdown));
        }
    }

    [Fact]
    public void Figures_are_well_formed()
    {
        foreach (var fig in _chapters.SelectMany(c => c.Sections).Select(s => s.Figure).Where(f => f is not null))
        {
            Assert.False(string.IsNullOrWhiteSpace(fig!.Caption));
            if (fig.Kind == HelpFigureKind.Svg)
                Assert.Contains("<svg", fig.Svg ?? "", StringComparison.Ordinal);
        }
    }
}
