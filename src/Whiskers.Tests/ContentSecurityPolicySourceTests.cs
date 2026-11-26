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

    // ---- Charts (2026-11-26): ApexCharts sends every option it holds for a JS function as {"@eval": …}, and the client
    // evaluates it with eval, which the CSP refuses — the chart throws and the circuit ends (the y-axis label formatter
    // on the dashboard). The check reads the options as the library serializes them, so every converter and every way
    // of setting an option is covered.

    /// <summary>The library's own serialization of chart options, the JSON its <c>renderChart</c> receives.</summary>
    internal static string SerializeChartOptions(object options)
    {
        var serializer = typeof(ApexCharts.YAxis).Assembly.GetType("ApexCharts.Internal.ChartSerializer", throwOnError: true)!;
        var method = serializer.GetMethod("SerializeOptions",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)method.Invoke(null, [options])!;
    }

    /// <summary>Every static method in Whiskers that builds chart options (a page's or widget's <c>CreateChartOptions</c>).</summary>
    private static IReadOnlyList<System.Reflection.MethodInfo> ChartOptionBuilders() =>
        typeof(Whiskers.Services.Metrics.MetricPoint).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                          | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType.IsGenericType
                        && m.ReturnType.GetGenericTypeDefinition() == typeof(ApexCharts.ApexChartOptions<>))
            .ToList();

    [Fact]
    public void The_serialized_check_sees_an_evaluated_option()
    {
        var formatter = new ApexCharts.ApexChartOptions<Whiskers.Services.Metrics.MetricPoint>
        {
            Yaxis = [new ApexCharts.YAxis { Labels = new ApexCharts.YAxisLabels { Formatter = "function(v) { return v + '%' }" } }],
        };
        Assert.Contains("@eval", SerializeChartOptions(formatter));   // the crash
        var arrow = new ApexCharts.ApexChartOptions<Whiskers.Services.Metrics.MetricPoint>
        {
            DataLabels = new ApexCharts.DataLabels { Formatter = "(v) => { return v + '%' }" },
        };
        Assert.Contains("@eval", SerializeChartOptions(arrow));
        var plain = new ApexCharts.ApexChartOptions<Whiskers.Services.Metrics.MetricPoint>
        {
            Yaxis = [new ApexCharts.YAxis { DecimalsInFloat = 0, Title = new ApexCharts.AxisTitle { Text = "%" } }],
        };
        Assert.DoesNotContain("@eval", SerializeChartOptions(plain));
    }

    [Fact]
    public void No_chart_sends_an_option_that_ApexCharts_evaluates_as_JavaScript()
    {
        var builders = ChartOptionBuilders();
        var offenders = new List<string>();
        foreach (var builder in builders)
        {
            var args = builder.GetParameters().Select(p => p.ParameterType == typeof(string) ? (object)"#2196F3"
                : throw new InvalidOperationException($"{builder.DeclaringType?.Name}.{builder.Name}: unexpected parameter {p.Name}")).ToArray();
            if (SerializeChartOptions(builder.Invoke(null, args)!).Contains("@eval", StringComparison.Ordinal))
                offenders.Add($"{builder.DeclaringType?.Name}.{builder.Name}");
        }
        Assert.True(offenders.Count == 0, "chart options the CSP refuses to evaluate: " + string.Join(", ", offenders));

        // Every chart file builds its options in such a method and changes none of them afterwards — otherwise the check
        // above would not see what the chart sends.
        var chartFiles = Files("*.razor").Concat(Files("*.cs"))
            .Where(c => c.Text.Contains("ApexChartOptions<", StringComparison.Ordinal)).ToList();
        Assert.Equal(chartFiles.Count,
            builders.Select(b => b.DeclaringType!).Select(t => t.DeclaringType ?? t).Distinct().Count());
        var initializer = new Regex(@"ApexChartOptions<\w+>\s+(_\w+)\s*=\s*(?<init>[^;]*);");
        foreach (var (path, text) in chartFiles)
            foreach (Match m in initializer.Matches(text))
            {
                Assert.Matches(@"^Create\w*\(", m.Groups["init"].Value.Trim());
                Assert.DoesNotMatch($@"\b{Regex.Escape(m.Groups[1].Value)}\s*(\.|\[)[^;]*?(?<![=!<>])=(?!=)", text);
            }
    }
}
