using System.Text.RegularExpressions;
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
        var result = MarkdownSanitizer.Sanitize(html);
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
        => Assert.Equal(html, MarkdownSanitizer.Sanitize(html));

    [Fact]
    public void MarkdigRoundTrip_JavascriptLinkIsNeutralized()
    {
        // The real attack path: a model-supplied javascript: link rendered through Markdig.
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
        var html = Markdown.ToHtml("[click me](javascript:alert(document.cookie))", pipeline);
        var safe = MarkdownSanitizer.Sanitize(html);
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
        var safe = MarkdownSanitizer.Sanitize(Markdown.ToHtml(markdown, pipeline)).ToLowerInvariant();

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
    public void Sanitize_escapes_a_tag_it_cannot_read()
    {
        var html = MarkdownSanitizer.Sanitize("<p class=\"a\" \"onclick=alert(1)>x</p>");
        Assert.Equal("&lt;p class=\"a\" \"onclick=alert(1)>x</p>", html);
        Assert.DoesNotMatch(@"<p\b", html);
    }

    // ---- Review of cdabe7d1: Sanitize is fail-closed on raw HTML too, not only on what Markdig emits ---------------------

    /// <summary>No start tag left in the output carries a handler, a style, a srcset/formaction/action, an iframe, or a
    /// URL attribute outside the allowed forms.</summary>
    private static void AssertInert(string html)
    {
        foreach (Match tag in Regex.Matches(html, @"<[a-zA-Z](?:""[^""]*""|'[^']*'|[^<>""'])*>"))
        {
            var t = tag.Value.ToLowerInvariant();
            var names = Regex.Replace(t, @"""[^""]*""|'[^']*'", "\"\"");   // text inside a value is no attribute
            Assert.DoesNotMatch(@"[\s/""'](on[a-z]+|style|srcset|formaction|action|xlink:href|srcdoc)\s*=", names);
            Assert.DoesNotMatch(@"^<(iframe|script|object|embed|form|svg|math|base|meta|link|style)\b", t);
            foreach (Match href in Regex.Matches(t, @"\shref\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)"))
                Assert.Matches(@"^[""']?(https?://|mailto:|#)", href.Groups[1].Value);
        }
    }

    [Theory]
    [InlineData("<a href=\"https://x\" title=\"<\" onclick=\"alert(1)\">x</a>")]          // '<' inside a quoted value
    [InlineData("<a href=\"https://x\" title=\">\" onclick=\"alert(1)\">x</a>")]          // '>' inside a quoted value
    [InlineData("<a href='javascript:alert(1)'>x</a>")]                                   // single-quoted href
    [InlineData("<a href=javascript:alert(1)>x</a>")]                                     // unquoted href
    [InlineData("<a href=\" java&#x09;script:alert(1)\">x</a>")]                          // entity + whitespace in the scheme
    [InlineData("<a HREF=\"JAVASCRIPT:alert(1)\">x</a>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]                        // iframe src
    [InlineData("<iframe srcdoc=\"&lt;script&gt;alert(1)&lt;/script&gt;\"></iframe>")]
    [InlineData("<form action=\"https://evil\"><button formaction=\"javascript:alert(1)\">x</button></form>")]
    [InlineData("<input type=\"image\" formaction=\"javascript:alert(1)\" src=\"x\">")]  // formaction on an allowed tag
    [InlineData("<img srcset=\"https://evil.example/p.png 1x\" src=\"a.png\">")]          // srcset
    [InlineData("<img src=x onerror=alert(1)>")]                                          // unquoted handler
    [InlineData("<img/src=x/onerror=alert(1)>")]                                          // '/' as separator
    [InlineData("<a href=\"https://x\"onclick=\"alert(1)\">x</a>")]                       // no space after a value
    [InlineData("<svg><a xlink:href=\"javascript:alert(1)\">x</a></svg>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<p style='position:fixed' ONCLICK='alert(1)'>x</p>")]
    [InlineData("<a href=\"https://x\" title='a\" onclick=\"alert(1)'>x</a>")]            // a quote inside the other quote
    [InlineData("<<a href=javascript:alert(1)>x</a>")]
    [InlineData("</a onclick=\"alert(1)\">")]                                             // attributes on an end tag
    public void Sanitize_leaves_no_active_attribute_in_raw_html(string payload)
        => AssertInert(MarkdownSanitizer.Sanitize(payload));

    [Fact]
    public void Sanitize_keeps_the_safe_parts_of_a_tag_with_a_quoted_angle_bracket()
        => Assert.Equal("<a href=\"https://x\" title=\"<\">x</a>",
            MarkdownSanitizer.Sanitize("<a href=\"https://x\" title=\"<\" onclick=\"alert(1)\">x</a>"));

    [Theory]
    [InlineData("<a href='javascript:alert(1)'>x</a>")]
    [InlineData("<a href=javascript:alert(1)>x</a>")]
    public void An_unsafe_href_in_any_quoting_form_becomes_a_fragment(string payload)
        => Assert.Equal("<a href=\"#\">x</a>", MarkdownSanitizer.Sanitize(payload));

    [Fact]
    public void NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", MarkdownSanitizer.Sanitize(null));
        Assert.Equal("", MarkdownSanitizer.Sanitize(""));
    }
}
