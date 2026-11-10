using System.Text.RegularExpressions;
using Whiskers.Tests.ReleaseGate;

namespace Whiskers.Tests.Claims;

/// <summary>
/// Contract 0011, Part B (INV-56): the governed documents against the claim register <c>docs/v1/CLAIMS.md</c>. Every
/// claim-shaped block carries a marker; every marked block's fingerprint equals its register row (claim-free blocks
/// included, Q2), so an edited sentence fails until the row is re-confirmed; every claim id has exactly one row and one
/// marker; every quote is in its block; every claim names tests that exist, run and are required by the gate (R8); a
/// quoted readiness line is the English title of a line. On a difference the fresh register is written beside it as
/// <c>CLAIMS.md.actual</c>.
/// </summary>
public class ClaimRegisterTests
{
    private static string Read(string relative) => GateFiles.Require(relative);

    /// <summary>Every block of every governed file, in order.</summary>
    internal static IReadOnlyList<(string File, ClaimBlock Block)> Blocks()
        => ClaimRules.GovernedFiles.SelectMany(g => ClaimBlockParser.Parse(Read(g.File)).Select(b => (g.File, b))).ToList();

    private static ClaimRegister.Contents Register() => ClaimRegister.Parse(Read(ClaimRules.Register));

    private static string Where((string File, ClaimBlock Block) b) => $"{b.File}:{b.Block.Line} \"{b.Block.StartsWith}…\"";

    /// <summary>Writes the fresh register beside the real one when <paramref name="problems"/> is not empty.</summary>
    private static void FailWithActual(List<string> problems)
    {
        if (problems.Count == 0) return;
        var actual = ClaimRegister.Render(Read(ClaimRules.Register), Blocks());
        File.WriteAllText(GateFiles.Full(ClaimRules.Register) + ".actual", actual);
        Assert.Fail($"{problems.Count} problem(s); the fresh register is written to {ClaimRules.Register}.actual:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Every_claim_shaped_block_of_the_governed_files_carries_a_marker()
    {
        var unmarked = Blocks().Where(b => b.Block.IsClaimShaped && !b.Block.IsMarked).Select(b => "no marker: " + Where(b)).ToList();
        FailWithActual(unmarked);
    }

    [Fact]
    public void Every_marker_is_well_formed()
    {
        var problems = new List<string>();
        foreach (var b in Blocks().Where(b => b.Block.IsMarked))
        {
            if (b.Block.Markers > 1) problems.Add($"{Where(b)}: {b.Block.Markers} markers; one block carries one marker");
            if (b.Block.ClaimIds.Count > 0 && b.Block.FreeReason is not null) problems.Add($"{Where(b)}: claim ids and a claim-free reason");
            if (b.Block.FreeReason is { } reason && !ClaimRules.Reasons.Contains(reason))
                problems.Add($"{Where(b)}: '{reason}' is not one of {string.Join(", ", ClaimRules.Reasons)}");
            if (b.Block.Markers > 0 && b.Block.ClaimIds.Count == 0 && b.Block.FreeReason is null) problems.Add($"{Where(b)}: an empty marker");
            foreach (var id in b.Block.ClaimIds)
            {
                var m = ClaimRules.ClaimId.Match(id);
                if (!m.Success) problems.Add($"{Where(b)}: '{id}' is not a claim id RM-nn, SEC-nn, SA-nn or PC-nn");
                else if (m.Groups["prefix"].Value != ClaimRules.PrefixOf(b.File)) problems.Add($"{Where(b)}: {id} belongs to another file (prefix {ClaimRules.PrefixOf(b.File)})");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_claim_id_has_one_register_row_and_one_marker_and_the_same_fingerprint()
    {
        var blocks = Blocks();
        var register = Register();
        var problems = new List<string>();
        var marked = blocks.SelectMany(b => b.Block.ClaimIds.Select(id => (Id: id, B: b))).ToList();
        foreach (var dup in marked.GroupBy(x => x.Id).Where(g => g.Count() > 1))
            problems.Add($"{dup.Key} is marked {dup.Count()} times ({string.Join(", ", dup.Select(x => Where(x.B)))})");
        foreach (var dup in register.Claims.GroupBy(r => r.Id).Where(g => g.Count() > 1))
            problems.Add($"{dup.Key} has {dup.Count()} register rows");
        var retired = register.Retired.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, b) in marked)
        {
            if (retired.Contains(id)) { problems.Add($"{id} is retired and must not be reused ({Where(b)})"); continue; }
            var row = register.Claims.FirstOrDefault(r => r.Id == id);
            if (row is null) { problems.Add($"{id} ({Where(b)}) has no register row"); continue; }
            if (row.File != b.File) problems.Add($"{id}: the register says {row.File}, the marker is in {b.File}");
            if (row.Block != b.Block.Fingerprint) problems.Add($"{id}: the block changed ({Where(b)}): fingerprint {b.Block.Fingerprint}, the register says {row.Block} — re-confirm the row");
        }
        foreach (var row in register.Claims.Where(r => marked.All(m => m.Id != r.Id)))
            problems.Add($"{row.Id} (CLAIMS.md:{row.Line}) has no marker in {row.File}: an orphan row (retire it instead of deleting)");
        FailWithActual(problems);
    }

    [Fact]
    public void Every_claim_free_block_matches_its_register_row()
    {
        // Q2 (a): a claim added to an "advice" paragraph changes its fingerprint and fails until the row is re-confirmed.
        var expected = Blocks().Where(b => b.Block.FreeReason is not null)
            .Select(b => $"{b.File} {b.Block.Fingerprint} {b.Block.FreeReason} {b.Block.StartsWith}").Order(StringComparer.Ordinal).ToList();
        var rows = Register().Free.Select(r => $"{r.File} {r.Block} {r.Reason} {r.StartsWith}").Order(StringComparer.Ordinal).ToList();
        var problems = expected.Except(rows).Select(e => "no register row (or a changed block): " + e)
            .Concat(rows.Except(expected).Select(r => "a register row without its block: " + r)).ToList();
        if (expected.Count != rows.Count && problems.Count == 0) problems.Add($"{expected.Count} claim-free blocks, {rows.Count} rows");
        FailWithActual(problems);
    }

    [Fact]
    public void Every_quote_is_found_in_its_block()
    {
        var blocks = Blocks();
        var problems = new List<string>();
        foreach (var row in Register().Claims)
        {
            var block = blocks.FirstOrDefault(b => b.Block.ClaimIds.Contains(row.Id));
            if (block.Block is null) continue; // the id test names the orphan
            var quote = ClaimRules.Normalize(row.Quote);
            if (quote.Length < 8) problems.Add($"{row.Id}: the quote '{row.Quote}' is too short to say anything");
            else if (!block.Block.Normalized.Contains(quote, StringComparison.Ordinal))
                problems.Add($"{row.Id}: the quote '{row.Quote}' is not in its block ({Where(block)})");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_claim_names_a_known_check_and_existing_tests_that_run()
    {
        var problems = new List<string>();
        var catalog = TestCatalog.Instance;
        foreach (var row in Register().Claims)
        {
            var checks = row.Check.Split('+', StringSplitOptions.TrimEntries);
            if (checks.Length == 0 || checks.Any(c => !ClaimRules.Checks.Contains(c)))
                problems.Add($"{row.Id}: check '{row.Check}' is not one of {string.Join(", ", ClaimRules.Checks)} (joined with ' + ')");
            if (row.Tests.Count == 0) problems.Add($"{row.Id}: names no test");
            foreach (var t in row.Tests)
            {
                if (t.Planned || t.Class is null) { problems.Add($"{row.Id}: '{t.Raw}' is a planned entry; a claim needs a test now"); continue; }
                var resolved = t.Method is not null && catalog.TypeExists(t.Raw) ? catalog.Resolve(t.Raw, null) : catalog.Resolve(t.Class, t.Method);
                if (resolved.Count == 0) { problems.Add($"{row.Id}: `{t.Raw}` names no test (R4)"); continue; }
                foreach (var (type, method) in resolved)
                {
                    if (catalog.IsStaticallySkipped(method)) problems.Add($"{row.Id}: {type.Name}.{method.Name} is statically skipped (R7)");
                    if (catalog.RunOf(type, method) == TestRun.None) problems.Add($"{row.Id}: {type.Name}.{method.Name} runs in no CI run (R9)");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_gate_requires_every_test_a_claim_names()
    {
        // Q3 (a): the release gate reads CLAIMS.md as a source of named tests (R8), so a claim test cannot vanish
        // behind the floor of its run.
        var inputs = GateFiles.Inputs();
        Assert.NotNull(inputs.Claims);
        var required = string.Join("\n", ReleaseGate.ReleaseGate.RenderManifestBlocks(inputs, GateFiles.Version()).Values);
        var catalog = TestCatalog.Instance;
        foreach (var row in Register().Claims)
        foreach (var t in row.Tests.Where(t => t.Class is not null))
        foreach (var (type, method) in catalog.Resolve(t.Class!, t.Method))
            Assert.Contains($" {type.FullName}.{method.Name}\n", required);

        // Without the register the claim-only tests drop out of the generated blocks.
        var without = string.Join("\n", ReleaseGate.ReleaseGate.RenderManifestBlocks(inputs with { Claims = null }, GateFiles.Version()).Values);
        Assert.DoesNotContain($" {typeof(SelfReportClaimTests).FullName}.", without);
    }

    [Fact]
    public void A_quoted_readiness_line_is_the_english_title_of_a_line()
    {
        // D12: in a governed document a quoted string after "readiness line" (or "readiness card … line") is the English
        // title of a readiness line, so a renamed line cannot leave a stale quote behind.
        var titles = ReadinessLocalizationTests.Resx("SharedResource.resx")
            .Where(kv => Regex.IsMatch(kv.Key, "^Readiness_[A-Za-z]+_Title$")).Select(kv => kv.Value).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        var quotes = 0;
        foreach (var (file, _) in ClaimRules.GovernedFiles)
        {
            var text = ClaimRules.Normalize(Read(file));
            foreach (Match m in Regex.Matches(text, @"readiness (?:line|card[^""]{0,80}?line)s?\s+""(?<title>[^""]+)""", RegexOptions.IgnoreCase))
            {
                quotes++;
                if (!titles.Contains(m.Groups["title"].Value))
                    problems.Add($"{file}: the readiness line \"{m.Groups["title"].Value}\" is no English Readiness_*_Title");
            }
        }
        Assert.True(quotes >= 5, $"only {quotes} quoted readiness lines found; the rule reads nothing");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
