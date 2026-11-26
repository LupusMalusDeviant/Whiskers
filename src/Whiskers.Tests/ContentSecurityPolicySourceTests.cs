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
            foreach (var color in new[] { "#2196F3", "#9C27B0" })   // both colours the pages pass
            {
                var args = builder.GetParameters().Select(p => p.ParameterType == typeof(string) ? (object)color
                    : throw new InvalidOperationException($"{builder.DeclaringType?.Name}.{builder.Name}: unexpected parameter {p.Name}")).ToArray();
                if (SerializeChartOptions(builder.Invoke(null, args)!).Contains("@eval", StringComparison.Ordinal))
                    offenders.Add($"{builder.DeclaringType?.Name}.{builder.Name}({color})");
            }
        Assert.True(offenders.Count == 0, "chart options the CSP refuses to evaluate: " + string.Join(", ", offenders));

        // What a chart sends is what such a builder returned: every chart file passes the source rules below.
        var chartFiles = Files("*.razor").Concat(Files("*.cs"))
            .Where(c => c.Text.Contains("ApexChart", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(chartFiles);
        var violations = chartFiles.SelectMany(c => ChartFileViolations(c.Text).Select(v => $"{c.Path}: {v}")).ToList();
        Assert.True(violations.Count == 0, "chart options not from a checked builder: " + string.Join("; ", violations));
    }

    // Where ApexChartOptions<…> may stand in a chart file: a field (or property) set from a Create… builder, the builder's
    // own signature, and the builder's `return new …`. Anything else (a local, inline options in markup, a field set
    // otherwise) would send options the serialized check never saw.
    private static readonly Regex OptionsField =
        new(@"(?:[\w.]+\.)?ApexChartOptions<[\w.]+>\s+(?<name>\w+)\s*(?:\{\s*get;[^}]*\}\s*)?=\s*Create\w*\(");
    private static readonly Regex BuilderSignature = new(@"static\s+(?:[\w.]+\.)?ApexChartOptions<[\w.]+>\s+Create\w*\(");
    private static readonly Regex BuilderReturn = new(@"return\s+new\s+(?:[\w.]+\.)?ApexChartOptions<");
    private static readonly Regex AnyOptions = new(@"ApexChartOptions<");
    private static readonly Regex ChartTag = new(@"<(?:ApexCharts\.)?ApexChart\b(?<attrs>[^>]*)>");
    private static readonly Regex Attribute = new(@"(?<name>@?[\w:]+)\s*=\s*""(?<value>[^""]*)""");

    // The component parameters a chart may set: the others (FormatYAxisLabel, toolbar icons, annotation events) make the
    // component add an evaluated function of its own, which the serialized options do not show.
    private static readonly HashSet<string> ChartParameters = new(StringComparer.Ordinal) { "TItem", "Height", "Width", "Options", "@ref", "@key" };

    internal static IEnumerable<string> ChartFileViolations(string text)
    {
        var allowed = OptionsField.Matches(text).Concat(BuilderSignature.Matches(text)).Concat(BuilderReturn.Matches(text))
            .Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        foreach (Match m in AnyOptions.Matches(text))
            if (!allowed.Any(a => m.Index >= a.Start && m.Index < a.End))
                yield return $"ApexChartOptions outside a builder field or builder at {m.Index}";

        var fields = OptionsField.Matches(text).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            // Set again only from a builder.
            foreach (Match m in Regex.Matches(text, $@"(?<![\w.]){Regex.Escape(field)}\s*=(?![=>])\s*(?<rhs>[^;\r\n]*)"))
                if (!Regex.IsMatch(m.Groups["rhs"].Value, @"^Create\w*\("))
                    yield return $"{field} set to something else than a builder";
            // Never changed after it was built (a read, a lambda or a comparison is not a change).
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(field)}\s*(\.|\[)[^;\r\n]*?(?<![=!<>])=(?![=>])"))
                yield return $"{field} changed after it was built";
        }

        foreach (Match tag in ChartTag.Matches(text))
            foreach (Match a in Attribute.Matches(tag.Groups["attrs"].Value))
            {
                var name = a.Groups["name"].Value;
                if (!ChartParameters.Contains(name))
                    yield return $"chart parameter {name}";
                else if (name == "Options" && !fields.Contains(a.Groups["value"].Value.TrimStart('@')))
                    yield return $"chart options {a.Groups["value"].Value} not a builder field";
            }
    }

    [Theory]
    [InlineData("private ApexCharts.ApexChartOptions<MetricPoint> _o = CreateOptions(\"#1\");\n" +
                "private static ApexCharts.ApexChartOptions<MetricPoint> CreateOptions(string c) => new() { };\n" +
                "<ApexCharts.ApexChart TItem=\"MetricPoint\" Height=\"200\" Options=\"@_o\">\n" +
                "void Swap() { _o = CreateOptions(\"#2\"); var n = _o.Series.Count(s => s.Name != null); }", false)]   // as the pages are
    [InlineData("private ApexCharts.ApexChartOptions<MetricPoint> _o = CreateOptions(\"#1\");\n" +
                "void Swap() { _o = new() { DataLabels = new ApexCharts.DataLabels { Formatter = \"v => v\" } }; }", true)]   // set again
    [InlineData("private ApexCharts.ApexChartOptions<MetricPoint> _o = CreateOptions(\"#1\");\n" +
                "<ApexCharts.ApexChart TItem=\"MetricPoint\" Options=\"@(new ApexCharts.ApexChartOptions<MetricPoint> { })\">", true)]   // inline
    [InlineData("var o = new ApexCharts.ApexChartOptions<Whiskers.Services.Metrics.MetricPoint> { };", true)]   // a local
    [InlineData("ApexChartOptions<MetricPoint> Options { get; } = new() { };", true)]   // a property not from a builder
    [InlineData("private ApexCharts.ApexChartOptions<MetricPoint> _o = CreateOptions(\"#1\");\n" +
                "void Tweak() { _o.Yaxis[0].Labels.Formatter = \"v => v\"; }", true)]   // changed afterwards
    [InlineData("private ApexCharts.ApexChartOptions<MetricPoint> _o = CreateOptions(\"#1\");\n" +
                "<ApexCharts.ApexChart TItem=\"MetricPoint\" Options=\"@_o\" FormatYAxisLabel=\"@Format\">", true)]   // component adds eval
    public void The_chart_file_rules_find_options_the_serialized_check_does_not_see(string source, bool offends)
        => Assert.Equal(offends, ChartFileViolations(source).Any());
}
