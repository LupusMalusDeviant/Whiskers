using System.Text.RegularExpressions;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>The records of one commit, as text: what the base comparison reads (null = the file does not exist there).</summary>
public sealed record GateSnapshot(string? Csproj, string? Invariants, string? ReleaseGate, string? Contract);

/// <summary>
/// Contract 0008, D16 (a) and R18: the records compared with a base — the pull request's base commit, or in a release
/// run the previous release tag. No invariant ID disappears without a retirement, the pinned highest ID never goes
/// down, an accepted §6.1 decision never changes or disappears, and a new decision takes an ID above the base's.
/// </summary>
public static class BaseComparison
{
    private static readonly Regex Tag = new(@"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant);
    private static readonly Regex InvariantId = new(@"^INV-(\d{2,})$", RegexOptions.CultureInvariant);
    private static readonly Regex DecisionNumber = new(@"^RG-([1-9]\d*)$", RegexOptions.CultureInvariant);
    private static readonly Regex RowId = new(@"^\|\s*(INV-\d{2,})\s*\|", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>The invariant IDs of a table's rows, read line by line: a base written before the table's format was
    /// enforced (an unescaped '|' in a cell) still yields its IDs; the format itself is the current gate's R3.</summary>
    private static IEnumerable<string> RowIds(string invariants)
        => RowId.Matches(invariants.Replace("\r\n", "\n")).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    /// <summary>The highest vX.Y.Z tag strictly below <paramref name="version"/>, compared on the three numbers.</summary>
    public static string? PreviousTag(IEnumerable<string> tags, GateVersion version)
        => tags.Select(t => (Tag: t, Version: Tag.IsMatch(t) ? GateVersion.TryParse(t[1..]) : null))
            .Where(t => t.Version is not null && t.Version < version)
            .OrderByDescending(t => t.Version)
            .Select(t => t.Tag)
            .FirstOrDefault();

    public static IReadOnlyList<GateFinding> Compare(GateSnapshot current, GateSnapshot? baseline, string label)
    {
        var findings = new List<GateFinding>();
        void Add(string rule, string subject, string message) => findings.Add(new(rule, subject, message));

        if (baseline is null) { Add("D16", label, "the base cannot be read: no base to compare with is a finding, never a pass"); return findings; }
        GateVersion? baseVersion = null;
        try { baseVersion = baseline.Csproj is null ? null : ReleaseGate.ReadVersion(baseline.Csproj); }
        catch (FormatException ex) { Add("D16", label, $"the base's version cannot be read: {ex.Message}"); }
        if (baseline.Csproj is null) Add("D16", label, "the base has no src/Whiskers/Whiskers.csproj");
        if (baseline.Invariants is null) Add("D16", label, "the base has no docs/v1/SECURITY_INVARIANTS.md");
        if (baseline.Contract is null) Add("D16", label, "the base has no docs/v1/PRODUCT_CONTRACT.md");
        if (baseVersion is null || baseline.Invariants is null || baseline.Contract is null) return findings;
        // Bootstrap: a base below 1.0.0 that predates a table counts as empty for it; from 1.0.0 on a missing table is a finding.
        var bootstrap = baseVersion < GateVersion.One;

        // Invariant IDs and the pin.
        var currentGate = ReleaseGateDocument.Parse(current.ReleaseGate ?? "");
        var currentIds = RowIds(current.Invariants ?? "")
            .Concat(currentGate.Retired.Select(r => r.Id)).ToHashSet(StringComparer.Ordinal);
        var baseIds = RowIds(baseline.Invariants).ToHashSet(StringComparer.Ordinal);
        int? basePin = null;
        if (baseline.ReleaseGate is null || !baseline.ReleaseGate.Contains(ReleaseGateDocument.RetiredHeading, StringComparison.Ordinal)
            || !baseline.ReleaseGate.Contains("Highest invariant ID:", StringComparison.Ordinal))
        {
            if (!bootstrap) Add("D16", label, $"the base ({baseVersion}) has no RELEASE_GATE.md with a pin and '{ReleaseGateDocument.RetiredHeading}'");
        }
        else
        {
            var baseGate = ReleaseGateDocument.Parse(baseline.ReleaseGate);
            baseIds.UnionWith(baseGate.Retired.Select(r => r.Id));
            if (baseGate.Pin is { } p && InvariantId.Match(p) is { Success: true } m) basePin = int.Parse(m.Groups[1].Value);
            else Add("D16", label, "the base's RELEASE_GATE.md has no readable pin");
        }
        foreach (var id in baseIds.Where(id => !currentIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
            Add("D16", id, $"exists in {label} but is neither a row nor retired now: a deleted invariant needs a 'Retired invariants' row and a retirement decision");
        if (basePin is { } bp)
        {
            var now = currentGate.Pin is { } cp && InvariantId.Match(cp) is { Success: true } cm ? int.Parse(cm.Groups[1].Value) : (int?)null;
            if (now is null || now < bp) Add("D16", "RELEASE_GATE.md", $"the pinned highest ID went down from INV-{bp:D2} ({label}) to {currentGate.Pin ?? "none"}");
        }

        // §6.1 decisions (R18).
        var (baseDecisions, baseProblem) = ReleaseDecisions.Parse(baseline.Contract);
        if (baseProblem is not null && !bootstrap) Add("R18", label, $"the base ({baseVersion}): {baseProblem}");
        var (decisions, problem) = ReleaseDecisions.Parse(current.Contract ?? "");
        if (problem is not null) { Add("R18", "PRODUCT_CONTRACT.md §6.1", problem); return findings; }
        var now61 = decisions.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var d in baseDecisions.Where(d => d.Status == "Accepted"))
        {
            if (!now61.TryGetValue(d.Id, out var c)) Add("R18", d.Id, $"was accepted in {label} and is gone: an accepted decision is never deleted");
            else if (c.Cells != d.Cells) Add("R18", d.Id, $"was accepted in {label} as '{d.Cells}' and now reads '{c.Cells}': an accepted decision is never edited; record a new decision with a new ID");
        }
        var baseNumbers = baseDecisions.Select(d => DecisionNumber.Match(d.Id)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var highest = baseNumbers.Count == 0 ? 0 : baseNumbers.Max();
        var baseIdSet = baseDecisions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var d in decisions.Where(d => !baseIdSet.Contains(d.Id)))
            if (DecisionNumber.Match(d.Id) is { Success: true } m && int.Parse(m.Groups[1].Value) <= highest)
                Add("R18", d.Id, $"is new but not above RG-{highest}, the highest ID of {label}: an ID is never reused");
        return findings;
    }
}
