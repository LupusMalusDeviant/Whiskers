namespace Whiskers.Tests.ReleaseGate;

/// <summary>Contract 0008: the readers of SECURITY_INVARIANTS.md, the §7 path matrix and RELEASE_GATE.md, on the real
/// files and on edge cases.</summary>
public class InvariantTableParserTests
{
    [Fact]
    public void The_real_table_has_every_invariant_once_with_its_status_and_tests()
    {
        var rows = InvariantTableParser.Parse(GateFiles.Require(GateFiles.Invariants));
        Assert.Equal(62, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.All(rows, r => Assert.NotNull(r.Status));
        var inv18 = rows.Single(r => r.Id == "INV-18");
        Assert.Equal(InvariantStatus.Holds, inv18.Status);
        Assert.Contains(inv18.Tests, t => t is { Planned: false, Class: "NotificationsRoleTests", Method: null });
        // A planned entry may name no class (only on a ❌ or ❓ row, R6): INV-18 as it stood before it was closed.
        var planned = InvariantTableParser.Parse(ReleaseGateTests.SetCell(GateFiles.Require(GateFiles.Invariants), "| INV-18 |", 4,
            "the in-handler checks of these actions *(planned, WP10)*")).Single(r => r.Id == "INV-18");
        Assert.Contains(planned.Tests, t => t.Planned && t.Class is null);
        var inv21 = rows.Single(r => r.Id == "INV-21");
        Assert.Contains(inv21.Tests, t => t is { Planned: false, Class: "ReadOnlyKillSwitchWireTests", Method: null });
        Assert.Contains(inv21.Tests, t => t is { Planned: false, Class: "UiMutationRunnerTests", Method: "The_kill_switch_stops_a_human_too" });
    }

    [Theory]
    [InlineData("✅", InvariantStatus.Holds)]
    [InlineData("⚠️", InvariantStatus.Partial)]
    [InlineData("⚠", InvariantStatus.Partial)]
    [InlineData("❌", InvariantStatus.Violated)]
    [InlineData("❓", InvariantStatus.Unverified)]
    public void A_bare_symbol_is_a_status(string cell, InvariantStatus status)
        => Assert.Equal(status, InvariantTableParser.ParseStatus(cell));

    [Theory]
    [InlineData("✅ per host after migration")]
    [InlineData("✔")]
    [InlineData("")]
    [InlineData("✅✅")]
    public void Anything_else_is_no_status(string cell) => Assert.Null(InvariantTableParser.ParseStatus(cell));

    [Fact]
    public void Prose_planned_entries_and_escaped_pipes_are_read_as_written()
    {
        const string table = "| ID | Invariant | Status | Evidence / gap | Test | WP |\n|---|---|---|---|---|---|\n" +
            "| INV-01 | a \\| b | ⚠️ | `x \\| y` | `ATests` (incl. the wire test `ATests.One`), R1-47: prose, `BTests.Two` *(planned, WP6)*, the checks *(planned, WP10)* | WP1 |\n";
        var row = Assert.Single(InvariantTableParser.Parse(table));
        Assert.Equal(InvariantStatus.Partial, row.Status);
        Assert.Equal("WP1", row.Package);
        Assert.Collection(row.Tests.OrderBy(t => t.Raw, StringComparer.Ordinal),
            t => Assert.True(t is { Class: null, Planned: true }),
            t => Assert.True(t is { Class: "ATests", Method: null, Planned: false }),
            t => Assert.True(t is { Class: "ATests", Method: "One", Planned: false }),
            t => Assert.True(t is { Class: "BTests", Method: "Two", Planned: true }));
    }

    [Fact]
    public void A_row_with_an_unescaped_pipe_is_malformed()
    {
        const string table = "| ID | Invariant | Status | Evidence / gap | Test | WP |\n|---|---|---|---|---|---|\n" +
            "| INV-01 | a | ⚠️ | `x | y` | `ATests` | WP1 |\n";
        Assert.Throws<FormatException>(() => InvariantTableParser.Parse(table));
    }
}

public class PathMatrixParserTests
{
    [Fact]
    public void The_real_matrix_has_its_rows_and_each_non_m_row_names_its_capabilities()
    {
        var problems = new List<string>();
        var rows = PathMatrixParser.Parse(GateFiles.Require(GateFiles.Contract), GateFiles.Read(GateFiles.PathCapabilities), problems);
        Assert.Empty(problems);
        Assert.Equal(49, rows.Count(r => r.Class == "M"));
        Assert.All(rows.Where(r => r.Class == "M"), r => Assert.NotNull(r.McpTool));
        Assert.Equal("pause_server_checks", rows.Single(r => r.Id == "M-38").McpTool);
        Assert.Equal(["checks.pause", "checks.resume"], rows.Single(r => r.Id == "U-22").Capabilities);
        Assert.NotNull(rows.Single(r => r.Id == "S-13").NoCapabilityReason);
        var totals = PathMatrixParser.Totals(GateFiles.Require(GateFiles.Contract), problems);
        Assert.Equal(rows.Count, totals!["Total"]);
    }

    [Fact]
    public void A_missing_extra_or_reasonless_mapping_is_a_problem()
    {
        const string contract = "## 7. Paths\n\n| ID | Handler |\n|---|---|\n| U-01 | a |\n| U-02 | b |\n| M-01 | `start_container` |\n\n## 8. End\n";
        const string mapping = "## Rows and capabilities\n\n| Row | Capabilities |\n|---|---|\n| U-01 | container.start |\n| U-09 | x |\n| U-02 | — |\n";
        var problems = new List<string>();
        var rows = PathMatrixParser.Parse(contract, mapping, problems);
        Assert.Equal(3, rows.Count);
        Assert.Contains(problems, p => p.Contains("U-09"));
        Assert.Contains(problems, p => p.Contains("U-02") && p.Contains("no reason"));
        problems.Clear();
        PathMatrixParser.Parse(contract, null, problems);
        Assert.Contains(problems, p => p.Contains("PATH_CAPABILITIES.md is missing"));
    }

    [Fact]
    public void Indented_and_tight_rows_are_read_fenced_ones_are_not_and_a_near_miss_is_a_problem()
    {
        // Review round 6 m-2: the §7 reader takes every '|' line outside a fence.
        const string contract = "## 7. Paths\n\n| ID | Handler |\n|---|---|\n| U-01 | a |\n  | U-02 | b |\n|A-12| c |\n"
            + "```\n| U-90 | example |\n```\n| **U-40** | bold |\n\n## 8. End\n";
        const string mapping = "## Rows and capabilities\n\n| Row | Capabilities |\n|---|---|\n| U-01 | x.a |\n| U-02 | x.b |\n| A-12 | x.c |\n";
        var problems = new List<string>();
        var rows = PathMatrixParser.Parse(contract, mapping, problems);
        Assert.Equal(["U-01", "U-02", "A-12"], rows.Select(r => r.Id));
        Assert.DoesNotContain(rows, r => r.Id == "U-90");
        Assert.Contains(problems, p => p.Contains("'**U-40**' looks like a §7 row ID"));
        Assert.Single(problems);

        // Review round 7 n-2/n-3: other near misses — a suffix, lower case, an en dash, a letter O for a zero — are problems,
        // and a fenced '## 8.' inside §7 does not end it.
        const string nearMisses = "## 7. Paths\n\n| ID | Handler |\n|---|---|\n| U-01 | a |\n| U-40a | b |\n| u-41 | c |\n| U–42 | d |\n| U-4O | e |\n"
            + "```\n## 8. Example\n```\n| U-02 | f |\n\n## 8. End\n";
        const string nearMapping = "## Rows and capabilities\n\n| Row | Capabilities |\n|---|---|\n| U-01 | x.a |\n| U-02 | x.f |\n";
        var nearProblems = new List<string>();
        var nearRows = PathMatrixParser.Parse(nearMisses, nearMapping, nearProblems);
        Assert.Equal(["U-01", "U-02"], nearRows.Select(r => r.Id));
        foreach (var miss in new[] { "U-40a", "u-41", "U–42", "U-4O" })
            Assert.Contains(nearProblems, p => p.Contains($"'{miss}' looks like a §7 row ID"));
        Assert.Equal(4, nearProblems.Count);

        // Review round 8 n-2: no dash or spaces, a minus sign, a class letter after other letters are near misses too;
        // words with a class letter and a lower-case 'o' ('MCP tools', 'Total') are not.
        const string wider = "## 7. Paths\n\n| ID | Handler |\n|---|---|\n| U-01 | a |\n| U40 | b |\n| **U 41** | c |\n| U\u221242 | d |\n"
            + "| MU-43 | e |\n| XU-44 | f |\n| MCP tools (M) | g |\n| **Total** | h |\n"
            // Review round 9 n-1/n-2: a letter O or o, or I, for a digit.
            + "| **U-O** | i |\n| **U-O4** | j |\n| **U-o1** | k |\n| **U-OI** | l |\n"
            // Review round 9 n-4: lookalikes — a Cyrillic class letter, zero-width and soft hyphens, small and fullwidth
            // hyphen-minus, fullwidth letters and digits (a fullwidth-digit ID is no row either).
            + "| \u0422-45 | m |\n| U\u200B-46 | n |\n| U\u00AD47 | o |\n| U\uFE6348 | p |\n| U\uFF0D49 | q |\n"
            + "| \uFF35-\uFF15\uFF10 | r |\n| U-\uFF15\uFF11 | s |\n"
            // Review round 10: without a digit, after '**' so that rule (d) cannot catch them, the Dash and Invisible
            // characters and the Cyrillic O are what makes these near misses.
            + "| **U\uFF0DOI** | t |\n| **U\u200B-O** | u |\n| **U\u00AD-lO** | v |\n| **U-\u041El** | w |\n\n## 8. End\n";
        const string widerMapping = "## Rows and capabilities\n\n| Row | Capabilities |\n|---|---|\n| U-01 | x.a |\n";
        var widerProblems = new List<string>();
        Assert.Equal(["U-01"], PathMatrixParser.Parse(wider, widerMapping, widerProblems).Select(r => r.Id));
        string[] misses =
        [
            "U40", "**U 41**", "U\u221242", "MU-43", "XU-44", "**U-O**", "**U-O4**", "**U-o1**", "**U-OI**",
            "\u0422-45", "U\u200B-46", "U\u00AD47", "U\uFE6348", "U\uFF0D49", "\uFF35-\uFF15\uFF10", "U-\uFF15\uFF11",
            "**U\uFF0DOI**", "**U\u200B-O**", "**U\u00AD-lO**", "**U-\u041El**",
        ];
        foreach (var miss in misses)
            Assert.Contains(widerProblems, p => p.Contains($"'{miss}' looks like a §7 row ID"));
        Assert.Equal(misses.Length, widerProblems.Count);

        // Review round 8 n-3: a fenced '## 7. Example' before the real §7 does not start it, so a row-like line between
        // the two is neither a row nor a problem.
        const string fencedStart = "## 6. Before\n\n```\n## 7. Example\n```\n| U-09 | x |\n\n## 7. Paths\n\n| ID | Handler |\n|---|---|\n"
            + "| U-01 | a |\n\n## 8. End\n";
        var fencedProblems = new List<string>();
        Assert.Equal(["U-01"], PathMatrixParser.Parse(fencedStart, widerMapping, fencedProblems).Select(r => r.Id));
        Assert.Empty(fencedProblems);

        // Review round 6 NIT a: a class or the total counted twice in §7.6 is a problem.
        var countProblems = new List<string>();
        PathMatrixParser.Totals("**Rows today**\n\n| Class | Rows |\n|---|---|\n| UI (U) | 3 |\n| UI (U) | 4 |\n| **Total** | **7** |\n| Total | 7 |\n", countProblems);
        Assert.Contains(countProblems, p => p.Contains("'U' is counted twice"));
        Assert.Contains(countProblems, p => p.Contains("'Total' is counted twice"));

        // Review round 6 NIT b: the base comparison reads no ID from a fenced example.
        Assert.Equal(["INV-01", "INV-02"], BaseComparison.RowIds("| INV-01 | a |\n```\n| INV-99 | example |\n```\n  | INV-02 | b |\n"));
    }
}

public class ReleaseGateDocumentTests
{
    [Fact]
    public void The_real_document_pins_the_highest_id_and_lists_every_open_invariant()
    {
        var gate = ReleaseGateDocument.Parse(GateFiles.Require(GateFiles.ReleaseGateDoc));
        Assert.Empty(gate.Problems);
        Assert.Equal("INV-62", gate.Pin);
        Assert.Equal(InvariantTableParser.Parse(GateFiles.Require(GateFiles.Invariants)).Count(r => r.Status != InvariantStatus.Holds),
            gate.Open.Count);
        Assert.Contains(gate.Open, o => o is { Id: "INV-57", Status: InvariantStatus.Partial, Package: "WP2" });
        Assert.Contains(gate.Open, o => o is { Id: "INV-58", Status: InvariantStatus.Partial });
        Assert.Empty(gate.Exceptions);
        Assert.Empty(gate.Known);
        Assert.Empty(gate.Retired);
        Assert.Contains(gate.Suites, s => s is { Workflow: "ci.yml", Job: "build-test", Step: "Test (in-process)" });
        Assert.Contains(gate.Suites, s => s is { Workflow: "release.yml", Job: "scan", Step: null });
    }

    [Fact]
    public void A_missing_heading_a_second_pin_or_a_short_row_is_a_problem()
    {
        var text = GateFiles.Require(GateFiles.ReleaseGateDoc);
        Assert.Contains(ReleaseGateDocument.Parse(text.Replace("## Platform skips", "## Skips")).Problems, p => p.Contains("Platform skips"));
        Assert.Contains(ReleaseGateDocument.Parse(text + "\nHighest invariant ID: INV-63\n").Problems, p => p.Contains("2 times"));
        Assert.Contains(ReleaseGateDocument.Parse(text.Replace("Highest invariant ID: INV-62", "Highest ID INV-62")).Problems, p => p.Contains("no line"));
        Assert.Contains(ReleaseGateDocument.Parse(ReleaseGateTests.InsertAfter(text, "| INV-01 |", "| INV-99 | ❌ | WP1 |")).Problems, p => p.Contains("cells"));
    }

    [Fact]
    public void A_row_after_a_blank_line_is_a_problem_not_dropped()
    {
        // Review NIT: a blank line ends the table; the release.yml suite rows below it must not vanish silently.
        var text = GateFiles.Require(GateFiles.ReleaseGateDoc);
        var line = ReleaseGateTests.LineStartingWith(text, "| release.yml | scan |");
        var split = text.Replace(line + "\n", "\n" + line + "\n");
        Assert.Contains(ReleaseGateDocument.Parse(split).Problems, p => p.Contains("after the end of the table"));
        // Two blank lines, and a blank line then text then the row (review round 2, m-C).
        var twoBlanks = text.Replace(line + "\n", "\n\n" + line + "\n");
        Assert.Contains(ReleaseGateDocument.Parse(twoBlanks).Problems, p => p.Contains("after the end of the table"));
        var textBetween = text.Replace(line + "\n", "\nSome prose.\n" + line + "\n");
        Assert.Contains(ReleaseGateDocument.Parse(textBetween).Problems, p => p.Contains("after the end of the table"));
        // A blank line, a data row, then a separator line is no new table of that header either.
        var fakeHeader = text.Replace(line + "\n", "\n" + line + "\n|---|---|---|---|\n");
        Assert.Contains(ReleaseGateDocument.Parse(fakeHeader).Problems, p => p.Contains("after the end of the table"));
        Assert.Empty(ReleaseGateDocument.Parse(text).Problems);
    }

    [Fact]
    public void A_repeated_table_with_the_same_header_is_read_where_repeats_are_allowed()
    {
        const string header = "| ID | Invariant | Status | Evidence / gap | Test | WP |\n|---|---|---|---|---|---|\n";
        const string row1 = "| INV-01 | a | ✅ | e | `ATests` | WP1 |\n";
        const string row2 = "| INV-02 | b | ✅ | e | `BTests` | WP1 |\n";
        Assert.Equal(2, InvariantTableParser.Parse(header + row1 + "\nprose\n\n" + header + row2).Count);
        Assert.Throws<FormatException>(() => InvariantTableParser.Parse(header + row1 + "\nprose\n" + row2));
        Assert.Throws<FormatException>(() => MarkdownTables.After("## H\n\n" + header + row1 + "\n" + header + row2, "## H"));
    }

    [Fact]
    public void A_fenced_comment_is_no_heading_and_a_fenced_pipe_is_no_row()
    {
        // Review round 3 NIT: only '#'..'######' plus a space ends the check, and only outside code fences.
        const string table = "## H\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n";
        Assert.Throws<FormatException>(() => MarkdownTables.After(table + "```bash\n# a comment\n```\n| 3 | 4 |\n", "## H"));
        Assert.Throws<FormatException>(() => MarkdownTables.After(table + "#hashtag, not a heading\n| 3 | 4 |\n", "## H"));
        Assert.Single(MarkdownTables.After(table + "```\n| a | pipe | in code |\n```\n\n## Next\n\n| 3 | 4 |\n", "## H"));
        // Review round 4 NIT 1: a fence closes only with its own character and at least its length.
        Assert.Throws<FormatException>(() => MarkdownTables.After(table + "```\n~~~\n# comment\n```\n| 3 | 4 |\n", "## H"));
        Assert.Throws<FormatException>(() => MarkdownTables.After(table + "````\n```\n# comment\n````\n| 3 | 4 |\n", "## H"));
        Assert.Equal([false, true, true, true, true, false], MarkdownTables.Fenced(["a", "````md", "```", "~~~", "`````", "b"]));
        // Review round 4 NIT 3: a fenced example table between the heading and the real table is not the table.
        var rows = MarkdownTables.After("## H\n\n```\n| X | Y |\n|---|---|\n| no | no |\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |\n", "## H");
        Assert.Equal(["1", "2"], Assert.Single(rows).Cells);
        Assert.Throws<FormatException>(() => MarkdownTables.After("## H\n\n```\n# not a heading\n| X |\n```\n\n## Next\n", "## H"));
        // Review round 5 NIT a: an opener indented by four spaces is code, not a fence; a backtick opener whose info string
        // holds a backtick is no opener; a tilde opener may.
        Assert.Equal([false, false, false], MarkdownTables.Fenced(["    ```", "| x |", "    ```"]));
        Assert.Equal([true, true, true], MarkdownTables.Fenced(["   ```", "| x |", "   ```"]));
        Assert.Equal([false, false], MarkdownTables.Fenced(["``` a`b", "| x |"]));
        Assert.Equal([true, true, true], MarkdownTables.Fenced(["~~~ a`b", "| x |", "~~~"]));
    }

    [Fact]
    public void Every_table_reader_skips_fenced_example_tables()
    {
        // Review round 5 m-1: the header checked is the header of the table read, not a fenced example above it.
        var gate = GateFiles.Require(GateFiles.ReleaseGateDoc);
        // The real Open table rewritten with four columns, the correct five-column header shown above it in a fence.
        var lines = MarkdownTables.Lines(gate).ToList();
        var at = lines.FindIndex(l => l.StartsWith("| Invariant | Status |", StringComparison.Ordinal));
        var end = at;
        while (end < lines.Count && lines[end].StartsWith('|')) end++;
        var fourColumns = lines.Skip(at).Take(end - at).Select(l => "| " + string.Join(" | ", MarkdownTables.Split(l).Take(4)) + " |").ToList();
        var wrong = lines.Take(at).Concat(["```", lines[at], lines[at + 1], "```", ""]).Concat(fourColumns).Concat(lines.Skip(end));
        var wrongOpen = string.Join("\n", wrong);
        var parsed = ReleaseGateDocument.Parse(wrongOpen);
        Assert.Contains(parsed.Problems, p => p.Contains(ReleaseGateDocument.OpenHeading) && p.Contains("columns"));
        Assert.Empty(parsed.Open); // no row is read with shifted columns
        var contract = GateFiles.Require(GateFiles.Contract);
        var decisionsHeader = "| " + string.Join(" | ", ReleaseDecisions.Header) + " |";
        var wrongDecisions = contract.Replace(decisionsHeader + "\n", "```\n" + decisionsHeader + "\n```\n\n| ID | Kind | Invariant | Decision | Status | Date | Decided by | Notes |\n");
        Assert.NotNull(ReleaseDecisions.Parse(wrongDecisions).Problem);

        // Review round 5 m-3: WithHeader and the §7.6 totals skip fenced lines as After does.
        const string header = "| ID | Invariant | Status | Evidence / gap | Test | WP |\n|---|---|---|---|---|---|\n";
        Assert.Empty(InvariantTableParser.Parse("```\n" + header + "| INV-01 | a | ✅ | e | `ATests` | WP1 |\n```\n"));
        var problems = new List<string>();
        var totals = PathMatrixParser.Totals("```\n**Rows today**\n\n| Class | Rows |\n|---|---|\n| **Total** | **1** |\n```\n\n**Rows today**\n\n| Class | Rows |\n|---|---|\n| **Total** | **7** |\n", problems);
        Assert.Equal(7, totals!["Total"]);
        var exampleFirst = PathMatrixParser.Totals("**Rows today**\n\n```\n| Class | Rows |\n|---|---|\n| **Total** | **1** |\n```\n\n| Class | Rows |\n|---|---|\n| **Total** | **7** |\n", problems);
        Assert.Equal(7, exampleFirst!["Total"]);
    }

    [Fact]
    public void The_decision_table_needs_its_heading_and_columns()
    {
        var contract = GateFiles.Require(GateFiles.Contract);
        var (decisions, problem) = ReleaseDecisions.Parse(contract);
        Assert.Null(problem);
        Assert.Empty(decisions);
        Assert.NotNull(ReleaseDecisions.Parse(contract.Replace("### 6.1 Release decisions", "### 6.1 Decisions")).Problem);
        Assert.NotNull(ReleaseDecisions.Parse(contract.Replace("| ID | Kind | Invariant | Decision | Release | Status | Date | Decided by |",
            "| ID | Kind | Invariant | Decision | Status | Date | Decided by | Notes |")).Problem);
        var one = ReleaseGateTests.WithDecision(contract, "RG-1", "exception", "INV-01");
        var d = Assert.Single(ReleaseDecisions.Parse(one).Decisions);
        Assert.Equal(("RG-1", "exception", "INV-01", (string?)null, "Accepted", new DateOnly(2026, 1, 1)), (d.Id, d.Kind, d.Invariant, d.Release, d.Status, d.Date!.Value));
    }
}
