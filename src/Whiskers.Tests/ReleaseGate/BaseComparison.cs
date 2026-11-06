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
    // Indented rows too (up to any whitespace), as the table reader and GitHub accept them (review round 5 m-2).
    private static readonly Regex RowId = new(@"^[ \t]*\|\s*(INV-\d{2,})\s*\|", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>The invariant IDs of a table's rows, read line by line: a base written before the table's format was
    /// enforced (an unescaped '|' in a cell) still yields its IDs; the format itself is the current gate's R3.</summary>
    private static IEnumerable<string> RowIds(string invariants)
        => RowId.Matches(invariants.Replace("\r\n", "\n")).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    /// <summary>The base a release run chose, and why it cannot be used (empty when it can).</summary>
    public sealed record BaseChoice(string? Tag, IReadOnlyList<string> Problems);

    /// <summary>
    /// The previous release a release run compares with (Amendment 2; review round 1 M3, round 2 m-B and m-D). The
    /// guarantee is the ancestry check (<paramref name="isAncestor"/>: the tag's commit is in the release commit's history)
    /// together with the version match <see cref="Compare"/> makes (the tag's commit carries the tag's version). The list
    /// of published GitHub releases (no drafts, no pre-releases) only screens out tags whose release run failed; anyone who
    /// can push a tag can also publish a release by hand. A tag between the chosen base and the version that is in the
    /// history and carries its own version but has no published release is a finding: the base would otherwise fall back
    /// past it (a deleted or demoted release), and a release forged below it could not pick an older base unseen.
    /// </summary>
    /// <param name="published">The published releases, or null when the list is missing (a finding).</param>
    /// <param name="versionAt">The version in the csproj of a tag's commit, or null when the commit has no csproj. It throws
    /// <see cref="FormatException"/> when the csproj exists but its version cannot be read: that is a problem, never
    /// "not a release commit" (review round 3, m-1).</param>
    public static BaseChoice ChooseBase(IEnumerable<string> tags, IEnumerable<string>? published, Func<string, bool> isAncestor,
        Func<string, GateVersion?> versionAt, GateVersion version)
    {
        if (published is null)
            return new(null, ["there is no list of published releases: without it a tag whose release failed could be the base"]);
        var releases = published.Select(p => p.Trim()).ToHashSet(StringComparer.Ordinal);
        var all = tags.Select(t => t.Trim()).Where(t => Tag.IsMatch(t)).ToList();
        var chosen = PreviousTag(all.Where(releases.Contains), version);
        if (chosen is null)
            return new(null, [$"no published release below {version} was found: a missing base is a finding, never a pass (were the tags fetched?)"]);

        var problems = new List<string>();
        if (!isAncestor(chosen))
            problems.Add($"the previous release {chosen} is not an ancestor of the release commit: not a base (was the history fetched?)");
        var chosenVersion = GateVersion.TryParse(chosen[1..])!;
        foreach (var t in all.Where(t => !releases.Contains(t)).OrderBy(t => t, StringComparer.Ordinal))
        {
            var v = GateVersion.TryParse(t[1..])!;
            if (!(v > chosenVersion && v < version && isAncestor(t))) continue;
            GateVersion? own;
            try { own = versionAt(t); }
            catch (FormatException ex)
            {
                problems.Add($"{t} is in the release commit's history above {chosen}, but the version in its csproj cannot be read "
                             + $"({ex.Message}): what the gate cannot read is a finding");
                continue;
            }
            if (own == v)
                problems.Add($"{t} is a release commit in the release commit's history (its csproj says {v}) without a published "
                             + $"release: the base would fall back past it to {chosen}; publish that release or delete the tag");
        }
        return new(chosen, problems);
    }

    /// <summary>The highest vX.Y.Z tag strictly below <paramref name="version"/>, compared on the three numbers.</summary>
    public static string? PreviousTag(IEnumerable<string> tags, GateVersion version)
        => tags.Select(t => (Tag: t, Version: Tag.IsMatch(t) ? GateVersion.TryParse(t[1..]) : null))
            .Where(t => t.Version is not null && t.Version < version)
            .OrderByDescending(t => t.Version)
            .Select(t => t.Tag)
            .FirstOrDefault();

    /// <summary>
    /// Compares the current records with a base. <paramref name="tag"/> is the release tag the base was chosen by
    /// (previous-tag mode), or null for a pull request's base commit: a tag whose commit does not carry the tag's version
    /// in its csproj is not a release commit (every real release passed <c>prepare</c>'s version check), so it is refused.
    /// </summary>
    public static IReadOnlyList<GateFinding> Compare(GateSnapshot current, GateSnapshot? baseline, string label, string? tag = null)
    {
        var findings = new List<GateFinding>();
        void Add(string rule, string subject, string message) => findings.Add(new(rule, subject, message));

        if (baseline is null) { Add("D16", label, "the base cannot be read: no base to compare with is a finding, never a pass"); return findings; }

        // The base's version first: it decides whether a missing file is a finding (Amendment 1).
        if (baseline.Csproj is null) { Add("D16", label, "the base has no src/Whiskers/Whiskers.csproj"); return findings; }
        GateVersion baseVersion;
        try { baseVersion = ReleaseGate.ReadVersion(baseline.Csproj); }
        catch (FormatException ex) { Add("D16", label, $"the base's version cannot be read: {ex.Message}"); return findings; }
        if (tag is not null && tag != $"v{baseVersion}")
        {
            Add("D16", label, $"the tag {tag} points at a commit whose csproj says {baseVersion}: not a release commit, so not a base");
            return findings;
        }
        // The version never goes down against the base (D2): a lower version would drop the gate's mode.
        try
        {
            if (current.Csproj is null) Add("D2", "Whiskers.csproj", "the current commit has no src/Whiskers/Whiskers.csproj: its version cannot be compared with the base");
            else if (ReleaseGate.ReadVersion(current.Csproj) is { } now && now < baseVersion)
                Add("D2", "Whiskers.csproj", $"the version {now} is below {baseVersion} of {label}: the version (and with it the gate's mode) never goes down");
        }
        catch (FormatException ex) { Add("D2", "Whiskers.csproj", $"the current version cannot be read: {ex.Message}"); }

        // Bootstrap (Amendment 1): a base below 1.0.0 that lacks a file or table counts as empty for it; from 1.0.0 on
        // a missing file or table is a finding.
        var bootstrap = baseVersion < GateVersion.One;
        if (!bootstrap)
        {
            if (baseline.Invariants is null) Add("D16", label, "the base has no docs/v1/SECURITY_INVARIANTS.md");
            if (baseline.Contract is null) Add("D16", label, "the base has no docs/v1/PRODUCT_CONTRACT.md");
            if (baseline.Invariants is null || baseline.Contract is null) return findings;
        }

        // Invariant IDs (line by line: an older base may not satisfy today's format) and the pin.
        var currentGate = ReleaseGateDocument.Parse(current.ReleaseGate ?? "");
        var currentIds = RowIds(current.Invariants ?? "")
            .Concat(currentGate.Retired.Select(r => r.Id)).ToHashSet(StringComparer.Ordinal);
        var baseIds = RowIds(baseline.Invariants ?? "").ToHashSet(StringComparer.Ordinal);
        int? basePin = null;
        var gateAbsent = baseline.ReleaseGate is null
            || (!baseline.ReleaseGate.Contains(ReleaseGateDocument.RetiredHeading, StringComparison.Ordinal)
                && !baseline.ReleaseGate.Contains("Highest invariant ID:", StringComparison.Ordinal));
        if (gateAbsent)
        {
            if (!bootstrap) Add("D16", label, $"the base ({baseVersion}) has no RELEASE_GATE.md with a pin and '{ReleaseGateDocument.RetiredHeading}'");
        }
        else
        {
            // Present in the base: read strictly. A malformed table would hide retired IDs, so its problems are findings.
            var baseGate = ReleaseGateDocument.Parse(baseline.ReleaseGate!);
            foreach (var gateProblem in baseGate.Problems) Add("D16", label, $"the base's RELEASE_GATE.md: {gateProblem}");
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
        // Absent from a 0.x base: empty. Present (or a base at 1.0.0 or above): read strictly, a problem is a finding.
        var baseContract = baseline.Contract ?? "";
        var decisionsAbsent = !baseContract.Replace("\r\n", "\n").Split('\n').Any(l => l.Trim() == ReleaseDecisions.Heading);
        var (baseDecisions, baseProblem) = ReleaseDecisions.Parse(baseContract);
        if (baseProblem is not null && !(bootstrap && decisionsAbsent)) Add("R18", label, $"the base ({baseVersion}): {baseProblem}");
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
