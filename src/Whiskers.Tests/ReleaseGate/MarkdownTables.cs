using System.Text;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>
/// One row reader for every document the gate reads. A table is a header row, an optional separator row
/// (<c>|---|</c>) and data rows, each a line that starts with <c>|</c>. Cells are split on unescaped <c>|</c>
/// (<c>\|</c> stays in the cell, as <c>|</c>). A row whose cell count differs from the header's is malformed.
/// </summary>
public static class MarkdownTables
{
    /// <summary>The data rows of the first table after the line <paramref name="heading"/> (exact, trimmed).</summary>
    /// <exception cref="FormatException">The heading is missing, no table follows it, or a row is malformed.</exception>
    public static IReadOnlyList<(int Line, string[] Cells)> After(string markdown, string heading)
    {
        var lines = Lines(markdown);
        var at = Array.FindIndex(lines, l => l.Trim() == heading);
        if (at < 0) throw new FormatException($"the heading '{heading}' is missing");
        if (Array.FindIndex(lines, at + 1, l => l.Trim() == heading) >= 0)
            throw new FormatException($"the heading '{heading}' appears twice");
        var start = at + 1;
        while (start < lines.Length && !lines[start].TrimStart().StartsWith('|'))
        {
            if (lines[start].TrimStart().StartsWith('#'))
                throw new FormatException($"no table follows the heading '{heading}'");
            start++;
        }
        if (start >= lines.Length) throw new FormatException($"no table follows the heading '{heading}'");
        return Read(lines, start, heading);
    }

    /// <summary>Every table in <paramref name="markdown"/> whose header cells equal <paramref name="header"/>.</summary>
    public static IReadOnlyList<(int Line, string[] Cells)> WithHeader(string markdown, params string[] header)
    {
        var lines = Lines(markdown);
        var rows = new List<(int, string[])>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith('|')) continue;
            if (i > 0 && lines[i - 1].TrimStart().StartsWith('|')) continue; // not the first row of a table
            if (!Split(lines[i]).SequenceEqual(header)) continue;
            rows.AddRange(Read(lines, i, string.Join(" | ", header)));
        }
        return rows;
    }

    private static List<(int Line, string[] Cells)> Read(string[] lines, int headerIndex, string what)
    {
        var header = Split(lines[headerIndex]);
        var rows = new List<(int, string[])>();
        for (var i = headerIndex + 1; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
        {
            var cells = Split(lines[i]);
            if (i == headerIndex + 1 && cells.All(c => c.Length > 0 && c.Trim(':', '-').Length == 0)) continue; // separator
            if (cells.Length != header.Length)
                throw new FormatException($"line {i + 1}: the row under '{what}' has {cells.Length} cells, the table has {header.Length}");
            rows.Add((i + 1, cells));
        }
        // A blank line ends a Markdown table: rows after it would be dropped silently (review NIT). A '|' line right after
        // the blank is a finding unless it starts a new table (a header followed by its separator).
        var end = headerIndex + 1;
        while (end < lines.Length && lines[end].TrimStart().StartsWith('|')) end++;
        if (end + 1 < lines.Length && lines[end].Trim().Length == 0 && lines[end + 1].TrimStart().StartsWith('|'))
        {
            var next = end + 2 < lines.Length ? Split(lines[end + 2]) : [];
            var startsTable = next.Length > 0 && next.All(c => c.Length > 0 && c.Trim(':', '-').Length == 0);
            if (!startsTable)
                throw new FormatException($"line {end + 2}: a table row after a blank line under '{what}' — the table ended at line {end}, so the row would be dropped");
        }
        return rows;
    }

    /// <summary>The cells of one table line, trimmed, with <c>\|</c> turned back into <c>|</c>.</summary>
    public static string[] Split(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '|') { cell.Append('|'); i++; continue; }
            if (text[i] == '|') { cells.Add(cell.ToString().Trim()); cell.Clear(); continue; }
            cell.Append(text[i]);
        }
        cells.Add(cell.ToString().Trim());
        return cells.ToArray();
    }

    public static string[] Lines(string markdown) => markdown.Replace("\r\n", "\n").Split('\n');
}
