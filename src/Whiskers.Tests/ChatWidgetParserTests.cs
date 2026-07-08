using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Chat;

namespace Whiskers.Tests;

public class ChatWidgetParserTests
{
    private readonly ChatWidgetParser _parser = new();

    [Fact]
    public void Plain_text_yields_one_text_segment()
    {
        var segs = _parser.Parse("Alles im grünen Bereich.");
        Assert.Single(segs);
        Assert.False(segs[0].IsWidget);
        Assert.Equal("Alles im grünen Bereich.", segs[0].Text);
        Assert.False(_parser.HasWidgets("Alles im grünen Bereich."));
    }

    [Fact]
    public void Null_or_empty_yields_one_empty_text_segment()
    {
        Assert.Equal("", Assert.Single(_parser.Parse(null)).Text);
        Assert.Equal("", Assert.Single(_parser.Parse("")).Text);
    }

    [Fact]
    public void Parses_a_container_cpu_chart_token()
    {
        var segs = _parser.Parse("Hier: [[chart:container:abc123:cpu]] schau.");
        Assert.Equal(3, segs.Count);
        Assert.Equal("Hier: ", segs[0].Text);
        var w = segs[1].Widget!;
        Assert.Equal(ChatWidgetKind.Chart, w.Kind);
        Assert.Equal(ChatWidgetTarget.Container, w.Target);
        Assert.Equal("abc123", w.Id);
        Assert.Equal(ChatWidgetMetric.Cpu, w.Metric);
        Assert.Equal(" schau.", segs[2].Text);
    }

    [Theory]
    [InlineData("mem")]
    [InlineData("memory")]
    [InlineData("ram")]
    public void Memory_aliases_map_to_memory_metric(string alias)
    {
        var w = Assert.Single(_parser.Parse($"[[chart:server:srv1:{alias}]]").Where(s => s.IsWidget)).Widget!;
        Assert.Equal(ChatWidgetMetric.Memory, w.Metric);
        Assert.Equal(ChatWidgetTarget.Server, w.Target);
    }

    [Fact]
    public void Status_token_defaults_metric_to_cpu()
    {
        var w = Assert.Single(_parser.Parse("[[status:container:xyz]]").Where(s => s.IsWidget)).Widget!;
        Assert.Equal(ChatWidgetKind.Status, w.Kind);
        Assert.Equal("xyz", w.Id);
    }

    [Fact]
    public void Multiple_tokens_back_to_back_are_all_parsed()
    {
        var segs = _parser.Parse("[[status:server:s1]][[chart:server:s1:cpu]]");
        Assert.Equal(2, segs.Count);
        Assert.True(segs.All(s => s.IsWidget));
    }

    [Fact]
    public void Unknown_or_malformed_tokens_stay_text()
    {
        const string text = "[[chart:cluster:foo:cpu]] and [[bogus]] and [[chart:container]]";
        var segs = _parser.Parse(text);
        Assert.Single(segs);
        Assert.False(segs[0].IsWidget);
        Assert.Equal(text, segs[0].Text);
    }

    [Fact]
    public void Token_is_case_insensitive()
    {
        var w = Assert.Single(_parser.Parse("[[CHART:Container:ID1:CPU]]").Where(s => s.IsWidget)).Widget!;
        Assert.Equal(ChatWidgetKind.Chart, w.Kind);
        Assert.Equal("ID1", w.Id);
    }
}
