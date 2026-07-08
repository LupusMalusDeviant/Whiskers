using Markdig;
using Whiskers.Utils;

namespace Whiskers.Tests;

public class MarkdownSanitizerTests
{
    [Theory]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"JavaScript:alert(1)\">x</a>")]
    [InlineData("<a href=\"data:text/html,abc\">x</a>")]
    public void NeutralizesUnsafeHrefs(string html)
    {
        var result = MarkdownSanitizer.NeutralizeUnsafeHrefs(html);
        Assert.Contains("href=\"#\"", result);
        Assert.DoesNotContain("javascript:", result.ToLowerInvariant());
        Assert.DoesNotContain("data:", result.ToLowerInvariant());
    }

    [Theory]
    [InlineData("<a href=\"https://example.com\">x</a>")]
    [InlineData("<a href=\"http://example.com/p?q=1\">x</a>")]
    [InlineData("<a href=\"mailto:a@b.com\">x</a>")]
    [InlineData("<a href=\"#section\">x</a>")]
    public void PreservesSafeHrefs(string html)
        => Assert.Equal(html, MarkdownSanitizer.NeutralizeUnsafeHrefs(html));

    [Fact]
    public void MarkdigRoundTrip_JavascriptLinkIsNeutralized()
    {
        // The real attack path: a model-supplied javascript: link rendered through Markdig.
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
        var html = Markdown.ToHtml("[click me](javascript:alert(document.cookie))", pipeline);
        var safe = MarkdownSanitizer.NeutralizeUnsafeHrefs(html);
        Assert.DoesNotContain("javascript:", safe.ToLowerInvariant());
        Assert.Contains("href=\"#\"", safe);
    }

    [Fact]
    public void NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", MarkdownSanitizer.NeutralizeUnsafeHrefs(null));
        Assert.Equal("", MarkdownSanitizer.NeutralizeUnsafeHrefs(""));
    }
}
