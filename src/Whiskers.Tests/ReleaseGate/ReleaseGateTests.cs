namespace Whiskers.Tests.ReleaseGate;

/// <summary>
/// Contract 0008 (R1-51): the mechanical release gate. The first test runs the gate on the real repository in the
/// mode its version and run select; every other test mutates the real files in memory and proves one rule catches
/// what it is for, so no rule passes by finding nothing. Nothing is written to the repository except a
/// <c>.actual</c> file on a snapshot difference.
/// </summary>
public class ReleaseGateTests
{
    private static readonly GateVersion Dev = new(0, 15, 0);
    private static readonly GateVersion V100 = new(1, 0, 0);
    private static readonly GateVersion V101 = new(1, 0, 1);
    private static readonly GateVersion V102 = new(1, 0, 2);

    private const string LimitsDoc = "docs/operations/test-limits.md";
    private const string DecisionsHeader = "| ID | Kind | Invariant | Decision | Release | Status | Date | Decided by |\n|---|---|---|---|---|---|---|---|\n";
    private const string ExceptionsHeader = "| Invariant | Status shipped | Decision | Limitation | Review by |\n|---|---|---|---|---|\n";
    private const string KnownHeader = "| Invariant | Status | Decision | Advisory | Limitation | Release | Review by |\n|---|---|---|---|---|---|---|\n";
    private const string RetiredHeader = "| Invariant | Decision | Date |\n|---|---|---|\n";
    private const string SkipsHeader = "| Test | Skips when | Runs unskipped in | Why |\n|---|---|---|---|\n";

    private static readonly Lazy<GateInputs> RealInputs = new(GateFiles.Inputs);
    private static GateInputs Real => RealInputs.Value;

    /// <summary>The real records with INV-18 as it stood before it was closed: ❌, counted in §11 and listed as open. The
    /// checks that need a ❌ (R1, R2, R11, R17) use it now that the real table has none.</summary>
    private static readonly Lazy<GateInputs> ViolatedInputs = new(() => Real with
    {
        Invariants = Mutate(Mutate(SetCell(Real.Invariants, "| INV-18 |", 2, "❌"),
            "| ✅ | 56 |", "| ✅ | 55 |"), "| ❌ | 0 |", "| ❌ | 1 (INV-18) |"),
        ReleaseGate = InsertAfter(Real.ReleaseGate, "| INV-01 |",
            "| INV-18 | ❌ | WP10 | in-handler checks of approval decisions, notification deletion, scan and job start are missing | — |"),
    });
    private static GateInputs Violated => ViolatedInputs.Value;

    // ---- the real repository -----------------------------------------------------------------------------------

    [Fact]
    public void The_repository_passes_the_gate_in_its_mode()
    {
        var inputs = GateFiles.Inputs();
        var version = GateFiles.Version();
        var mode = ReleaseGate.Mode(version, Environment.GetEnvironmentVariable(ReleaseGate.ModeVariable));
        var findings = ReleaseGate.Evaluate(inputs, mode, version);

        // A difference in a generated file: write the fresh render beside it, never over it (as for the MCP catalog).
        if (findings.Any(f => f.Rule == "R8"))
            foreach (var (manifest, block) in ReleaseGate.RenderManifestBlocks(inputs, version))
                File.WriteAllText(GateFiles.Full($".github/required-tests/{manifest}") + ".actual",
                    ReleaseGate.WithBlock(inputs.Manifests.GetValueOrDefault(manifest) ?? "", block));
        if (findings.Any(f => f.Rule == "R12"))
            File.WriteAllText(GateFiles.Full(GateFiles.Inventory) + ".actual", RenderInventory(inputs));

        Assert.True(findings.Count == 0,
            $"The release gate ({mode} mode, version {version}) has {findings.Count} finding(s):\n" + string.Join("\n", findings));
    }

    [Fact]
    public void The_mode_follows_the_version_and_the_run_and_can_only_get_stricter()
    {
        Assert.Equal(GateMode.Development, ReleaseGate.Mode(Dev, null));
        Assert.Equal(GateMode.Development, ReleaseGate.Mode(Dev, ""));
        Assert.Equal(GateMode.Development, ReleaseGate.Mode(Dev, "release"));   // 0.x releases ship with open invariants
        foreach (var v in new[] { V100, new GateVersion(1, 2, 3), new GateVersion(2, 0, 0) })
        {
            Assert.Equal(GateMode.Line, ReleaseGate.Mode(v, null));
            Assert.Equal(GateMode.Line, ReleaseGate.Mode(v, ""));
            Assert.Equal(GateMode.Release, ReleaseGate.Mode(v, "release"));
            Assert.Equal(GateMode.Release, ReleaseGate.Mode(v, "dry-run"));
        }
        Assert.Equal(GateMode.Release, ReleaseGate.Mode(Dev, "dry-run"));
        foreach (var bad in new[] { "Release", "true", "off", "relase", "development" })
        {
            Assert.Throws<ArgumentException>(() => ReleaseGate.Mode(Dev, bad));
            Assert.Throws<ArgumentException>(() => ReleaseGate.Mode(V100, bad));
        }
        // No value gives a mode below the one the version alone gives.
        foreach (var v in new[] { Dev, V100, V102 })
        foreach (var value in new[] { null, "", "release", "dry-run" })
            Assert.True(ReleaseGate.Mode(v, value) >= ReleaseGate.Mode(v, null));
    }

    [Fact]
    public void An_unresolvable_version_fails()
    {
        Assert.Equal(Dev, ReleaseGate.ReadVersion("<Project><PropertyGroup><Version>0.15.0</Version></PropertyGroup></Project>"));
        Assert.Throws<FormatException>(() => ReleaseGate.ReadVersion("<Project><PropertyGroup></PropertyGroup></Project>"));
        Assert.Throws<FormatException>(() => ReleaseGate.ReadVersion("<Version>1.0.0</Version><Version>1.0.0</Version>"));
        Assert.Throws<FormatException>(() => ReleaseGate.ReadVersion("<Version>1.0</Version>"));
        Assert.Throws<FormatException>(() => ReleaseGate.ReadVersion("<Version>1.0.0-rc.1</Version>"));
        Assert.Throws<FormatException>(() => ReleaseGate.ReadVersion("<Version>01.0.0</Version>"));
    }

    // ---- R1, R2 ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_non_green_invariant_fails_the_release()
    {
        // Today's records in a 1.0.0 release run: every ⚠️ and the ❌ are refused.
        var release = Evaluate(Violated, GateMode.Release, V100);
        Assert.Contains(release, f => f.Rule == "R1" && f.Subject == "INV-01");
        Assert.Contains(release, f => f.Rule == "R1" && f.Subject == "INV-18");

        // ⚠️ with a complete, accepted exception passes; ❌ fails even with a complete exception row.
        var excepted = WithException(Violated, "INV-01", "RG-1") with { };
        excepted = WithException(excepted, "INV-18", "RG-2", status: "❌");
        var findings = Evaluate(excepted, GateMode.Release, V100);
        Assert.DoesNotContain(findings, f => f.Rule == "R1" && f.Subject == "INV-01");
        Assert.Contains(findings, f => f.Rule == "R1" && f.Subject == "INV-18");
        Assert.Contains(findings, f => f.Rule == "R11" && f.Subject == "INV-18" && f.Message.Contains("never accepted"));

        // ❌ with a valid known-violation record passes in a patch release (D15 c) — and never at X.Y.0.
        Assert.DoesNotContain(Evaluate(WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.1"), GateMode.Release, V101),
            f => f.Rule == "R1" && f.Subject == "INV-18");
        Assert.Contains(Evaluate(WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.0"), GateMode.Release, V100),
            f => f.Rule == "R1" && f.Subject == "INV-18");
    }

    [Fact]
    public void An_open_list_that_is_missing_or_stale_fails()
    {
        Assert.DoesNotContain(Evaluate(Real, GateMode.Development, Dev), f => f.Rule == "R2");

        var unlisted = Real with { ReleaseGate = RemoveLine(Real.ReleaseGate, "| INV-01 |") };
        Assert.Contains(Evaluate(unlisted, GateMode.Development, Dev), f => f.Rule == "R2" && f.Subject == "INV-01");

        // An accepted exception only waives the reference, not the row.
        var excepted = WithException(unlisted, "INV-01", "RG-1");
        Assert.Contains(Evaluate(excepted, GateMode.Development, Dev), f => f.Rule == "R2" && f.Subject == "INV-01");
        Assert.Contains(Evaluate(excepted, GateMode.Line, V100), f => f.Rule == "R2" && f.Subject == "INV-01");

        var stale = Real with { ReleaseGate = InsertAfter(Real.ReleaseGate, "| INV-01 |", "| INV-02 | ✅ | WP6 | done | — |") };
        Assert.Contains(Evaluate(stale, GateMode.Development, Dev), f => f.Rule == "R2" && f.Subject == "INV-02" && f.Message.Contains("✅ now"));

        var wrongStatus = Violated with { ReleaseGate = SetCell(Violated.ReleaseGate, "| INV-18 |", 1, "⚠️") };
        Assert.Contains(Evaluate(wrongStatus, GateMode.Development, Dev), f => f.Rule == "R2" && f.Subject == "INV-18");
    }

    [Fact]
    public void A_new_violation_is_mergeable_after_1_0_only_with_a_reference()
    {
        // Without a reference every open row fails in Line mode.
        Assert.Contains(Evaluate(Violated, GateMode.Line, V100), f => f.Rule == "R2" && f.Subject == "INV-18" && f.Message.Contains("reference"));

        foreach (var reference in new[] { "GHSA-2c4m-59x8-f3xq", "https://github.com/LupusMalusDeviant/Whiskers/issues/123" })
        {
            var referenced = Violated with { ReleaseGate = SetCell(Violated.ReleaseGate, "| INV-18 |", 4, reference) };
            Assert.DoesNotContain(Evaluate(referenced, GateMode.Line, V100), f => f.Rule == "R2" && f.Subject == "INV-18");
            // The same files cannot be released.
            Assert.Contains(Evaluate(referenced, GateMode.Release, V100), f => f.Rule == "R1" && f.Subject == "INV-18");
        }
        foreach (var bad in new[] { "#123", "GHSA-1234-5678-9abc", "https://github.com/someone/else/issues/1", "see the advisory" })
        {
            var malformed = Violated with { ReleaseGate = SetCell(Violated.ReleaseGate, "| INV-18 |", 4, bad) };
            Assert.Contains(Evaluate(malformed, GateMode.Line, V100), f => f.Rule == "R2" && f.Subject == "INV-18");
        }
    }

    // ---- R3 ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_malformed_qualified_or_miscounted_table_fails()
    {
        var row03 = LineStartingWith(Real.Invariants, "| INV-03 |");
        var duplicate = Real with { Invariants = InsertAfter(Real.Invariants, "| INV-03 |", row03) };
        Assert.Contains(Evaluate(duplicate, GateMode.Development, Dev), f => f.Rule == "R3" && f.Subject == "INV-03" && f.Message.Contains("appears 2 times"));

        var unknown = Real with { Invariants = SetCell(Real.Invariants, "| INV-03 |", 2, "✔") };
        Assert.Contains(Evaluate(unknown, GateMode.Development, Dev), f => f.Rule == "R3" && f.Subject == "INV-03" && f.Message.Contains("bare symbol"));

        var qualified = Real with { Invariants = SetCell(Real.Invariants, "| INV-58 |", 2, "✅ per host after migration") };
        Assert.Contains(Evaluate(qualified, GateMode.Development, Dev), f => f.Rule == "R3" && f.Subject == "INV-58");

        // A table under a new heading whose header the parser does not recognise: its ❌ row is invisible to the table,
        // the pin and §11 are left alone — the row line itself is the finding (review round 4 m-1).
        var hidden = Real with
        {
            Invariants = Mutate(Real.Invariants, "## 11.", "## 12. Something\n\n| ID | Invariant | Status | Evidence/gap | Test | WP |\n|---|---|---|---|---|---|\n"
                + "| INV-63 | hidden | ❌ | none | — | WP1 |\n\n## 11."),
        };
        Assert.Contains(Evaluate(hidden, GateMode.Development, Dev), f => f.Rule == "R3" && f.Subject == "INV-63" && f.Message.Contains("cannot read"));
        // Indented by up to three spaces the row still renders as a table row on GitHub (review round 5 m-2).
        var indented = Real with
        {
            Invariants = Mutate(Real.Invariants, "## 11.", "## 12. Something\n\n  | ID | Invariant | Status | Evidence/gap | Test | WP |\n  |---|---|---|---|---|---|\n"
                + "  | INV-63 | hidden | ❌ | none | — | WP1 |\n\n## 11."),
        };
        Assert.Contains(Evaluate(indented, GateMode.Development, Dev), f => f.Rule == "R3" && f.Subject == "INV-63" && f.Message.Contains("cannot read"));
        // ...but a row in a fenced example is no row.
        var example = Real with { Invariants = Mutate(Real.Invariants, "## 11.", "```\n| INV-63 | example |\n```\n\n## 11.") };
        Assert.DoesNotContain(Evaluate(example, GateMode.Development, Dev), f => f.Subject == "INV-63");

        var miscounted = Real with { Invariants = Mutate(Real.Invariants, "| ✅ | 56 |", "| ✅ | 57 |") };
        Assert.Contains(Evaluate(miscounted, GateMode.Development, Dev), f => f.Rule == "R3" && f.Message.Contains("✅ is 57, the table has 56"));

        var pipe = Real with { Invariants = Mutate(Real.Invariants, "`docker system\\|container", "`docker system|container") };
        Assert.Contains(Evaluate(pipe, GateMode.Development, Dev), f => f.Rule == "R3" && f.Message.Contains("cells"));
    }

    [Fact]
    public void A_deleted_invariant_is_caught_even_with_the_count_adjusted()
    {
        var deleted = Real with
        {
            Invariants = Mutate(Mutate(RemoveLine(Real.Invariants, "| INV-61 |"), "| ✅ | 56 |", "| ✅ | 55 |"), "| **Total** | **62** |", "| **Total** | **61** |"),
        };
        var findings = Evaluate(deleted, GateMode.Development, Dev);
        Assert.DoesNotContain(findings, f => f.Message.Contains("§11") || f.Subject.Contains("§11"));
        Assert.Contains(findings, f => f.Rule == "R3" && f.Subject == "INV-61" && f.Message.Contains("neither a row of the table nor retired"));

        var abovePin = Real with { ReleaseGate = Mutate(Real.ReleaseGate, "Highest invariant ID: INV-62", "Highest invariant ID: INV-61") };
        Assert.Contains(Evaluate(abovePin, GateMode.Development, Dev), f => f.Rule == "R3" && f.Subject == "INV-62" && f.Message.Contains("above the pinned"));

        // Retired properly: a 'Retired invariants' row with an accepted retirement decision.
        var retired = deleted with
        {
            ReleaseGate = Mutate(deleted.ReleaseGate, RetiredHeader, RetiredHeader + "| INV-61 | RG-1 | 2026-01-01 |\n"),
            Contract = WithDecision(deleted.Contract, "RG-1", "retirement", "INV-61"),
        };
        Assert.DoesNotContain(Evaluate(retired, GateMode.Development, Dev), f => f.Rule == "R3");

        var undecided = retired with { Contract = deleted.Contract };
        Assert.Contains(Evaluate(undecided, GateMode.Development, Dev), f => f.Rule == "R3" && f.Subject == "INV-61" && f.Message.Contains("RG-1"));

        var alsoRow = Real with
        {
            ReleaseGate = Mutate(Real.ReleaseGate, RetiredHeader, RetiredHeader + "| INV-61 | RG-1 | 2026-01-01 |\n"),
            Contract = WithDecision(Real.Contract, "RG-1", "retirement", "INV-61"),
        };
        Assert.Contains(Evaluate(alsoRow, GateMode.Development, Dev), f => f.Rule == "R3" && f.Subject == "INV-61" && f.Message.Contains("still a row"));

        var pinBelowRetired = Real with
        {
            ReleaseGate = Mutate(Real.ReleaseGate, RetiredHeader, RetiredHeader + "| INV-63 | RG-1 | 2026-01-01 |\n"),
            Contract = WithDecision(Real.Contract, "RG-1", "retirement", "INV-63"),
        };
        Assert.Contains(Evaluate(pinBelowRetired, GateMode.Development, Dev), f => f.Rule == "R3" && f.Message.Contains("below the highest retired ID"));
    }

    // ---- R4, R5, R6 --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("`LoginHardeningTests.A_cookie_carrying_a_synthetic_identity_is_rejected`", "`LoginHardeningTests.A_cookie_carrying_a_renamed_identity_is_rejected`")] // renamed method
    [InlineData("`LoginHardeningTests.A_cookie_carrying_a_synthetic_identity_is_rejected`", "`LoginHardeningRemovedTests`")]                                 // removed class
    [InlineData("`LoginHardeningTests.A_cookie_carrying_a_synthetic_identity_is_rejected`", "`PrincipalPropagationTest`")]                                   // typo
    [InlineData("`LoginHardeningTests.A_cookie_carrying_a_synthetic_identity_is_rejected`", "`GateFiles`")]                                                  // not a test class
    [InlineData("`LoginHardeningTests.A_cookie_carrying_a_synthetic_identity_is_rejected`", "`Program.cs`")]                                                 // a file name
    public void Every_backticked_name_in_the_test_column_resolves(string from, string to)
    {
        var mutated = Real with { Invariants = Mutate(Real.Invariants, from, to) };
        Assert.Contains(Evaluate(mutated, GateMode.Development, Dev), f => f.Rule == "R4" && f.Subject == "INV-03");
    }

    [Fact]
    public void A_green_row_needs_a_proven_test()
    {
        var planned = Real with { Invariants = SetCell(Real.Invariants, "| INV-03 |", 4, "`LoginHardeningTests.A_test_not_written_yet` *(planned, WP1)*") };
        Assert.Contains(Evaluate(planned, GateMode.Development, Dev), f => f.Rule == "R5" && f.Subject == "INV-03");

        // A Windows-only method allowed to skip in every CI job is no evidence.
        var platformOnly = Real with { Invariants = SetCell(Real.Invariants, "| INV-03 |", 4, "`PerHostCredentialTests.On_windows_a_key_readable_by_everyone_is_exposed`") };
        Assert.Contains(Evaluate(platformOnly, GateMode.Development, Dev), f => f.Rule == "R5" && f.Subject == "INV-03" && f.Message.Contains("proven"));
    }

    [Fact]
    public void A_planned_marker_must_name_the_owed_test()
    {
        // On a ⚠️ row (INV-01). INV-21's marker, the first of this kind, was paid with ReadOnlyKillSwitchWireTests.
        var existingClass = Real with { Invariants = AppendTest(Real.Invariants, "| INV-01 |", "`KillSwitchTests` *(planned, WP6)*") };
        Assert.Contains(Evaluate(existingClass, GateMode.Development, Dev), f => f.Rule == "R6" && f.Subject == "INV-01");

        var existingMethod = Real with { Invariants = AppendTest(Real.Invariants, "| INV-01 |",
            "`UiMutationRunnerTests.The_kill_switch_stops_a_human_too` *(planned, WP6)*") };
        Assert.Contains(Evaluate(existingMethod, GateMode.Development, Dev), f => f.Rule == "R6" && f.Subject == "INV-01");

        var nameless = Real with { Invariants = AppendTest(Real.Invariants, "| INV-01 |", "a wire test *(planned, WP6)*") };
        Assert.Contains(Evaluate(nameless, GateMode.Development, Dev), f => f.Rule == "R6" && f.Subject == "INV-01" && f.Message.Contains("only on a ❌ or ❓ row"));

        // The owed form passes: a class that does not exist yet, named; and today's table passes.
        var owed = Real with { Invariants = AppendTest(Real.Invariants, "| INV-01 |", "`PrincipalKeyWireTests` *(planned, R1-03)*") };
        Assert.DoesNotContain(Evaluate(owed, GateMode.Development, Dev), f => f.Rule == "R6");
        Assert.DoesNotContain(Evaluate(Real, GateMode.Development, Dev), f => f.Rule == "R6");

        // A ❌ row may name its owed test without a class: INV-18 as it stood before it was closed.
        var namelessOnViolated = Violated with
        {
            Invariants = SetCell(Violated.Invariants, "| INV-18 |", 4, "the in-handler checks of these actions *(planned, WP10)*"),
        };
        Assert.DoesNotContain(Evaluate(namelessOnViolated, GateMode.Development, Dev), f => f.Rule == "R6");
    }

    // ---- R7, R8, R9 --------------------------------------------------------------------------------------------

    [Fact]
    public void Skips_never_count_as_passes()
    {
        Assert.True(TestCatalog.Instance.IsStaticallySkipped(typeof(GateFixtures.SkipFixture).GetMethod(nameof(GateFixtures.SkipFixture.Skipped_fact))!));
        Assert.True(TestCatalog.Instance.IsStaticallySkipped(typeof(GateFixtures.SkipFixture).GetMethod(nameof(GateFixtures.SkipFixture.One_row_skipped))!));
        Assert.False(TestCatalog.Instance.IsStaticallySkipped(typeof(GateFixtures.CaseCountFixture).GetMethod(nameof(GateFixtures.CaseCountFixture.A_fact))!));

        foreach (var name in new[] { "SkipFixture.Skipped_fact", "SkipFixture.One_row_skipped" })
        {
            var named = Real with { Invariants = AppendTest(Real.Invariants, "| INV-03 |", $"`{name}`") };
            Assert.Contains(Evaluate(named, GateMode.Development, Dev), f => f.Rule == "R7" && f.Subject == "INV-03" && f.Message.Contains("statically skipped"));
        }

        // A runtime-skipping method without a Platform skips row is required like any other: CI fails when it skips.
        const string linuxOnly = "Whiskers.Tests.SecretEnvironmentTests";
        var blocks = ReleaseGate.RenderManifestBlocks(Real);
        Assert.Contains(blocks[ReleaseGate.InProcessManifest].Split('\n'), l => l.Contains($" {linuxOnly}.", StringComparison.Ordinal));

        // An allowed Windows-only method is left out of every manifest.
        const string windowsOnly = "PerHostCredentialTests.On_windows_a_key_readable_by_everyone_is_exposed";
        var namedWindows = Real with { Invariants = AppendTest(Real.Invariants, "| INV-03 |", $"`{windowsOnly}`") };
        Assert.All(ReleaseGate.RenderManifestBlocks(namedWindows).Values, b => Assert.DoesNotContain(windowsOnly, b));

        // A Platform skips row naming a real mandatory suite renders the method into that suite's manifest.
        var method = blocks[ReleaseGate.InProcessManifest].Split('\n').First(l => l.Contains($" {linuxOnly}.", StringComparison.Ordinal)).Split(' ')[1];
        var moved = Real with { ReleaseGate = Mutate(Real.ReleaseGate, SkipsHeader, SkipsHeader + $"| {method} | not in a container | ci.yml image-runtime / Image runtime tests | test |\n") };
        var movedBlocks = ReleaseGate.RenderManifestBlocks(moved);
        Assert.Contains(method, movedBlocks[ReleaseGate.ImageRuntimeManifest]);
        Assert.DoesNotContain(method + "\n", movedBlocks[ReleaseGate.InProcessManifest]);
        Assert.DoesNotContain(Evaluate(moved, GateMode.Development, Dev), f => f.Rule == "R7");

        // One naming a job that is not a mandatory suite with --require fails, and so does a stale row.
        var notASuite = Real with { ReleaseGate = Mutate(Real.ReleaseGate, SkipsHeader, SkipsHeader + $"| {method} | never | ci.yml artifact | test |\n") };
        Assert.Contains(Evaluate(notASuite, GateMode.Development, Dev), f => f.Rule == "R7" && f.Subject == method);
        var dockerStep = Real with { ReleaseGate = Mutate(Real.ReleaseGate, SkipsHeader, SkipsHeader + $"| {method} | never | ci.yml build-test / DI boot gate | test |\n") };
        Assert.Contains(Evaluate(dockerStep, GateMode.Development, Dev), f => f.Rule == "R7" && f.Subject == method);
        var stale = Real with { ReleaseGate = Mutate(Real.ReleaseGate, SkipsHeader, SkipsHeader + "| Whiskers.Tests.LoginHardeningTests.No_such_test | never | — | stale |\n") };
        Assert.Contains(Evaluate(stale, GateMode.Development, Dev), f => f.Rule == "R7" && f.Message.Contains("stale"));
    }

    [Fact]
    public void Every_named_test_is_required_per_method_in_its_ci_run()
    {
        var catalog = TestCatalog.Instance;
        int Cases(string method) => catalog.CaseCount(typeof(GateFixtures.CaseCountFixture), typeof(GateFixtures.CaseCountFixture).GetMethod(method)!);
        Assert.Equal(1, Cases(nameof(GateFixtures.CaseCountFixture.A_fact)));
        Assert.Equal(2, Cases(nameof(GateFixtures.CaseCountFixture.Two_inline_rows)));
        Assert.Equal(3, Cases(nameof(GateFixtures.CaseCountFixture.Three_member_rows)));
        Assert.Equal(1, Cases(nameof(GateFixtures.CaseCountFixture.Opaque_rows)));

        var blocks = ReleaseGate.RenderManifestBlocks(Real);
        // A class reference expands to one entry per test method (INV-06 names `PrincipalPropagationTests`).
        var methods = catalog.Resolve("PrincipalPropagationTests", null);
        Assert.True(methods.Count > 1);
        foreach (var (type, method) in methods)
            Assert.Contains($"{catalog.CaseCount(type, method)} {type.FullName}.{method.Name}\n", blocks[ReleaseGate.InProcessManifest]);
        // The Docker/in-process split.
        Assert.Contains("Whiskers.Tests.PostgresBackupRestoreTests.", blocks[ReleaseGate.DockerManifest]);
        Assert.DoesNotContain("Whiskers.Tests.PostgresBackupRestoreTests.", blocks[ReleaseGate.InProcessManifest]);
        // The generated blocks are the manifests' snapshot.
        foreach (var (manifest, block) in blocks)
            Assert.Equal(block, ReleaseGate.BlockOf(Real.Manifests[manifest]));
        var edited = Real with { Manifests = new Dictionary<string, string>(Real.Manifests) { [ReleaseGate.DockerManifest] = RemoveLine(Real.Manifests[ReleaseGate.DockerManifest], "1 Whiskers.Tests.PostgresBackupRestoreTests.A_dump_cut_off") } };
        Assert.Contains(Evaluate(edited, GateMode.Development, Dev), f => f.Rule == "R8" && f.Subject == ReleaseGate.DockerManifest);
    }

    [Fact]
    public void A_named_test_outside_every_checked_run_fails()
    {
        // An image-runtime test needs the image-runtime suite to check its manifest (D14).
        var imageNamed = Real with { Invariants = AppendTest(Real.Invariants, "| INV-03 |", "`ImageRuntimeToolsTests.Image_boots_and_answers_its_health_probe`") };
        Assert.DoesNotContain(Evaluate(imageNamed, GateMode.Development, Dev), f => f.Rule == "R9");
        Assert.Contains("ImageRuntimeToolsTests.Image_boots_and_answers_its_health_probe", ReleaseGate.RenderManifestBlocks(imageNamed)[ReleaseGate.ImageRuntimeManifest]);
        var unwired = imageNamed with { ReleaseGate = RemoveLine(imageNamed.ReleaseGate, "| ci.yml | image-runtime |") };
        Assert.Contains(Evaluate(unwired, GateMode.Development, Dev), f => f.Rule == "R9" && f.Message.Contains("image-runtime"));

        // A test no CI filter selects: a class xUnit's discovery does not run.
        var hidden = Real with { Invariants = AppendTest(Real.Invariants, "| INV-03 |", "`CaseCountFixture.A_fact`") };
        Assert.Contains(Evaluate(hidden, GateMode.Development, Dev), f => f.Rule == "R9" && f.Subject == "INV-03");
    }

    // ---- R10 ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ci.yml", "  image-runtime:\n    name: image-runtime", "  image-lane:\n    name: image-runtime")]                                            // job removed
    [InlineData("ci.yml", "      - name: Test (in-process)\n", "      - name: Test (in-process)\n        continue-on-error: true\n")]                          // may fail
    [InlineData("ci.yml", "--no-skips --require .github/required-tests/docker.txt", "--require .github/required-tests/docker.txt")]                                // skips pass
    [InlineData("ci.yml", " --require .github/required-tests/in-process.txt", "")]                                                                                  // no required tests
    [InlineData("ci.yml", "--require .github/required-tests/image-runtime.txt", "")]                                                                                 // image lane unchecked
    [InlineData("release.yml", "    needs: [prepare, test, security, build, scan, artifact-test]", "    needs: [prepare, test, security, build, artifact-test]")]  // scan dropped
    [InlineData("release.yml", "    needs: [prepare, test, security, build, scan, artifact-test]", "    needs: [prepare, test, build, scan, artifact-test]")]      // security dropped
    [InlineData("release.yml", "  security:\n    needs: prepare\n", "  security:\n    needs: prepare\n    if: false\n")]                                       // skipped
    // A step-level condition skips a mandatory step in the release run while the job still succeeds (review M4).
    [InlineData("ci.yml", "      - name: Test (in-process)\n", "      - name: Test (in-process)\n        if: inputs.release-gate != 'release'\n")]
    [InlineData("ci.yml", "      - name: Test (Docker integration, mandatory)\n", "      - name: Test (Docker integration, mandatory)\n        if: github.event_name != 'push'\n")]
    [InlineData("ci.yml", "      - name: Image runtime tests\n", "      - name: Image runtime tests\n        if: github.event_name == 'pull_request'\n")]
    [InlineData("ci.yml", "        if: github.event_name == 'pull_request' || inputs.release-gate == 'release'\n", "        if: github.event_name == 'pull_request'\n")] // base step: only the exact condition
    [InlineData("release.yml", "      - name: Trivy scan arm64 (gate, fail on CRITICAL)\n", "      - name: Trivy scan arm64 (gate, fail on CRITICAL)\n        if: false\n")]                                         // a step of a job row
    public void A_mandatory_suite_that_cannot_block_the_release_fails(string file, string from, string to)
    {
        var workflows = new Dictionary<string, string>(Real.Workflows) { [file] = Mutate(Real.Workflows[file], from, to) };
        Assert.Contains(Evaluate(Real with { Workflows = workflows }, GateMode.Development, Dev), f => f.Rule == "R10");
    }

    [Fact]
    public void The_release_run_alone_sets_the_mode_input()
    {
        Assert.DoesNotContain(Evaluate(Real, GateMode.Development, Dev), f => f.Rule == "R16");
        foreach (var (file, from, to) in new[]
        {
            ("release.yml", "      release-gate: release\n", ""),
            ("ci.yml", "          WHISKERS_RELEASE_GATE: ${{ inputs.release-gate }}\n", "          WHISKERS_RELEASE_GATE: release\n"),
            ("ci.yml", "      - name: Test (Docker integration, mandatory)\n", "      - name: Test (Docker integration, mandatory)\n        env:\n          WHISKERS_RELEASE_GATE: ${{ inputs.release-gate }}\n"),
            ("security.yml", "permissions: {}\n", "permissions: {}\n\nenv:\n  WHISKERS_RELEASE_GATE: dry-run\n"),
        })
        {
            var workflows = new Dictionary<string, string>(Real.Workflows) { [file] = Mutate(Real.Workflows[file], from, to) };
            Assert.Contains(Evaluate(Real with { Workflows = workflows }, GateMode.Development, Dev), f => f.Rule == "R16");
        }
    }

    // ---- R11 ---------------------------------------------------------------------------------------------------

    [Fact]
    public void An_exception_needs_an_accepted_dated_decision_and_a_limitation()
    {
        var valid = WithException(Real, "INV-01", "RG-1");
        Assert.DoesNotContain(Evaluate(valid, GateMode.Development, Dev), f => f.Rule == "R11");

        void Fails(GateInputs inputs, string because, GateVersion? version = null)
            => Assert.Contains(Evaluate(inputs, GateMode.Development, version ?? Dev), f => f.Rule == "R11" && f.Message.Contains(because));

        Fails(WithException(Real, "INV-01", "RG-1", decisionStatus: "Proposed"), "not Accepted");
        Fails(WithException(Real, "INV-01", "RG-1", date: ""), "no ISO date");
        Fails(WithException(Real, "INV-01", "RG-1", date: "2999-01-01"), "in the future");
        Fails(WithException(Real, "INV-01", "RG-1", decidedBy: "—"), "names no maintainer");
        Fails(WithException(Real, "INV-01", "RG-1", decisionKind: "retirement"), "not 'exception'");
        Fails(WithException(Real, "INV-01", "RG-1", decisionInvariant: "INV-13"), "not for INV-01");
        Fails(WithException(Real, "INV-01", "RG-1", link: "docs/operations/no-such-page.md#limits"), "does not exist");
        Fails(WithException(Real, "INV-01", "RG-1", link: LimitsDoc + "#no-such-heading"), "has no heading");
        Fails(WithException(Real, "INV-01", "RG-1", limitationText: "Principals are typed."), "does not name INV-01");
        Fails(WithException(Real, "INV-01", "RG-1", link: "docs/v1/WORK_PACKAGES.md#r1-51"), "must be in README.md");
        Fails(WithException(Violated, "INV-18", "RG-1", status: "❌"), "never accepted");
        Fails(WithException(Real, "INV-02", "RG-1", status: "✅"), "stale");
        Fails(WithException(Real, "INV-01", "RG-1", reviewBy: "0.15.0"), "is due");                 // in Development mode too
        Fails(WithException(Real, "INV-01", "RG-1", reviewBy: "1.0.0"), "is due", V100);
    }

    // ---- R17 ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_known_violation_covers_exactly_one_release()
    {
        var record = WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.1");
        Assert.DoesNotContain(Evaluate(record, GateMode.Release, V101), f => f.Rule is "R1" or "R17" && f.Subject == "INV-18");

        void Fails(GateInputs inputs, GateVersion version, string because)
            => Assert.Contains(Evaluate(inputs, GateMode.Release, version), f => f.Rule == "R17" && f.Subject == "INV-18" && f.Message.Contains(because));

        Fails(record, V102, "not the current version");                                                         // after the bump, in every mode
        Assert.Contains(Evaluate(record, GateMode.Development, V102), f => f.Rule == "R17");
        Assert.Contains(Evaluate(record, GateMode.Line, V102), f => f.Rule == "R17");
        Fails(WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.1", decisionRelease: "1.0.2"), V101, "covers release '1.0.2'");
        Fails(WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.1", reviewBy: "1.1.0"), V101, "next patch");
        // A renewal at 1.0.2 that changes only 'Review by': the record still covers 1.0.1, and so does its decision.
        var renewed = WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.1", reviewBy: "1.0.3");
        Fails(renewed, V102, "not the current version");
        Assert.Contains(Evaluate(renewed, GateMode.Release, V102), f => f.Rule == "R1" && f.Subject == "INV-18");
        // Changing 'Release' too does not help: the accepted decision still says 1.0.1 (and cannot be edited, R18).
        Fails(WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.2", decisionRelease: "1.0.1"), V102, "covers release '1.0.1'");
        Fails(WithKnownViolation(Violated, "INV-18", "RG-1", "1.1.0"), new GateVersion(1, 1, 0), "not a patch release");   // X.Y.0
        Fails(WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.0"), V100, "not a patch release");
        Fails(WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.1", limitationText: "No limitation is named here."), V101, "does not name INV-18");
        Fails(WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.1", advisory: "CVE-2026-1234"), V101, "is not GHSA");
        Fails(WithKnownViolation(Violated, "INV-18", "RG-1", "1.0.1", decisionKind: "exception"), V101, "not 'known violation'");
        Assert.Contains(Evaluate(WithKnownViolation(Violated, "INV-01", "RG-1", "1.0.1", status: "⚠️"), GateMode.Release, V101),
            f => f.Rule == "R17" && f.Subject == "INV-01" && f.Message.Contains("❌ or ❓ only"));
        Assert.Contains(Evaluate(WithKnownViolation(Violated, "INV-02", "RG-1", "1.0.1", status: "✅"), GateMode.Release, V101),
            f => f.Rule == "R17" && f.Subject == "INV-02");
    }

    // ---- R18, D16: against a base ------------------------------------------------------------------------------

    [Fact]
    public void An_accepted_decision_cannot_change()
    {
        const string csproj = "<Project><PropertyGroup><Version>0.15.0</Version></PropertyGroup></Project>";
        GateSnapshot Snapshot(GateInputs i, string? project = null) => new(project ?? csproj, i.Invariants, i.ReleaseGate, i.Contract);
        var withRg2 = Real with { Contract = WithDecision(Real.Contract, "RG-2", "exception", "INV-01") };
        var baseline = Snapshot(withRg2);
        Assert.Empty(BaseComparison.Compare(baseline, baseline, "the base"));

        // Edited in one cell, deleted, an ID reused, a new ID at or below the base's highest: all fail.
        var edited = Snapshot(Real with { Contract = WithDecision(Real.Contract, "RG-2", "exception", "INV-01", date: "2026-01-02") });
        Assert.Contains(BaseComparison.Compare(edited, baseline, "the base"), f => f.Rule == "R18" && f.Subject == "RG-2" && f.Message.Contains("never edited"));
        Assert.Contains(BaseComparison.Compare(Snapshot(Real), baseline, "the base"), f => f.Rule == "R18" && f.Subject == "RG-2" && f.Message.Contains("never deleted"));
        var reused = Snapshot(Real with { Contract = WithDecision(withRg2.Contract, "RG-1", "exception", "INV-13") });
        Assert.Contains(BaseComparison.Compare(reused, baseline, "the base"), f => f.Rule == "R18" && f.Subject == "RG-1");
        var added = Snapshot(Real with { Contract = WithDecision(withRg2.Contract, "RG-3", "exception", "INV-13") });
        Assert.Empty(BaseComparison.Compare(added, baseline, "the base"));

        // An invariant ID that disappears, and a pin that goes down.
        var deleted = Snapshot(Real with
        {
            Invariants = RemoveLine(Real.Invariants, "| INV-62 |"),
            ReleaseGate = Mutate(Real.ReleaseGate, "Highest invariant ID: INV-62", "Highest invariant ID: INV-61"),
        });
        var lost = BaseComparison.Compare(deleted, Snapshot(Real), "the base");
        Assert.Contains(lost, f => f.Rule == "D16" && f.Subject == "INV-62");
        Assert.Contains(lost, f => f.Rule == "D16" && f.Message.Contains("went down"));

        // The previous release tag: the highest vX.Y.Z strictly below the version, by the three numbers.
        string[] tags = ["v0.9.0-beta", "v0.10.0", "v0.15.0", "v0.9.0", "v1.0.1", "v1.1.0", "v1.0.2", "latest"];
        Assert.Equal("v1.0.1", BaseComparison.PreviousTag(tags, V102));
        Assert.Equal("v0.15.0", BaseComparison.PreviousTag(tags, V100));
        Assert.Equal("v1.0.2", BaseComparison.PreviousTag(tags, new GateVersion(1, 1, 0)));
        Assert.Null(BaseComparison.PreviousTag(["v0.9.0-beta", "latest"], V100));
        // The same checks against a previous-tag base: the base is just another snapshot.
        Assert.Contains(BaseComparison.Compare(edited, baseline, "the previous release tag v1.0.1"), f => f.Rule == "R18" && f.Subject == "RG-2");

        // Bootstrap: a base below 1.0.0 that predates the tables is empty for them.
        var predates = new GateSnapshot(csproj, Real.Invariants, null, Mutate(Real.Contract, "### 6.1 Release decisions", "### 6.1 (not yet)"));
        Assert.Empty(BaseComparison.Compare(Snapshot(Real), predates, "the base"));
        var noPinYet = new GateSnapshot(csproj, Real.Invariants, "# Release gate\n", Real.Contract);
        Assert.Empty(BaseComparison.Compare(Snapshot(Real), noPinYet, "the base"));
        // Amendment 1: a 0.x base without the files (v0.14.1, v0.15.0 have no docs/v1/) is an empty base.
        var bare = new GateSnapshot(csproj, null, null, null);
        Assert.Empty(BaseComparison.Compare(Snapshot(Real), bare, "the previous release v0.15.0", "v0.15.0"));
        Assert.Empty(BaseComparison.Compare(Snapshot(Real), predates with { Invariants = null }, "the base"));
        Assert.Empty(BaseComparison.Compare(Snapshot(Real), predates with { Contract = null }, "the base"));
        // From 1.0.0 on a missing file or table is a finding; so is a base without a csproj, or no base at all.
        const string one = "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>";
        GateSnapshot Current(GateInputs i) => Snapshot(i, "<Project><PropertyGroup><Version>1.0.1</Version></PropertyGroup></Project>");
        Assert.Contains(BaseComparison.Compare(Current(Real), bare with { Csproj = one }, "the base"), f => f.Rule == "D16" && f.Message.Contains("SECURITY_INVARIANTS.md"));
        Assert.Contains(BaseComparison.Compare(Current(Real), predates with { Csproj = one, Contract = null }, "the base"), f => f.Rule == "D16" && f.Message.Contains("PRODUCT_CONTRACT.md"));
        Assert.Contains(BaseComparison.Compare(Snapshot(Real), bare with { Csproj = null }, "the base"), f => f.Rule == "D16" && f.Message.Contains("csproj"));
        Assert.Contains(BaseComparison.Compare(Snapshot(Real), null, "the base"), f => f.Rule == "D16");
        var findings = BaseComparison.Compare(Current(Real), predates with { Csproj = one }, "the previous release v1.0.0", "v1.0.0");
        Assert.Contains(findings, f => f.Rule == "D16" && f.Message.Contains("no RELEASE_GATE.md"));
        Assert.Contains(findings, f => f.Rule == "R18" && f.Message.Contains("6.1"));
    }

    [Fact]
    public void A_pushed_tag_cannot_choose_or_fake_the_base()
    {
        // Review round 1 M3, round 2 m-B/m-D: the base is a published release in the release commit's history.
        var v120 = new GateVersion(1, 2, 0);
        string[] tags = ["v0.15.0", "v1.0.0", "v1.1.0", "v1.1.99", "latest"];
        string[] published = ["v0.15.0", "v1.0.0", "v1.1.0", "v1.2.0-rc1"];
        static GateVersion? Own(string t) => t == "v1.1.99" ? new GateVersion(0, 9, 0) : GateVersion.TryParse(t[1..]); // v1.1.99: a forged tag
        static bool All(string _) => true;

        var ok = BaseComparison.ChooseBase(tags, published, All, Own, v120);
        Assert.Equal(("v1.1.0", 0), (ok.Tag, ok.Problems.Count)); // the unpublished v1.1.99 carries another version: no release commit

        // Not an ancestor of the release commit.
        var stray = BaseComparison.ChooseBase(tags, published, t => t != "v1.1.0", Own, v120);
        Assert.Contains(stray.Problems, p => p.Contains("v1.1.0 is not an ancestor"));
        // The release list is missing, or empty.
        var missing = BaseComparison.ChooseBase(tags, null, All, Own, v120);
        Assert.Null(missing.Tag);
        Assert.Contains(missing.Problems, p => p.Contains("no list of published releases"));
        var empty = BaseComparison.ChooseBase(tags, [], All, Own, v120);
        Assert.Null(empty.Tag);
        Assert.Contains(empty.Problems, p => p.Contains("no published release below 1.2.0"));

        // A release deleted or demoted to a pre-release: the base would fall back past a real release commit.
        var fallback = BaseComparison.ChooseBase(tags, ["v0.15.0", "v1.0.0"], All, Own, v120);
        Assert.Equal("v1.0.0", fallback.Tag);
        Assert.Contains(fallback.Problems, p => p.StartsWith("v1.1.0 is a release commit", StringComparison.Ordinal));
        // ...but not a tag outside the history, or one whose commit carries another version (a failed or forged tag).
        Assert.Empty(BaseComparison.ChooseBase(tags, ["v0.15.0", "v1.0.0"], t => t != "v1.1.0", Own, v120).Problems.Where(p => p.StartsWith("v1.1.0", StringComparison.Ordinal)));
        Assert.Empty(BaseComparison.ChooseBase(tags, ["v0.15.0", "v1.0.0"], All, t => t == "v1.1.0" ? null : Own(t), v120).Problems);
        // A csproj that exists but cannot be read is a problem, not "no release commit" (review round 3, m-1).
        var unreadable = BaseComparison.ChooseBase(tags, ["v0.15.0", "v1.0.0"], All,
            t => t == "v1.1.0" ? throw new FormatException("no <Version> element") : Own(t), v120);
        Assert.Contains(unreadable.Problems, p => p.StartsWith("v1.1.0 is in the release commit's history", StringComparison.Ordinal) && p.Contains("cannot be read"));

        // A tag whose commit does not carry the tag's version is no release commit: refused, never a bootstrap base.
        const string old = "<Project><PropertyGroup><Version>0.9.0</Version></PropertyGroup></Project>";
        const string current = "<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>";
        var now = new GateSnapshot(current, Real.Invariants, Real.ReleaseGate, Real.Contract);
        var fake = BaseComparison.Compare(now, new GateSnapshot(old, null, null, null), "the previous release v1.1.99", "v1.1.99");
        Assert.Contains(fake, f => f.Rule == "D16" && f.Message.Contains("not a release commit"));
        Assert.Empty(BaseComparison.Compare(now, new GateSnapshot(old, null, null, null), "the base"));
    }

    [Fact]
    public void The_version_never_goes_down_against_the_base()
    {
        // Review m1: lowering <Version> would drop the gate's mode (D2).
        static string Project(string v) => $"<Project><PropertyGroup><Version>{v}</Version></PropertyGroup></Project>";
        GateSnapshot At(string v) => new(Project(v), Real.Invariants, Real.ReleaseGate, Real.Contract);
        Assert.Contains(BaseComparison.Compare(At("0.99.0"), At("1.0.3"), "the base"), f => f.Rule == "D2" && f.Message.Contains("never goes down"));
        Assert.DoesNotContain(BaseComparison.Compare(At("1.0.3"), At("1.0.3"), "the base"), f => f.Rule == "D2");
        Assert.DoesNotContain(BaseComparison.Compare(At("1.1.0"), At("1.0.3"), "the base"), f => f.Rule == "D2");
        // No current csproj: the comparison fails on its own instead of skipping the check (review round 2 NIT).
        Assert.Contains(BaseComparison.Compare(At("1.0.3") with { Csproj = null }, At("1.0.3"), "the base"), f => f.Rule == "D2" && f.Message.Contains("no src/Whiskers/Whiskers.csproj"));
    }

    [Fact]
    public void A_malformed_table_in_the_base_is_a_finding_not_an_empty_base()
    {
        // Review m2: a base table that is present is read strictly; only an absent one in a 0.x base counts as empty.
        static string Project(string v) => $"<Project><PropertyGroup><Version>{v}</Version></PropertyGroup></Project>";
        var withRetired = Mutate(Real.ReleaseGate, "| Invariant | Decision | Date |\n|---|---|---|\n", "| Invariant | Decision | Date |\n|---|---|---|\n| INV-99 | RG-1 |\n");
        foreach (var v in new[] { "0.15.0", "1.0.0" })
        {
            var current = new GateSnapshot(Project(v), Real.Invariants, Real.ReleaseGate, Real.Contract);
            var brokenGate = current with { ReleaseGate = withRetired };
            Assert.Contains(BaseComparison.Compare(current, brokenGate, "the base"), f => f.Rule == "D16" && f.Message.Contains("RELEASE_GATE.md"));
            var brokenDecisions = current with { Contract = Real.Contract.Replace("| ID | Kind | Invariant | Decision | Release | Status | Date | Decided by |",
                "| ID | Kind | Invariant | Decision | Status | Date | Decided by | Notes |") };
            Assert.Contains(BaseComparison.Compare(current, brokenDecisions, "the base"), f => f.Rule == "R18");
        }
    }

    // ---- R12–R15 -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_capability_inventory_matches_the_code()
    {
        var expected = RenderInventory(Real);
        var actual = Real.CapabilityInventory;
        if (actual is null || actual.Replace("\r\n", "\n").TrimEnd() != expected.TrimEnd())
        {
            File.WriteAllText(GateFiles.Full(GateFiles.Inventory) + ".actual", expected);
            Assert.Fail($"{GateFiles.Inventory} differs from the capability catalog; the fresh render was written to {GateFiles.Inventory}.actual — " +
                        "diff it, and if the change is intended, replace the file with it in the same commit.");
        }
        var changed = Real with { CapabilityInventory = Real.CapabilityInventory!.Replace("| `container.start` | write |", "| `container.start` | admin |") };
        Assert.Contains(Evaluate(changed, GateMode.Development, Dev), f => f.Rule == "R12");
    }

    [Fact]
    public void Matrix_and_catalog_name_each_other()
    {
        Assert.DoesNotContain(Evaluate(Real, GateMode.Development, Dev), f => f.Rule is "R13" or "R14");

        // A capability no row names (its row's line dropped), a row without a line.
        var unnamed = Real with { PathCapabilities = RemoveLine(Real.PathCapabilities!, "| U-38 |") };
        var findings = Evaluate(unnamed, GateMode.Development, Dev);
        Assert.Contains(findings, f => f.Rule == "R13" && f.Subject == "endpointcheck.save");
        Assert.Contains(findings, f => f.Rule == "R13" && f.Message.Contains("no line for §7 row U-38"));

        // A new MCP tool without an M row.
        var noRow = Real with { Contract = RemoveLine(Real.Contract, "| M-49 |") };
        Assert.Contains(Evaluate(noRow, GateMode.Development, Dev), f => f.Rule == "R13" && f.Subject == "mtls.client_renew");

        // A row naming an unknown capability or tool.
        var unknownCapability = Real with { PathCapabilities = SetCell(Real.PathCapabilities!, "| U-17 |", 1, "nginx.reload, nginx.restart") };
        Assert.Contains(Evaluate(unknownCapability, GateMode.Development, Dev), f => f.Rule == "R14" && f.Subject == "U-17");
        var unknownTool = Real with { Contract = Mutate(Real.Contract, "| M-38 | `pause_server_checks` |", "| M-38 | `pause_all_checks` |") };
        Assert.Contains(Evaluate(unknownTool, GateMode.Development, Dev), f => f.Rule == "R14" && f.Subject == "M-38");
        var readNotNamed = Real with { Contract = Mutate(Real.Contract, "| M-44 | `get_git_deploy_app` (contract 0001) | read |", "| M-44 | `get_git_deploy_app` (contract 0001) | admin |") };
        Assert.Contains(Evaluate(readNotNamed, GateMode.Development, Dev), f => f.Rule == "R14" && f.Subject == "M-44");
    }

    [Fact]
    public void The_totals_match_the_rows()
    {
        Assert.DoesNotContain(Evaluate(Real, GateMode.Development, Dev), f => f.Rule == "R15");
        var count = Real with { Contract = Mutate(Real.Contract, "| UI handler groups (U) | 40 |", "| UI handler groups (U) | 39 |") };
        Assert.Contains(Evaluate(count, GateMode.Development, Dev), f => f.Rule == "R15" && f.Subject.Contains("(U)"));
        var row = Real with { Contract = RemoveLine(Real.Contract, "| T-03 |") };
        var findings = Evaluate(row, GateMode.Development, Dev);
        Assert.Contains(findings, f => f.Rule == "R15" && f.Subject.Contains("(T)"));
        Assert.Contains(findings, f => f.Rule == "R15" && f.Subject.Contains("(Total)"));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    internal static IReadOnlyList<GateFinding> Evaluate(GateInputs inputs, GateMode mode, GateVersion version)
        => ReleaseGate.Evaluate(inputs, mode, version);

    private static string RenderInventory(GateInputs inputs)
        => CapabilityInventoryRenderer.Render(inputs.Capabilities, PathMatrixParser.Parse(inputs.Contract, inputs.PathCapabilities),
            inputs.Surfaces, inputs.Compositions);

    internal static string Mutate(string text, string from, string to)
    {
        var index = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(index >= 0, $"mutation anchor not found: {from}");
        return text[..index] + to + text[(index + from.Length)..];
    }

    internal static string LineStartingWith(string text, string start)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith(start, StringComparison.Ordinal));
        Assert.True(line is not null, $"no line starts with {start}");
        return line!;
    }

    internal static string RemoveLine(string text, string start) => Mutate(text, LineStartingWith(text, start) + "\n", "");

    internal static string InsertAfter(string text, string start, string line)
    {
        var existing = LineStartingWith(text, start);
        return Mutate(text, existing + "\n", existing + "\n" + line + "\n");
    }

    /// <summary>The table line that starts with <paramref name="start"/>, with cell <paramref name="index"/> replaced.</summary>
    internal static string SetCell(string text, string start, int index, string value)
    {
        var line = LineStartingWith(text, start);
        var cells = MarkdownTables.Split(line).Select(c => c.Replace("|", "\\|")).ToArray();
        cells[index] = value;
        return Mutate(text, line + "\n", "| " + string.Join(" | ", cells) + " |\n");
    }

    private static string AppendTest(string invariants, string row, string test)
    {
        var line = LineStartingWith(invariants, row);
        var cells = MarkdownTables.Split(line).Select(c => c.Replace("|", "\\|")).ToArray();
        cells[4] = cells[4] + ", " + test;
        return Mutate(invariants, line + "\n", "| " + string.Join(" | ", cells) + " |\n");
    }

    internal static string WithDecision(string contract, string id, string kind, string invariant, string? release = null,
        string status = "Accepted", string date = "2026-01-01", string decidedBy = "the maintainer")
        => Mutate(contract, DecisionsHeader, DecisionsHeader + $"| {id} | {kind} | {invariant} | test decision | {release ?? "—"} | {status} | {date} | {decidedBy} |\n");

    private static Func<string, string?> WithLimits(Func<string, string?> read, string invariant, string? text)
        => path => path == LimitsDoc ? $"# Test limits\n\n## Accepted limits (test)\n\n{text ?? $"{invariant} and the other recorded gaps (INV-01, INV-02, INV-13, INV-18) are open in this release; operators must know."}\n\n## Other\n\nNothing.\n" : read(path);

    private static GateInputs WithException(GateInputs inputs, string invariant, string decision, string status = "⚠️",
        string decisionStatus = "Accepted", string date = "2026-01-01", string decidedBy = "the maintainer", string decisionKind = "exception",
        string? decisionInvariant = null, string link = LimitsDoc + "#accepted-limits-test", string? limitationText = null, string reviewBy = "9.0.0")
        => inputs with
        {
            ReleaseGate = Mutate(inputs.ReleaseGate, ExceptionsHeader, ExceptionsHeader + $"| {invariant} | {status} | {decision} | {link} | {reviewBy} |\n"),
            Contract = WithDecision(inputs.Contract, decision, decisionKind, decisionInvariant ?? invariant, null, decisionStatus, date, decidedBy),
            ReadDocument = WithLimits(inputs.ReadDocument, invariant, limitationText),
        };

    private static GateInputs WithKnownViolation(GateInputs inputs, string invariant, string decision, string release, string status = "❌",
        string? decisionRelease = null, string? reviewBy = null, string advisory = "GHSA-2c4m-59x8-f3xq", string decisionKind = "known violation",
        string? limitationText = null)
    {
        var next = GateVersion.TryParse(release)!.NextPatch().ToString();
        return inputs with
        {
            ReleaseGate = Mutate(inputs.ReleaseGate, KnownHeader,
                KnownHeader + $"| {invariant} | {status} | {decision} | {advisory} | {LimitsDoc}#accepted-limits-test | {release} | {reviewBy ?? next} |\n"),
            Contract = WithDecision(inputs.Contract, decision, decisionKind, invariant, decisionKind == "known violation" ? decisionRelease ?? release : null),
            ReadDocument = WithLimits(inputs.ReadDocument, invariant, limitationText),
        };
    }
}
