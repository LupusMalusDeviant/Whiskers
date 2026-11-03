using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>
/// R1-41 (A-16): a name in a Prometheus label is escaped as the exposition format requires, so a container, image or
/// server name with a quote, a backslash or a line break cannot end the label, start a new sample or forge a series.
/// </summary>
public class MetricsLabelEscapingTests
{
    [Theory]
    [InlineData("web", "web")]
    [InlineData("say \"hi\"", "say \\\"hi\\\"")]
    [InlineData(@"C:\data", @"C:\\data")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("a\r\nb", "a\r\\nb")]
    [InlineData("a\rb", "a\rb")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_label_value_is_escaped(string? value, string expected)
        => Assert.Equal(expected, PrometheusText.Label(value));

    [Fact]
    public void A_name_cannot_forge_a_sample()
    {
        // A container named to close the label and add its own series.
        var hostile = "x\"} 1\nwhiskers_container_up{name=\"forged";
        var line = $"whiskers_container_up{{name=\"{PrometheusText.Label(hostile)}\"}} 0";

        Assert.DoesNotContain('\n', line);
        // Exactly one unescaped closing of the label set: the one the exporter wrote.
        var unescapedQuotes = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '"') unescapedQuotes++;
        }
        Assert.Equal(2, unescapedQuotes);
    }

    [Fact]
    public void Distinct_names_stay_distinct_labels()
    {
        string[] names = ["a\rb", "a\nb", "a\r\nb", "a\\nb", "a\\\nb"];
        Assert.Equal(names.Length, names.Select(PrometheusText.Label).Distinct().Count());
        Assert.All(names.Select(PrometheusText.Label), l => Assert.DoesNotContain('\n', l));
    }

    [Theory]
    [InlineData("disk full", "disk full")]
    [InlineData("first\nwhiskers_up 0", "first")]
    [InlineData("first\r\nsecond", "first")]
    [InlineData(null, "")]
    public void A_comment_ends_at_its_first_line_break(string? text, string expected)
        => Assert.Equal(expected, PrometheusText.Comment(text));
}
