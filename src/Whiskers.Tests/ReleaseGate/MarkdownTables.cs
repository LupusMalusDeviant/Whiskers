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
        // The first '|' line after the heading outside code fences: a fenced example table is not the table (review round 4
        // NIT 3), and a fenced shell comment is no heading.
        var fenced = Fenced(lines);
        var start = at + 1;
        while (start < lines.Length && (fenced[start] || !lines[start].TrimStart().StartsWith('|')))
        {
            if (!fenced[start] && IsHeading(lines[start].TrimStart()))
                throw new FormatException($"no table follows the heading '{heading}'");
            start++;
        }
        if (start >= lines.Length) throw new FormatException($"no table follows the heading '{heading}'");
        return Read(lines, start, heading, repeatAllowed: false);
    }

    /// <summary>Every table in <paramref name="markdown"/> whose header cells equal <paramref name="header"/>.</summary>
    /// <remarks>A table under its own heading with a different (or no) header row is not seen at all — rows written there
    /// are not read. For the invariant tables R3 guards this: every line that looks like an invariant row
    /// (<c>| INV-nn |</c>) but is not a parsed row is a finding (review round 4 m-1). A new caller needs its own guard.</remarks>
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
        // Up to the next heading ('#' to '######' and a space), outside code fences: a shell comment in a fenced block is
        // no heading, and a '|' line in a fence is no table row (review round 3 NIT).
        var fenced = Fenced(lines);
        for (; i2 < lines.Length; i2++)
        {
            var trimmed = lines[i2].TrimStart();
            if (fenced[i2]) continue;
            if (IsHeading(trimmed)) break;
            if (!trimmed.StartsWith('|')) continue;
            var repeat = repeatAllowed && Split(lines[i2]).SequenceEqual(header)
                && i2 + 1 < lines.Length && IsSeparator(Split(lines[i2 + 1]));
            if (!repeat)
                throw new FormatException($"line {i2 + 1}: a table row after the end of the table under '{what}' (it ended at line {tableEnd}) would be dropped");
            while (i2 + 1 < lines.Length && lines[i2 + 1].TrimStart().StartsWith('|')) i2++; // skip that table
        }
        return rows;
    }

    /// <summary>
    /// Which lines are code-fence markers or inside a fence (CommonMark): a fence opens with three or more backticks or
    /// tildes and closes only with the same character, at least as many of them, and nothing else on the line (review
    /// round 4 NIT 1: a '~~~' line inside a '```' block does not close it). An unclosed fence runs to the end.
    /// </summary>
    public static bool[] Fenced(string[] lines)
    {
        var fenced = new bool[lines.Length];
        char? marker = null;
        var length = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            var run = trimmed.Length > 0 && trimmed[0] is '`' or '~' ? trimmed.TakeWhile(c => c == trimmed[0]).Count() : 0;
            if (marker is null)
            {
                if (run >= 3) { marker = trimmed[0]; length = run; fenced[i] = true; }
                continue;
            }
            fenced[i] = true;
            if (run >= length && trimmed[0] == marker && trimmed[run..].Trim().Length == 0) marker = null;
        }
        return fenced;
    }

    private static bool IsHeading(string trimmed) => System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^#{1,6} ");

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
