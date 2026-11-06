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
        return Read(lines, start, heading, repeatAllowed: false);
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
            rows.AddRange(Read(lines, i, string.Join(" | ", header), repeatAllowed: true));
        }
        return rows;
    }

    /// <param name="repeatAllowed">Whether a second table with the same header may follow under the same heading (it is
    /// then read by its own call, as <see cref="WithHeader"/> does); for <see cref="After"/> it would be dropped, so it is
    /// a problem there.</param>
    private static List<(int Line, string[] Cells)> Read(string[] lines, int headerIndex, string what, bool repeatAllowed)
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
        // A table ends at its first non-'|' line: rows after that would be dropped silently (review round 1 NIT, round 2
        // m-C). Every '|' line from there up to the next heading is a problem, unless it belongs to another table with the
        // same header (header row, then its separator) where such a repeat is read on its own.
        var i2 = headerIndex + 1;
        while (i2 < lines.Length && lines[i2].TrimStart().StartsWith('|')) i2++;
        var tableEnd = i2;
        for (; i2 < lines.Length && !lines[i2].TrimStart().StartsWith('#'); i2++)
        {
            if (!lines[i2].TrimStart().StartsWith('|')) continue;
            var repeat = repeatAllowed && Split(lines[i2]).SequenceEqual(header)
                && i2 + 1 < lines.Length && IsSeparator(Split(lines[i2 + 1]));
            if (!repeat)
                throw new FormatException($"line {i2 + 1}: a table row after the end of the table under '{what}' (it ended at line {tableEnd}) would be dropped");
            while (i2 + 1 < lines.Length && lines[i2 + 1].TrimStart().StartsWith('|')) i2++; // skip that table
        }
        return rows;
    }

    private static bool IsSeparator(string[] cells) => cells.All(c => c.Length > 0 && c.Trim(':', '-').Length == 0);

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
