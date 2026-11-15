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

    // ---- Contract 0017 F1: model output carries no event handler and no style -------------------------------------------

    [Fact]
    public void Agent_markdown_cannot_carry_an_event_handler()
    {
        // The pipeline AgentView.razor used up to 1.0.0 (with generic attributes): the sanitizer alone must strip it.
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
        var html = MarkdownSanitizer.Sanitize(
            Markdown.ToHtml("[details](https://example.com){onmouseover=alert(document.cookie)}", pipeline));
        Assert.False(html.Contains("onmouseover", StringComparison.OrdinalIgnoreCase),
            "model output renders an inline event handler: " + html.Trim());
        // And through the product path (pipeline without generic attributes + sanitizer): the link survives, the
        // attribute block stays visible text and becomes no attribute.
        var rendered = MarkdownSanitizer.RenderModelOutput("[details](https://example.com){onmouseover=alert(document.cookie)}");
        Assert.DoesNotMatch(@"<[^>]*onmouseover", rendered);
        Assert.Contains("<a href=\"https://example.com\">details</a>{onmouseover=", rendered);
    }

    [Theory]
    [InlineData("# Title {onclick=alert(1)}")]
    [InlineData("![x](https://example.com/a.png){onerror=alert(1)}")]
    [InlineData("[x](https://example.com){style=\"position:fixed;inset:0\"}")]
    [InlineData("::: warn {onfocus=alert(1) tabindex=0}\nhi\n:::")]
    [InlineData("| a |\n|---|\n| b |\n{onclick=alert(1)}")]
    public void The_model_output_pipeline_attaches_no_handler_or_style_attribute(string markdown)
    {
        var html = Markdown.ToHtml(markdown, MarkdownSanitizer.ModelOutputPipeline);
        Assert.DoesNotMatch(@"<[a-z][^>]*\s(on[a-z]+|style)\s*=", html.ToLowerInvariant());
        Assert.DoesNotMatch(@"<[a-z][^>]*\s(on[a-z]+|style)\s*=", MarkdownSanitizer.RenderModelOutput(markdown).ToLowerInvariant());
    }

    [Theory]
    [InlineData("<a href=\"https://x\" onclick=\"alert(1)\">x</a>", "<a href=\"https://x\">x</a>")]
    [InlineData("<a ONMOUSEOVER='alert(1)' href=\"https://x\">x</a>", "<a href=\"https://x\">x</a>")]
    [InlineData("<p style=\"color:red\" class=\"c\">x</p>", "<p class=\"c\">x</p>")]
    [InlineData("<img src=\"help/a.png\" onerror=alert(1) />", "<img src=\"help/a.png\" />")]
    [InlineData("<img src=\"https://x/a.png\" alt=\"a\" />", "<img alt=\"a\" />")]
    [InlineData("<p title=\"a onclick=b\">x</p>", "<p title=\"a onclick=b\">x</p>")]       // text inside a value stays
    [InlineData("<p>onclick=&quot;x&quot; is text</p>", "<p>onclick=&quot;x&quot; is text</p>")]
    [InlineData("<a href=\"javascript:alert(1)\" onclick=\"x\">x</a>", "<a href=\"#\">x</a>")]
    public void Sanitize_removes_handler_and_style_attributes_and_unsafe_hrefs(string html, string expected)
        => Assert.Equal(expected, MarkdownSanitizer.Sanitize(html));

    [Theory]
    [InlineData("![x](https://tracker.example/p.png)")]
    [InlineData("![x](//tracker.example/p.png)")]
    [InlineData("![x](http://10.0.0.1/p.png)")]
    [InlineData("![x](javascript:alert(1))")]
    public void An_image_in_model_output_loads_nothing_off_site(string markdown)
    {
        var html = MarkdownSanitizer.RenderModelOutput(markdown);
        Assert.Contains("<img", html);
        Assert.DoesNotContain("src=", html);
    }

    [Theory]
    [InlineData("![x](help/agent.png)", "src=\"help/agent.png\"")]
    [InlineData("![x](data:image/png;base64,iVBORw0KGgo=)", "src=\"data:image/png;base64,iVBORw0KGgo=\"")]
    public void A_relative_or_data_image_stays(string markdown, string expected)
        => Assert.Contains(expected, MarkdownSanitizer.RenderModelOutput(markdown));

    [Fact]
    public void The_model_output_pipeline_has_tables_task_lists_and_autolinks_but_no_media_or_containers()
    {
        var html = MarkdownSanitizer.RenderModelOutput(
            "| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n\nsee https://example.com\n\n![v](https://youtube.com/watch?v=x)\n\n::: warn\nhi\n:::");
        Assert.Contains("<table>", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("<a href=\"https://example.com\">", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<video", html);
        Assert.DoesNotContain("<div class=\"warn\"", html);
    }

    [Fact]
    public void Handler_text_in_a_code_block_stays_visible()
    {
        var html = MarkdownSanitizer.RenderModelOutput("```html\n<a onclick=\"x\" style=\"y\">\n```");
        Assert.Contains("onclick=&quot;x&quot; style=&quot;y&quot;", html);
    }

    [Fact]
    public void Sanitize_drops_every_attribute_of_a_tag_it_cannot_read()
        => Assert.Equal("<p>x</p>", MarkdownSanitizer.Sanitize("<p class=\"a\" \"onclick=alert(1)>x</p>"));

    [Fact]
    public void NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", MarkdownSanitizer.NeutralizeUnsafeHrefs(null));
        Assert.Equal("", MarkdownSanitizer.NeutralizeUnsafeHrefs(""));
    }
}
