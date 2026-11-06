using System.Text.RegularExpressions;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>
/// Reads the invariant table of docs/v1/SECURITY_INVARIANTS.md: every table with the header
/// <c>| ID | Invariant | Status | Evidence / gap | Test | WP |</c>, one row per invariant.
/// </summary>
public static class InvariantTableParser
{
    public static readonly string[] Header = ["ID", "Invariant", "Status", "Evidence / gap", "Test", "WP"];

    private static readonly Regex Backticked = new(@"`([^`]*)`", RegexOptions.CultureInvariant);
    private static readonly Regex PlannedMarker = new(@"\*\(planned\b[^)]*\)\*", RegexOptions.CultureInvariant);

    public static IReadOnlyList<InvariantRow> Parse(string markdown)
        => MarkdownTables.WithHeader(markdown, Header)
            .Select(r => new InvariantRow(r.Cells[0], ParseStatus(r.Cells[2]), r.Cells[2], ParseTests(r.Cells[4]), r.Cells[5], r.Line))
            .ToList();

    /// <summary>The status of a cell that holds exactly one bare symbol, else null (R3).</summary>
    public static InvariantStatus? ParseStatus(string cell) => cell.Trim() switch
    {
        "✅" => InvariantStatus.Holds,
        "⚠️" or "⚠" => InvariantStatus.Partial,
        "❌" => InvariantStatus.Violated,
        "❓" => InvariantStatus.Unverified,
        _ => null,
    };

    public static string Symbol(InvariantStatus status) => status switch
    {
        InvariantStatus.Holds => "✅",
        InvariantStatus.Partial => "⚠️",
        InvariantStatus.Violated => "❌",
        _ => "❓",
    };

    /// <summary>
    /// The test references of a test cell. Backticks hold test names only (`Class` or `Class.Method`); a
    /// <c>*(planned, …)*</c> marker right after a name (only blanks between) makes that name planned; a marker after
    /// prose is a planned entry without a name.
    /// </summary>
    public static IReadOnlyList<TestReference> ParseTests(string cell)
    {
        var refs = new List<TestReference>();
        var names = Backticked.Matches(cell).ToList();
        var plannedAfter = new HashSet<int>();
        foreach (Match marker in PlannedMarker.Matches(cell))
        {
            var owner = names.LastOrDefault(n => n.Index + n.Length <= marker.Index
                                                 && cell[(n.Index + n.Length)..marker.Index].Trim().Length == 0);
            if (owner is not null) plannedAfter.Add(owner.Index);
            else refs.Add(new TestReference(null, null, Planned: true, marker.Value));
        }
        foreach (var n in names)
        {
            var raw = n.Groups[1].Value;
            var dot = raw.LastIndexOf('.');
            var (cls, method) = dot < 0 ? (raw, (string?)null) : (raw[..dot], raw[(dot + 1)..]);
            refs.Add(new TestReference(cls, method, plannedAfter.Contains(n.Index), raw));
        }
        return refs;
    }
}
