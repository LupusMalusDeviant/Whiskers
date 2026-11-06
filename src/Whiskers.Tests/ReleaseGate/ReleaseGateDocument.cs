using System.Globalization;
using System.Text.RegularExpressions;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>Reads docs/v1/RELEASE_GATE.md: the pinned highest invariant ID and six tables (contract 0008).</summary>
public static class ReleaseGateDocument
{
    public const string OpenHeading = "## Open invariants";
    public const string RetiredHeading = "## Retired invariants";
    public const string KnownHeading = "## Known violations";
    public const string ExceptionsHeading = "## Accepted exceptions";
    public const string PlatformSkipsHeading = "## Platform skips";
    public const string SuitesHeading = "## Mandatory suites";

    private static readonly Regex PinLine = new(@"^Highest invariant ID: (\S+)\s*$", RegexOptions.CultureInvariant);

    public static ReleaseGateTables Parse(string markdown)
    {
        var problems = new List<string>();
        var lines = MarkdownTables.Lines(markdown);
        var pins = lines.Select((l, i) => (Match: PinLine.Match(l), Line: i + 1)).Where(p => p.Match.Success).ToList();
        if (pins.Count != 1)
            problems.Add(pins.Count == 0 ? "RELEASE_GATE.md has no line 'Highest invariant ID: INV-nn'"
                                         : $"RELEASE_GATE.md pins the highest invariant ID {pins.Count} times (lines {string.Join(", ", pins.Select(p => p.Line))})");

        IReadOnlyList<(int Line, string[] Cells)> Table(string heading, int columns)
        {
            try
            {
                var rows = MarkdownTables.After(markdown, heading);
                // The header itself must have the table's column count: read it again through the separator-free path.
                var headerLine = MarkdownTables.Lines(markdown).SkipWhile(l => l.Trim() != heading).Skip(1)
                    .FirstOrDefault(l => l.TrimStart().StartsWith('|'));
                if (headerLine is not null && MarkdownTables.Split(headerLine).Length != columns)
                    problems.Add($"RELEASE_GATE.md '{heading}' must have {columns} columns");
                return rows;
            }
            catch (FormatException e)
            {
                problems.Add($"RELEASE_GATE.md: {e.Message}");
                return [];
            }
        }

        return new ReleaseGateTables
        {
            Pin = pins.Count == 1 ? pins[0].Match.Groups[1].Value : null,
            PinLine = pins.Count == 1 ? pins[0].Line : 0,
            Open = Table(OpenHeading, 5).Select(r => new OpenInvariant(r.Cells[0], InvariantTableParser.ParseStatus(r.Cells[1]),
                r.Cells[2], r.Cells[3], Dash(r.Cells[4]), r.Line)).ToList(),
            Retired = Table(RetiredHeading, 3).Select(r => new RetiredInvariant(r.Cells[0], r.Cells[1], Date(r.Cells[2]), r.Line)).ToList(),
            Known = Table(KnownHeading, 7).Select(r => new KnownViolation(r.Cells[0], InvariantTableParser.ParseStatus(r.Cells[1]),
                r.Cells[2], r.Cells[3], r.Cells[4], r.Cells[5], r.Cells[6], r.Line)).ToList(),
            Exceptions = Table(ExceptionsHeading, 5).Select(r => new AcceptedException(r.Cells[0], InvariantTableParser.ParseStatus(r.Cells[1]),
                r.Cells[2], r.Cells[3], r.Cells[4], r.Line)).ToList(),
            PlatformSkips = Table(PlatformSkipsHeading, 4).Select(r => new PlatformSkip(r.Cells[0], r.Cells[1], Dash(r.Cells[2]),
                r.Cells[3], r.Line)).ToList(),
            Suites = Table(SuitesHeading, 4).Select(r => new MandatorySuite(r.Cells[0], r.Cells[1], Dash(r.Cells[2]), r.Cells[3], r.Line)).ToList(),
            Problems = problems,
        };
    }

    /// <summary>Null for an empty cell or one that starts with a dash (— or -), else the cell.</summary>
    public static string? Dash(string cell)
        => cell.Length == 0 || cell.StartsWith('—') || cell == "-" || cell.StartsWith("- ", StringComparison.Ordinal) ? null : cell;

    public static DateOnly? Date(string cell)
        => DateOnly.TryParseExact(cell.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>Reads PRODUCT_CONTRACT.md §6.1 "Release decisions" (D5).</summary>
public static class ReleaseDecisions
{
    public const string Heading = "### 6.1 Release decisions";
    public static readonly string[] Header = ["ID", "Kind", "Invariant", "Decision", "Release", "Status", "Date", "Decided by"];

    /// <summary>The decisions, or a problem when the table is missing or malformed.</summary>
    public static (IReadOnlyList<ReleaseDecision> Decisions, string? Problem) Parse(string contract)
    {
        try
        {
            var lines = MarkdownTables.Lines(contract);
            var at = Array.FindIndex(lines, l => l.Trim() == Heading);
            if (at < 0) return ([], $"PRODUCT_CONTRACT.md has no '{Heading}' table");
            var headerLine = lines.Skip(at + 1).TakeWhile(l => !l.TrimStart().StartsWith('#')).FirstOrDefault(l => l.TrimStart().StartsWith('|'));
            if (headerLine is null || !MarkdownTables.Split(headerLine).SequenceEqual(Header))
                return ([], $"PRODUCT_CONTRACT.md '{Heading}' must have the columns | {string.Join(" | ", Header)} |");
            var rows = MarkdownTables.After(contract, Heading);
            return (rows.Select(r => new ReleaseDecision(r.Cells[0], r.Cells[1], r.Cells[2], ReleaseGateDocument.Dash(r.Cells[4]),
                r.Cells[5], ReleaseGateDocument.Date(r.Cells[6]), r.Cells[7], r.Line) { Cells = string.Join(" | ", r.Cells) }).ToList(), null);
        }
        catch (FormatException e)
        {
            return ([], $"PRODUCT_CONTRACT.md: {e.Message}");
        }
    }
}
