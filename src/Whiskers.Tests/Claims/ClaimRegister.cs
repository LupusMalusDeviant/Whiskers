using System.Text;
using Whiskers.Tests.ReleaseGate;

namespace Whiskers.Tests.Claims;

/// <summary>A row of the register's <i>Claims</i> table (D9).</summary>
public sealed record ClaimRow(string Id, string File, string Block, string Quote, string Check, string TestsCell, int Line)
{
    public IReadOnlyList<TestReference> Tests => InvariantTableParser.ParseTests(TestsCell);
}

/// <summary>A row of the register's <i>Claim-free blocks</i> table (D9).</summary>
public sealed record ClaimFreeRow(string File, string Block, string Reason, string StartsWith, int Line);

/// <summary>A retired claim id: listed so that it is never reused.</summary>
public sealed record RetiredClaim(string Id, string Why, int Line);

/// <summary>
/// Reads and renders <c>docs/v1/CLAIMS.md</c> (contract 0011, D9): markdown tables under fixed headings, read with the
/// release gate's table reader. Used by <see cref="ClaimRegisterTests"/> and by the gate (R8), which requires every test
/// a claim names.
/// </summary>
public static class ClaimRegister
{
    public const string ClaimsHeading = "## Claims";
    public const string FreeHeading = "## Claim-free blocks";
    public const string RetiredHeading = "## Retired claims";
    public static readonly string[] ClaimsHeader = ["Claim", "File", "Block", "Quote", "Check", "Tests"];
    public static readonly string[] FreeHeader = ["File", "Block", "Reason", "Starts with"];
    public static readonly string[] RetiredHeader = ["Claim", "Why"];

    public sealed record Contents(IReadOnlyList<ClaimRow> Claims, IReadOnlyList<ClaimFreeRow> Free, IReadOnlyList<RetiredClaim> Retired);

    /// <exception cref="FormatException">A heading or table is missing, its header differs, or a row is malformed.</exception>
    public static Contents Parse(string markdown)
    {
        var claims = Table(markdown, ClaimsHeading, ClaimsHeader)
            .Select(r => new ClaimRow(r.Cells[0], r.Cells[1].Trim('`'), r.Cells[2].Trim('`'), r.Cells[3], r.Cells[4], r.Cells[5], r.Line)).ToList();
        var free = Table(markdown, FreeHeading, FreeHeader)
            .Select(r => new ClaimFreeRow(r.Cells[0].Trim('`'), r.Cells[1].Trim('`'), r.Cells[2], r.Cells[3], r.Line)).ToList();
        var retired = Table(markdown, RetiredHeading, RetiredHeader)
            .Select(r => new RetiredClaim(r.Cells[0], r.Cells[1], r.Line)).ToList();
        return new(claims, free, retired);
    }

    /// <summary>Claim id → its test references, for the gate (R8). Empty when the register cannot be read (the claim
    /// tests report that).</summary>
    public static IReadOnlyList<(string Id, IReadOnlyList<TestReference> Tests)> TestsOf(string? markdown)
    {
        if (markdown is null) return [];
        try { return Parse(markdown).Claims.Select(c => (c.Id, c.Tests)).ToList(); }
        catch (FormatException) { return []; }
    }

    private static IReadOnlyList<(int Line, string[] Cells)> Table(string markdown, string heading, string[] header)
    {
        var (found, rows) = MarkdownTables.AfterTable(markdown, heading);
        if (!found.SequenceEqual(header))
            throw new FormatException($"the table under '{heading}' must have the header '| {string.Join(" | ", header)} |'");
        return rows;
    }

    /// <summary>
    /// The register as it should be: the text before <see cref="ClaimsHeading"/> kept, every claim row with its block's
    /// current fingerprint (a claim no block names keeps its row with "?"), every claim-free block from its marker, the
    /// retired claims kept. Written as <c>CLAIMS.md.actual</c> on a difference.
    /// </summary>
    public static string Render(string current, IReadOnlyList<(string File, ClaimBlock Block)> blocks)
    {
        Contents? parsed = null;
        try { parsed = Parse(current); } catch (FormatException) { }
        var text = current.Replace("\r\n", "\n");
        var at = text.IndexOf("\n" + ClaimsHeading + "\n", StringComparison.Ordinal);
        var preamble = at < 0 ? "# Claim register\n" : text[..(at + 1)];

        var byId = blocks.SelectMany(b => b.Block.ClaimIds.Select(id => (Id: id, b.File, b.Block)))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rows = new List<ClaimRow>();
        foreach (var row in parsed?.Claims ?? [])
            rows.Add(byId.TryGetValue(row.Id, out var b) ? row with { File = b.File, Block = b.Block.Fingerprint } : row with { Block = "?" });
        foreach (var (id, b) in byId.Where(kv => rows.All(r => r.Id != kv.Key)))
            rows.Add(new ClaimRow(id, b.File, b.Block.Fingerprint, "?", "?", "?", 0));
        var order = blocks.Select((b, i) => (b, i)).SelectMany(x => x.b.Block.ClaimIds.Select(id => (id, x.i)))
            .GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        rows = rows.OrderBy(r => order.GetValueOrDefault(r.Id, int.MaxValue)).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();

        var sb = new StringBuilder(preamble);
        sb.Append(ClaimsHeading).Append("\n\n");
        sb.Append(Row(ClaimsHeader)).Append(Row(ClaimsHeader.Select(_ => "---")));
        foreach (var r in rows)
            sb.Append(Row([r.Id, $"`{r.File}`", $"`{r.Block}`", r.Quote, r.Check, r.TestsCell]));
        sb.Append('\n').Append(FreeHeading).Append("\n\n");
        sb.Append(Row(FreeHeader)).Append(Row(FreeHeader.Select(_ => "---")));
        foreach (var (file, block) in blocks.Where(b => b.Block.FreeReason is not null))
            sb.Append(Row([$"`{file}`", $"`{block.Fingerprint}`", block.FreeReason!, block.StartsWith]));
        sb.Append('\n').Append(RetiredHeading).Append("\n\n");
        sb.Append(Row(RetiredHeader)).Append(Row(RetiredHeader.Select(_ => "---")));
        foreach (var r in parsed?.Retired ?? []) sb.Append(Row([r.Id, r.Why]));
        return sb.ToString();
    }

    private static string Row(IEnumerable<string> cells)
        => "| " + string.Join(" | ", cells.Select(c => c.Replace("|", "\\|"))) + " |\n";
}
