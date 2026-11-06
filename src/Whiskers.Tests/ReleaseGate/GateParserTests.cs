namespace Whiskers.Tests.ReleaseGate;

/// <summary>Contract 0008: the readers of SECURITY_INVARIANTS.md, the §7 path matrix and RELEASE_GATE.md, on the real
/// files and on edge cases.</summary>
public class InvariantTableParserTests
{
    [Fact]
    public void The_real_table_has_every_invariant_once_with_its_status_and_tests()
    {
        var rows = InvariantTableParser.Parse(GateFiles.Require(GateFiles.Invariants));
        Assert.Equal(61, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.All(rows, r => Assert.NotNull(r.Status));
        var inv18 = rows.Single(r => r.Id == "INV-18");
        Assert.Equal(InvariantStatus.Violated, inv18.Status);
        Assert.Contains(inv18.Tests, t => t.Planned && t.Class is null);
        var inv21 = rows.Single(r => r.Id == "INV-21");
        Assert.Contains(inv21.Tests, t => t is { Planned: true, Class: "ReadOnlyKillSwitchWireTests", Method: null });
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
}

public class ReleaseGateDocumentTests
{
    [Fact]
    public void The_real_document_pins_the_highest_id_and_lists_every_open_invariant()
    {
        var gate = ReleaseGateDocument.Parse(GateFiles.Require(GateFiles.ReleaseGateDoc));
        Assert.Empty(gate.Problems);
        Assert.Equal("INV-61", gate.Pin);
        Assert.Equal(12, gate.Open.Count);
        Assert.Contains(gate.Open, o => o is { Id: "INV-18", Status: InvariantStatus.Violated, Package: "WP10" });
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
        Assert.Contains(ReleaseGateDocument.Parse(text + "\nHighest invariant ID: INV-62\n").Problems, p => p.Contains("2 times"));
        Assert.Contains(ReleaseGateDocument.Parse(text.Replace("Highest invariant ID: INV-61", "Highest ID INV-61")).Problems, p => p.Contains("no line"));
        Assert.Contains(ReleaseGateDocument.Parse(ReleaseGateTests.InsertAfter(text, "| INV-18 |", "| INV-99 | ❌ | WP1 |")).Problems, p => p.Contains("cells"));
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
