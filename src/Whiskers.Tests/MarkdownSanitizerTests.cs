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

    [Theory]
    [InlineData("[click][x]\n\n[x]: javascript:alert(1)")]           // reference-style link
    [InlineData("[click](JaVaScRiPt:alert(1))")]                       // mixed case
    [InlineData("[click](  javascript:alert(1)  )")]                   // padded
    [InlineData("<javascript:alert(1)>")]                              // autolink syntax
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]        // data: URL
    [InlineData("[![img](https://example.com/a.png)](javascript:x)")]  // image inside a link
    public void MarkdigRoundTrip_EveryLinkForm_ReachesTheSanitizerAsAnHref(string markdown)
    {
        // Whatever Markdig version renders the reply, every link form must come out as an href the sanitizer sees: a
        // change in Markdig's output (another attribute, quoting) would otherwise slip past the regex.
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
        var safe = MarkdownSanitizer.NeutralizeUnsafeHrefs(Markdown.ToHtml(markdown, pipeline)).ToLowerInvariant();

        Assert.DoesNotContain("href=\"javascript:", safe);
        Assert.DoesNotContain("href=\"data:", safe);
        Assert.DoesNotMatch(@"<a\b(?![^>]*href=""(#|https?://|mailto:))[^>]*href=", safe);
        // Review round 2 (n1): the input did become a link (and the image in it an image), so the checks above saw one.
        Assert.Contains("<a href=\"#\"", safe);
        if (markdown.Contains("![img]", StringComparison.Ordinal)) Assert.Contains("<img", safe);
    }

    [Fact]
    public void NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", MarkdownSanitizer.NeutralizeUnsafeHrefs(null));
        Assert.Equal("", MarkdownSanitizer.NeutralizeUnsafeHrefs(""));
    }
}
