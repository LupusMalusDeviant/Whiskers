using System.Text.RegularExpressions;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>
/// Reads the path matrix of PRODUCT_CONTRACT.md §7 and, for the rows that are not M rows, the capabilities they
/// name in docs/v1/PATH_CAPABILITIES.md (contract 0008, D6 b). An M row names its MCP tool in the second column.
/// </summary>
public static class PathMatrixParser
{
    public const string PathCapabilitiesHeading = "## Rows and capabilities";
    // A row ID is an ASCII class letter, an ASCII '-' and ASCII digits only ('\d' alone would also take e.g. fullwidth digits).
    private static readonly Regex RowId = new(@"^([MASTU])-([0-9]+)$", RegexOptions.CultureInvariant);
    // A near miss of a row ID (review rounds 6 to 9): a first cell that is not exactly a row ID matches when
    //   (a) it contains any numeric character (Unicode category N: ASCII, fullwidth, Arabic-Indic, superscript digits …),
    //       e.g. 'U40', '**U 41**', 'U−42', 'MU-43', 'Ｕ-40', 'U­40', 'U​-40', 'U－40', 'U-o1'; or
    //   (b) anywhere in it, a class letter M, A, S, T or U in any case (also after other letters) is followed by any run,
    //       possibly empty, of whitespace, separators and dashes (see Separator) and then a capital O that no letter
    //       follows ('U-O', 'UO'); or
    //   (c) anywhere in it, a class letter is followed by at least one dash (with any whitespace or separators around it)
    //       and then a digit-like letter: O, o, I, l, or the Cyrillic О, о ('**U-OI**', 'U-lO', 'U-oo'); or
    //   (d) it starts with a class letter and a character that is neither a letter, a digit nor '_' ('U:x').
    // Words with a class letter and a lower-case 'o' but no dash and no digit ('MCP tools', 'Total') do not match.
    // Dashes: ASCII '-', U+00AD soft hyphen, U+2010 to U+2015, U+2212 minus, U+FE58, U+FE63 and U+FF0D. Other separators:
    // U+200B to U+200D and U+2060 (zero-width characters).
    private const string Dash = @"\-­‐-―−﹘﹣－";
    private const string Invisible = @"​-‍⁠";
    private static readonly Regex LooksLikeRowId = new(
        @"\p{N}"
        + $@"|[MASTU][\s{Dash}{Invisible}]*(?-i:O)(?![\p{{L}}])"
        + $@"|[MASTU][\s{Invisible}]*[{Dash}]+[\s{Invisible}]*(?-i:[OoIlОо])"
        + @"|^[MASTU]\W",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex FirstBackticked = new(@"`([^`]+)`", RegexOptions.CultureInvariant);

    public static IReadOnlyList<PathRow> Parse(string contract, string? pathCapabilities)
        => Parse(contract, pathCapabilities, []);

    /// <summary>Same, collecting problems (missing or extra PATH_CAPABILITIES rows, malformed tables).</summary>
    public static IReadOnlyList<PathRow> Parse(string contract, string? pathCapabilities, List<string> problems)
    {
        var lines = MarkdownTables.Lines(contract);
        // A fenced '## 7.' or '## 8.' is an example, not the section's bounds (review round 7 n-1).
        var fenced = MarkdownTables.Fenced(lines);
        var start = Array.FindIndex(lines, l => l.StartsWith("## 7.", StringComparison.Ordinal));
        while (start >= 0 && fenced[start]) start = Array.FindIndex(lines, start + 1, l => l.StartsWith("## 7.", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, Math.Max(start, 0), l => l.StartsWith("## 8.", StringComparison.Ordinal));
        while (end >= 0 && fenced[end]) end = Array.FindIndex(lines, end + 1, l => l.StartsWith("## 8.", StringComparison.Ordinal));
        if (start < 0 || end < 0) { problems.Add("PRODUCT_CONTRACT.md has no §7 path matrix (## 7. … ## 8.)"); return []; }

        var mapping = new Dictionary<string, (int Line, string Cell)>(StringComparer.Ordinal);
        if (pathCapabilities is null) problems.Add("docs/v1/PATH_CAPABILITIES.md is missing (D6 b)");
        else
        {
            try
            {
                foreach (var (line, cells) in MarkdownTables.After(pathCapabilities, PathCapabilitiesHeading))
                    if (!mapping.TryAdd(cells[0], (line, cells[1]))) problems.Add($"PATH_CAPABILITIES.md:{line}: row {cells[0]} is listed twice");
            }
            catch (FormatException e) { problems.Add($"PATH_CAPABILITIES.md: {e.Message}"); }
        }

        // Every '|' line outside a code fence, indented or not, with or without a space after the bar (review round 6 m-2):
        // a row the reader drops would be missing from R14/R15 and the inventory while the gate stays green. A first cell
        // that names a row ID but is not exactly one ('**U-40**', 'U-40a') is a problem, never a skipped line.
        var rows = new List<PathRow>();
        for (var i = start; i < end; i++)
        {
            if (fenced[i] || !lines[i].TrimStart().StartsWith('|')) continue;
            var cells = MarkdownTables.Split(lines[i]);
            var id = RowId.Match(cells[0]);
            if (!id.Success)
            {
                if (LooksLikeRowId.IsMatch(cells[0]))
                    problems.Add($"PRODUCT_CONTRACT.md:{i + 1}: '{cells[0]}' looks like a §7 row ID but is not one (write it as e.g. 'U-40')");
                continue;
            }
            var cls = id.Groups[1].Value;
            if (cls == "M")
            {
                var tool = FirstBackticked.Match(cells.Length > 1 ? cells[1] : "");
                rows.Add(new PathRow(cells[0], cls, tool.Success ? tool.Groups[1].Value : null, cells.Length > 2 ? cells[2] : "", [], null, i + 1));
                continue;
            }
            if (!mapping.Remove(cells[0], out var mapped))
            {
                if (pathCapabilities is not null) problems.Add($"PATH_CAPABILITIES.md has no line for §7 row {cells[0]} (PRODUCT_CONTRACT.md:{i + 1})");
                rows.Add(new PathRow(cells[0], cls, null, "", [], null, i + 1));
                continue;
            }
            var reason = ReleaseGateDocument.Dash(mapped.Cell) is null ? mapped.Cell : null;
            var caps = reason is not null ? [] : mapped.Cell.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.Trim('`')).ToList();
            if (reason is not null && reason.Trim('—', '-', ' ').Length == 0)
                problems.Add($"PATH_CAPABILITIES.md:{mapped.Line}: row {cells[0]} maps to no capability and says no reason");
            rows.Add(new PathRow(cells[0], cls, null, "", caps, reason, i + 1));
        }
        foreach (var (extra, at) in mapping)
            problems.Add($"PATH_CAPABILITIES.md:{at.Line}: row {extra} is not a row of §7");
        return rows;
    }

    public const string WithoutPathHeading = "## Capabilities without a path";

    /// <summary>The capabilities PATH_CAPABILITIES.md lists as performed by no path today, with the reason.</summary>
    public static IReadOnlyDictionary<string, string> WithoutPath(string? pathCapabilities, List<string> problems)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pathCapabilities is null) return result;
        try
        {
            foreach (var (line, cells) in MarkdownTables.After(pathCapabilities, WithoutPathHeading))
            {
                if (cells[1].Trim().Length == 0) problems.Add($"PATH_CAPABILITIES.md:{line}: '{cells[0]}' says no reason");
                if (!result.TryAdd(cells[0].Trim('`'), cells[1])) problems.Add($"PATH_CAPABILITIES.md:{line}: '{cells[0]}' is listed twice");
            }
        }
        catch (FormatException e) { problems.Add($"PATH_CAPABILITIES.md: {e.Message}"); }
        return result;
    }

    /// <summary>§7.6 "Rows today": class letter → count, plus the total (key "Total").</summary>
    public static IReadOnlyDictionary<string, int>? Totals(string contract, List<string> problems)
    {
        var lines = MarkdownTables.Lines(contract);
        var fenced = MarkdownTables.Fenced(lines); // fenced lines are neither the marker nor the table (review round 5 m-3)
        var at = Array.FindIndex(lines, l => l.StartsWith("**Rows today", StringComparison.Ordinal));
        while (at >= 0 && fenced[at]) at = Array.FindIndex(lines, at + 1, l => l.StartsWith("**Rows today", StringComparison.Ordinal));
        if (at < 0) { problems.Add("PRODUCT_CONTRACT.md §7.6 has no '**Rows today' table"); return null; }
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var start = at + 1;
        while (start < lines.Length && (fenced[start] || !lines[start].TrimStart().StartsWith('|'))) start++;
        foreach (var line in lines.Skip(start).TakeWhile(l => l.TrimStart().StartsWith('|')))
        {
            var cells = MarkdownTables.Split(line);
            if (cells.Length != 2) continue;
            var count = Regex.Match(cells[1], @"^\**(\d+)\**$");
            if (!count.Success) continue;
            var cls = Regex.Match(cells[0], @"\(([MASTU])\)");
            var key = cls.Success ? cls.Groups[1].Value : cells[0].Contains("Total", StringComparison.Ordinal) ? "Total" : null;
            // A class or the total counted twice is a problem, as for §11 (review round 6 NIT a), never the last one wins.
            if (key is not null && !result.TryAdd(key, int.Parse(count.Groups[1].Value)))
                problems.Add($"PRODUCT_CONTRACT.md §7.6: '{key}' is counted twice");
        }
        return result;
    }
}
