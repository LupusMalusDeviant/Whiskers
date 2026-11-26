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

    /// <summary>The chart options ApexCharts always serializes as a JS function (<c>{"@eval": …}</c>, its
    /// <c>FunctionStringConverter</c>): the client evaluates each with <c>eval</c>, which the CSP refuses — the chart
    /// throws and the circuit ends (2026-11-26, the y-axis label formatter on the dashboard).</summary>
    internal static IReadOnlyDictionary<string, HashSet<string>> ApexEvalOptions() =>
        typeof(ApexCharts.YAxis).Assembly.GetTypes()
            .Where(t => t.Namespace == "ApexCharts")
            .SelectMany(t => t.GetProperties().Select(p => (Type: t.Name, Property: p)))
            .Where(x => x.Property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonConverterAttribute), true)
                .Cast<System.Text.Json.Serialization.JsonConverterAttribute>()
                .Any(a => a.ConverterType?.Name == "FunctionStringConverter"))
            .GroupBy(x => x.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Property.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    // An object initializer of an ApexCharts option type, its own level only: new ApexCharts.YAxisLabels { … }.
    private static readonly Regex ApexInitializer = new(@"new\s+ApexCharts\.(\w+)\s*\{([^{}]*)\}");

    // A string that contains the word "function": the list converters evaluate such an entry (case-insensitive).
    private static readonly Regex FunctionText = new(@"""[^""\r\n]*function[^""\r\n]*""", RegexOptions.IgnoreCase);

    internal static IEnumerable<string> ApexEvalUses(string source)
    {
        var options = ApexEvalOptions();
        foreach (Match m in ApexInitializer.Matches(source))
            if (options.TryGetValue(m.Groups[1].Value, out var names))
                foreach (var name in names)
                    if (Regex.IsMatch(m.Groups[2].Value, $@"\b{Regex.Escape(name)}\s*=(?!=)"))
                        yield return $"{m.Groups[1].Value}.{name}";
        if (source.Contains("ApexCharts", StringComparison.Ordinal))
            foreach (Match m in FunctionText.Matches(source))
                yield return m.Value;
    }

    [Theory]
    [InlineData("Labels = new ApexCharts.YAxisLabels { Formatter = \"function(v) { return v + '%' }\" }", true)]   // the crash
    [InlineData("Labels = new ApexCharts.YAxisLabels { Formatter = \"v => v\" }", true)]   // evaluated whatever it says
    [InlineData("Yaxis = new ApexCharts.YAxis { DecimalsInFloat = 0, Title = new ApexCharts.AxisTitle { Text = \"%\" } }", false)]
    [InlineData("<MudTextField Label=\"Name\" /> new ApexCharts.Tooltip { X = new ApexCharts.TooltipX { Format = \"HH:mm\" } }", false)]
    public void The_chart_scan_finds_evaluated_options_only(string source, bool offends)
        => Assert.Equal(offends, ApexEvalUses(source).Any());

    [Fact]
    public void No_chart_sets_an_option_that_ApexCharts_evaluates_as_JavaScript()
    {
        Assert.Contains("Formatter", ApexEvalOptions()["YAxisLabels"]);
        var offenders = Files("*.razor").Concat(Files("*.cs"))
            .SelectMany(c => ApexEvalUses(c.Text).Select(hit => $"{c.Path} ({hit})")).ToList();
        Assert.True(offenders.Count == 0, "chart options the CSP refuses to evaluate in: " + string.Join(", ", offenders));
    }
}
