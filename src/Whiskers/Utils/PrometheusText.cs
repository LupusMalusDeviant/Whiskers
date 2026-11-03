namespace Whiskers.Utils;

/// <summary>
/// R1-41 (A-16): text for the Prometheus exposition format. A label value is escaped as the format requires — backslash,
/// double quote and line feed — so a container, image or server name that carries one cannot end the label, start a new
/// sample or forge a series. A comment line is cut at its first line break.
/// </summary>
public static class PrometheusText
{
    /// <summary>The value of a label, escaped; <c>null</c> is the empty value.</summary>
    public static string Label(string? value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>Text for a <c>#</c> comment line: everything after the first line break would be read as samples.</summary>
    public static string Comment(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}
