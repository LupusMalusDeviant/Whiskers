using System.Text.RegularExpressions;

namespace Whiskers.Tests;

/// <summary>Contract 0017 F2: the CSP is <c>script-src 'self'</c>, without <c>'unsafe-inline'</c> or
/// <c>'unsafe-eval'</c>. A component that renders an inline <c>&lt;script&gt;</c> block or an <c>on*=</c> attribute,
/// or calls <c>eval</c> through JS interop, would silently stop working in the browser; this guard fails instead.</summary>
public sealed class ContentSecurityPolicySourceTests
{
    private static string AppRoot => DeploymentScripts.RepoFile("src", "Whiskers");

    private static IEnumerable<(string Path, string Text)> Files(string pattern) =>
        Directory.EnumerateFiles(AppRoot, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(p => (p, File.ReadAllText(p)));

    // An HTML start tag in markup: a lower-case element name (components such as <MudButton> start upper-case), quoted
    // values consumed whole. C# in @code is not inside such a tag, so `only = "x"` there is not read as an attribute.
    private static readonly Regex HtmlStartTag = new(@"<[a-z][a-z0-9-]*(?:\s(?:""[^""]*""|'[^']*'|[^<>""'])*)?/?>");

    // An on<word>= attribute, quoted or not, any case, not Blazor's @onclick (preceded by '@') or a directive suffix.
    private static readonly Regex Handler = new(@"(?<![@\w.:-])on[a-z]+\s*=", RegexOptions.IgnoreCase);

    internal static IEnumerable<string> InlineHandlers(string razor) =>
        HtmlStartTag.Matches(razor)
            .Select(m => m.Value)
            .Where(tag => Handler.IsMatch(Regex.Replace(tag, @"""[^""]*""|'[^']*'", "\"\"")));   // not text in a value

    [Fact]
    public void No_component_renders_an_inline_script_or_event_handler()
    {
        var inlineScript = new Regex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase);
        var offenders = Files("*.razor")
            .Where(c => inlineScript.IsMatch(c.Text) || InlineHandlers(c.Text).Any())
            .Select(c => c.Path).ToList();
        Assert.True(offenders.Count == 0, "inline script the CSP blocks in: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("<div onmouseover=\"this.style.transform='scale(1.06)'\">", true)]   // what AiChat.razor had
    [InlineData("<div OnMouseOver=\"x()\">", true)]
    [InlineData("<img src=\"a.png\" onerror=alert(1) />", true)]                     // unquoted
    [InlineData("<div @onclick=\"Open\" @onclick:stopPropagation=\"true\">", false)]
    [InlineData("<MudButton OnClick=\"Save\">", false)]                              // a component parameter
    [InlineData("<div title=\"turn onfoo=1 off\">", false)]                          // text inside a value
    [InlineData("@code { private bool only = \"x\" == y; }", false)]
    public void The_handler_scan_reads_html_start_tags_only(string razor, bool offends)
        => Assert.Equal(offends, InlineHandlers(razor).Any());

    [Fact]
    public void No_component_or_class_calls_eval_through_js_interop()
    {
        var eval = new Regex(@"Invoke(Void)?Async(<[^>]*>)?\(\s*""eval""", RegexOptions.IgnoreCase);
        var offenders = Files("*.razor").Concat(Files("*.cs"))
            .Where(c => eval.IsMatch(c.Text)).Select(c => c.Path).ToList();
        Assert.True(offenders.Count == 0, "eval interop the CSP blocks in: " + string.Join(", ", offenders));
    }
}
