using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>
/// The mechanical release gate of contract 0008 (R1-51). <see cref="Evaluate"/> is pure: it reads only
/// <see cref="GateInputs"/> and returns every finding, so each rule is tested on mutated copies of the real files.
/// </summary>
public static class ReleaseGate
{
    public const string ModeVariable = "WHISKERS_RELEASE_GATE";
    public const string InProcessManifest = "in-process.txt";
    public const string DockerManifest = "docker.txt";
    public const string ImageRuntimeManifest = "image-runtime.txt";
    public const string BlockBegin = "# BEGIN release-gate (generated)";
    public const string BlockEnd = "# END release-gate";

    private static readonly Regex InvariantId = new(@"^INV-(\d{2,})$", RegexOptions.CultureInvariant);
    private static readonly Regex InvariantInCell = new(@"INV-\d+", RegexOptions.CultureInvariant);
    private static readonly Regex DecisionId = new(@"^RG-([1-9]\d*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex Ghsa = new(@"^GHSA(-[23456789cfghjmpqrvwx]{4}){3}$", RegexOptions.CultureInvariant);
    private static readonly Regex IssueUrl = new(@"^https://github\.com/LupusMalusDeviant/Whiskers/issues/[1-9]\d*$", RegexOptions.CultureInvariant);

    // ---- version and mode ------------------------------------------------------------------------------------

    /// <summary>The one <c>&lt;Version&gt;</c> of Whiskers.csproj as X.Y.Z; anything else throws (R16).</summary>
    public static GateVersion ReadVersion(string csproj)
    {
        var matches = Regex.Matches(csproj ?? "", @"<Version>([^<]*)</Version>");
        if (matches.Count != 1)
            throw new FormatException($"Whiskers.csproj must hold exactly one <Version>, it holds {matches.Count}");
        return GateVersion.TryParse(matches[0].Groups[1].Value.Trim())
               ?? throw new FormatException($"<Version>{matches[0].Groups[1].Value}</Version> is not X.Y.Z");
    }

    /// <summary>
    /// Below 1.0.0 Development, from 1.0.0 Line; <c>release</c> at 1.0.0 or later and <c>dry-run</c> at any version
    /// Release. Never lower than the version alone gives; any other non-empty value throws (a finding, R16).
    /// </summary>
    public static GateMode Mode(GateVersion version, string? environmentValue)
    {
        var byVersion = version >= GateVersion.One ? GateMode.Line : GateMode.Development;
        return environmentValue switch
        {
            null or "" => byVersion,
            "release" => byVersion == GateMode.Line ? GateMode.Release : byVersion,
            "dry-run" => GateMode.Release,
            _ => throw new ArgumentException($"{ModeVariable}='{environmentValue}' is not an accepted value (unset, empty, 'release' or 'dry-run')"),
        };
    }

    // ---- evaluation ------------------------------------------------------------------------------------------

    public static IReadOnlyList<GateFinding> Evaluate(GateInputs inputs, GateMode mode, GateVersion version)
    {
        var e = new Evaluation(inputs, mode, version);
        e.Run();
        return e.Findings;
    }

    /// <summary>The generated manifest blocks (R8): manifest file name → block text, markers included.</summary>
    /// <remarks>Rendered at the version the gate evaluates (review NIT): an exception expired at that version is out of
    /// the block here exactly as it is in <see cref="Evaluate"/>, so the .actual file can match.</remarks>
    public static IReadOnlyDictionary<string, string> RenderManifestBlocks(GateInputs inputs, GateVersion? version = null)
    {
        var e = new Evaluation(inputs, GateMode.Development, version ?? new GateVersion(0, 0, 0));
        e.Prepare();
        return e.RenderBlocks();
    }

    /// <summary>The manifest text with its generated block replaced by <paramref name="block"/> (or appended).</summary>
    public static string WithBlock(string manifest, string block)
    {
        var text = manifest.Replace("\r\n", "\n");
        var begin = text.IndexOf(BlockBegin, StringComparison.Ordinal);
        var end = text.IndexOf(BlockEnd, StringComparison.Ordinal);
        if (begin < 0 || end < begin) return text.TrimEnd('\n') + "\n\n" + block;
        var after = text.IndexOf('\n', end);
        return text[..begin] + block + (after < 0 ? "" : text[(after + 1)..]);
    }

    /// <summary>The generated block of a manifest (markers included), or null when it has none.</summary>
    public static string? BlockOf(string manifest)
    {
        var text = manifest.Replace("\r\n", "\n");
        var begin = text.IndexOf(BlockBegin, StringComparison.Ordinal);
        var end = text.IndexOf(BlockEnd, StringComparison.Ordinal);
        if (begin < 0 || end < begin) return null;
        if (text.IndexOf(BlockBegin, begin + 1, StringComparison.Ordinal) >= 0) return null;
        var after = text.IndexOf('\n', end);
        return after < 0 ? text[begin..] + "\n" : text[begin..(after + 1)];
    }

    /// <summary>The manifest a run's tests are required in.</summary>
    public static string? ManifestOf(TestRun run) => run switch
    {
        TestRun.InProcess => InProcessManifest,
        TestRun.Docker => DockerManifest,
        TestRun.ImageRuntime => ImageRuntimeManifest,
        _ => null,
    };

    public static bool IsAdvisory(string? text) => text is not null && Ghsa.IsMatch(text);
    public static bool IsReference(string? text) => text is not null && (Ghsa.IsMatch(text) || IssueUrl.IsMatch(text));

    /// <summary>GitHub's heading anchor: lower case, punctuation dropped, spaces to hyphens.</summary>
    public static string Slug(string heading)
    {
        var text = heading.Trim().TrimStart('#').Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
            else if (ch == ' ') sb.Append('-');
        }
        return sb.ToString();
    }

    private sealed class Evaluation(GateInputs inputs, GateMode mode, GateVersion version)
    {
        public List<GateFinding> Findings { get; } = [];

        private List<InvariantRow> _rows = [];
        private ReleaseGateTables _gate = new();
        private List<ReleaseDecision> _decisions = [];
        private List<PathRow> _paths = [];
        private readonly Dictionary<string, Dictionary<string, YamlDotNet.RepresentationModel.YamlMappingNode>> _jobs = new();
        // Every resolved, non-planned named test: invariant id → methods.
        private readonly Dictionary<string, List<(Type Type, MethodInfo Method)>> _named = new(StringComparer.Ordinal);

        private void Add(string rule, string subject, string message) => Findings.Add(new(rule, subject, message));

        public void Prepare()
        {
            try { _rows = InvariantTableParser.Parse(inputs.Invariants).ToList(); }
            catch (FormatException ex) { Add("R3", "SECURITY_INVARIANTS.md", ex.Message + " (a '|' inside a cell, also inside backticks, is written '\\|')"); }
            _gate = ReleaseGateDocument.Parse(inputs.ReleaseGate);
            var (decisions, problem) = ReleaseDecisions.Parse(inputs.Contract);
            _decisions = decisions.ToList();
            if (problem is not null) Add("R11", "PRODUCT_CONTRACT.md §6.1", problem);
            foreach (var (file, text) in inputs.Workflows)
            {
                try { _jobs[file] = Workflow.Jobs(text); }
                catch (Exception ex) { Add("R10", file, $"cannot be read as YAML: {ex.Message}"); }
            }
            foreach (var row in _rows)
            {
                var methods = new List<(Type, MethodInfo)>();
                foreach (var t in row.Tests.Where(t => !t.Planned && t.Class is not null))
                    methods.AddRange(ResolveReference(t));
                _named[row.Id] = methods;
            }
            // Contract 0011 (Q3): every test a registered claim names is required like a ✅ invariant's. The register's own
            // rules (rows, markers, R4/R7 of its test cells) are ClaimRegisterTests'; here the claim ids only join R7–R9.
            foreach (var (id, tests) in Whiskers.Tests.Claims.ClaimRegister.TestsOf(inputs.Claims))
            {
                if (_named.ContainsKey(id)) continue;
                _named[id] = tests.Where(t => !t.Planned && t.Class is not null).SelectMany(ResolveReference).ToList();
                _claims.Add(id);
            }
        }

        private readonly HashSet<string> _claims = new(StringComparer.Ordinal);

        public void Run()
        {
            Prepare();
            foreach (var p in _gate.Problems) Add("R3", "RELEASE_GATE.md", p);
            Status();
            Format();
            NamedTests();
            Skips();
            Manifests();
            Suites();
            Decisions();
            Exceptions();
            KnownViolations();
            Inventory();
            ModeInput();
        }

        // ---- R1, R2 ----

        private void Status()
        {
            var open = _gate.Open.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
            var excepted = ValidExceptions().ToHashSet(StringComparer.Ordinal);
            foreach (var row in _rows)
            {
                if (row.Status is null) continue; // R3 reports the cell
                if (mode == GateMode.Release)
                {
                    if (row.Status == InvariantStatus.Holds) continue;
                    if (row.Status == InvariantStatus.Partial && excepted.Contains(row.Id)) continue;
                    if (row.Status is InvariantStatus.Violated or InvariantStatus.Unverified && ValidKnownViolations().Contains(row.Id)) continue;
                    Add("R1", row.Id, row.Status == InvariantStatus.Partial
                        ? $"is ⚠️ without an accepted exception (SECURITY_INVARIANTS.md:{row.Line}); a release needs ✅ or an accepted exception (R11)"
                        : $"is {InvariantTableParser.Symbol(row.Status.Value)} (SECURITY_INVARIANTS.md:{row.Line}); a release needs ✅ — no exception is accepted for ❌ or ❓ (D11), only a known-violation record for exactly this patch release (R17)");
                    continue;
                }
                if (row.Status == InvariantStatus.Holds) continue;
                if (!open.TryGetValue(row.Id, out var o))
                {
                    Add("R2", row.Id, $"is {row.StatusCell} (SECURITY_INVARIANTS.md:{row.Line}) but has no row under '{ReleaseGateDocument.OpenHeading}' in RELEASE_GATE.md");
                    continue;
                }
                if (o.Status != row.Status) Add("R2", row.Id, $"RELEASE_GATE.md:{o.Line} lists it as '{(o.Status is null ? "?" : InvariantTableParser.Symbol(o.Status.Value))}', the table says {row.StatusCell}");
                if (!string.Equals(o.Package, row.Package, StringComparison.Ordinal)) Add("R2", row.Id, $"RELEASE_GATE.md:{o.Line} names package '{o.Package}', the table says '{row.Package}'");
                if (mode == GateMode.Line && !excepted.Contains(row.Id) && !IsReference(o.Reference))
                    Add("R2", row.Id, $"RELEASE_GATE.md:{o.Line}: from 1.0.0 on a non-✅ invariant without an accepted exception needs a reference (GHSA-xxxx-xxxx-xxxx or an issue URL of this repository), not '{o.Reference ?? "—"}'");
            }
            var statuses = _rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Status, StringComparer.Ordinal);
            foreach (var o in _gate.Open)
            {
                if (!statuses.TryGetValue(o.Id, out var s)) Add("R2", o.Id, $"RELEASE_GATE.md:{o.Line} lists an invariant the table does not have");
                else if (s == InvariantStatus.Holds) Add("R2", o.Id, $"RELEASE_GATE.md:{o.Line} lists it as open, but it is ✅ now: remove the row (the list only shrinks honestly)");
            }
            foreach (var dup in _gate.Open.GroupBy(o => o.Id).Where(g => g.Count() > 1))
                Add("R2", dup.Key, "is listed twice under 'Open invariants'");
        }

        // ---- R3 ----

        private void Format()
        {
            if (_rows.Count == 0) { Add("R3", "SECURITY_INVARIANTS.md", "no invariant table found"); return; }
            foreach (var dup in _rows.GroupBy(r => r.Id).Where(g => g.Count() > 1))
                Add("R3", dup.Key, $"appears {dup.Count()} times (lines {string.Join(", ", dup.Select(r => r.Line))})");
            foreach (var row in _rows)
            {
                if (!InvariantId.IsMatch(row.Id)) Add("R3", row.Id, $"SECURITY_INVARIANTS.md:{row.Line}: not an invariant id INV-nn");
                if (row.Status is null)
                    Add("R3", row.Id, $"SECURITY_INVARIANTS.md:{row.Line}: the status cell '{row.StatusCell}' is not one bare symbol (✅, ⚠️, ❌ or ❓); a qualification belongs in the evidence column, and a partial ✅ is ⚠️");
            }

            // §11: the counts equal the table's.
            try
            {
                var counts = MarkdownTables.After(inputs.Invariants, "## 11. Count");
                var expected = new Dictionary<string, int>
                {
                    ["✅"] = _rows.Count(r => r.Status == InvariantStatus.Holds),
                    ["⚠️"] = _rows.Count(r => r.Status == InvariantStatus.Partial),
                    ["❌"] = _rows.Count(r => r.Status == InvariantStatus.Violated),
                    ["❓"] = _rows.Count(r => r.Status == InvariantStatus.Unverified),
                    ["Total"] = _rows.Count,
                };
                foreach (var (line, cells) in counts)
                {
                    var key = cells[0].Contains("Total", StringComparison.Ordinal) ? "Total"
                        : InvariantTableParser.ParseStatus(cells[0]) is { } s ? InvariantTableParser.Symbol(s) : null;
                    var n = Regex.Match(cells[1], @"^\**(\d+)");
                    if (key is null || !n.Success) { Add("R3", "SECURITY_INVARIANTS.md §11", $"line {line}: '{string.Join(" | ", cells)}' is not a count row"); continue; }
                    if (!expected.TryGetValue(key, out var count)) { Add("R3", "SECURITY_INVARIANTS.md §11", $"line {line}: {key} is counted twice"); continue; }
                    if (int.Parse(n.Groups[1].Value) != count)
                        Add("R3", "SECURITY_INVARIANTS.md §11", $"line {line}: {key} is {n.Groups[1].Value}, the table has {count}");
                    expected.Remove(key);
                }
                foreach (var missing in expected.Keys) Add("R3", "SECURITY_INVARIANTS.md §11", $"has no count for {missing}");
            }
            catch (FormatException ex) { Add("R3", "SECURITY_INVARIANTS.md §11", ex.Message); }

            // The pin and the retired IDs.
            var pin = _gate.Pin is { } p && InvariantId.Match(p) is { Success: true } pm ? int.Parse(pm.Groups[1].Value) : (int?)null;
            if (_gate.Pin is not null && pin is null) Add("R3", "RELEASE_GATE.md", $"line {_gate.PinLine}: '{_gate.Pin}' is not an invariant id");
            var tableIds = _rows.Select(r => InvariantId.Match(r.Id)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();

            // Every line that looks like an invariant row must be a parsed row (review round 4 m-1): a table whose header
            // the parser does not recognise (a typo, under a new heading) would otherwise hide its rows, a ❌ among them,
            // and with the pin and §11 left alone nothing else notices. Any line outside a fence that starts with '|' (after
            // indentation, as the reader and GitHub accept) and names an INV-n in its first cell is meant (review round 5 m-2).
            if (_rows.Count > 0)
            {
                var parsedLines = _rows.Select(r => r.Line).ToHashSet();
                var lines = MarkdownTables.Lines(inputs.Invariants);
                var fenced = MarkdownTables.Fenced(lines);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (fenced[i] || parsedLines.Contains(i + 1) || !lines[i].TrimStart().StartsWith('|')) continue;
                    if (InvariantInCell.Match(MarkdownTables.Split(lines[i])[0]) is { Success: true } id)
                        Add("R3", id.Value, $"SECURITY_INVARIANTS.md:{i + 1}: an invariant row the gate cannot read (its table's header is not '| {string.Join(" | ", InvariantTableParser.Header)} |')");
                }
            }
            var retired = new HashSet<int>();
            foreach (var r in _gate.Retired)
            {
                var m = InvariantId.Match(r.Id);
                if (!m.Success) { Add("R3", r.Id, $"RELEASE_GATE.md:{r.Line}: not an invariant id"); continue; }
                var n = int.Parse(m.Groups[1].Value);
                if (!retired.Add(n)) Add("R3", r.Id, $"RELEASE_GATE.md:{r.Line}: retired twice");
                if (tableIds.Contains(n)) Add("R3", r.Id, $"RELEASE_GATE.md:{r.Line}: retired and still a row of the table; a retired ID is never reused");
                if (r.Date is null) Add("R3", r.Id, $"RELEASE_GATE.md:{r.Line}: the date is not an ISO date");
                foreach (var problem in DecisionProblems(r.DecisionId, "retirement", r.Id, null))
                    Add("R3", r.Id, $"RELEASE_GATE.md:{r.Line}: {problem}");
            }
            if (pin is { } highest)
            {
                for (var n = 1; n <= highest; n++)
                    if (!tableIds.Contains(n) && !retired.Contains(n))
                        Add("R3", $"INV-{n:D2}", $"is at or below the pinned highest ID INV-{highest:D2} but is neither a row of the table nor retired: a deleted invariant needs a retirement decision");
                foreach (var above in tableIds.Where(n => n > highest))
                    Add("R3", $"INV-{above:D2}", $"is above the pinned highest ID INV-{highest:D2}: raise the pin in RELEASE_GATE.md in the same pull request");
                if (retired.Count > 0 && retired.Max() > highest)
                    Add("R3", "RELEASE_GATE.md", $"the pin INV-{highest:D2} is below the highest retired ID INV-{retired.Max():D2}");
            }
        }

        // ---- R4, R5, R6 ----

        private IEnumerable<(Type Type, MethodInfo Method)> ResolveReference(TestReference t)
        {
            if (t.Class is null) return [];
            // A full class name without a method ("Whiskers.Tests.FooTests") splits like Class.Method; prefer the class.
            if (t.Method is not null && inputs.Tests.TypeExists(t.Raw)) return inputs.Tests.Resolve(t.Raw, null);
            return inputs.Tests.Resolve(t.Class, t.Method);
        }

        private void NamedTests()
        {
            foreach (var row in _rows)
            {
                foreach (var t in row.Tests)
                {
                    if (t.Class is null)
                    {
                        if (row.Status is not (InvariantStatus.Violated or InvariantStatus.Unverified))
                            Add("R6", row.Id, $"SECURITY_INVARIANTS.md:{row.Line}: a planned entry without a test name ('{t.Raw}') is allowed only on a ❌ or ❓ row; name the owed `Class.Method`");
                        continue;
                    }
                    if (!Identifier.IsMatch(t.Raw))
                    {
                        Add("R4", row.Id, $"SECURITY_INVARIANTS.md:{row.Line}: `{t.Raw}` is not a test name; backticks in the test column hold `Class` or `Class.Method` only");
                        continue;
                    }
                    var resolved = ResolveReference(t).ToList();
                    if (t.Planned)
                    {
                        if (t.Method is not null ? resolved.Count > 0 : inputs.Tests.TypeExists(t.Class))
                            Add("R6", row.Id, $"SECURITY_INVARIANTS.md:{row.Line}: the planned entry `{t.Raw}` names a test that exists; name the test that is still owed, or drop the marker if the debt is paid");
                        continue;
                    }
                    if (resolved.Count == 0)
                    {
                        var what = t.Method is null
                            ? inputs.Tests.TypeExists(t.Class) ? "is not a test class (or its name is ambiguous)" : "names no class of the test assembly"
                            : inputs.Tests.TypeExists(t.Class) ? $"names no test method '{t.Method}' of {t.Class}" : "names no class of the test assembly";
                        Add("R4", row.Id, $"SECURITY_INVARIANTS.md:{row.Line}: `{t.Raw}` {what}");
                    }
                }
                if (row.Status == InvariantStatus.Holds)
                {
                    if (row.Tests.Any(t => t.Planned))
                        Add("R5", row.Id, $"SECURITY_INVARIANTS.md:{row.Line}: a ✅ row names a planned test; ✅ needs every named test to exist");
                    if (!_named[row.Id].Any(m => IsProven(m.Type, m.Method)))
                        Add("R5", row.Id, $"SECURITY_INVARIANTS.md:{row.Line}: a ✅ row needs at least one named test that exists and is proven in a CI run");
                }
            }
        }

        private bool IsProven(Type type, MethodInfo method)
            => !inputs.Tests.IsStaticallySkipped(method) && TargetManifest(type, method) is not null && inputs.Tests.CaseCount(type, method) > 0;

        // ---- R7, R9 ----

        private PlatformSkip? SkipRow(Type type, MethodInfo method)
            => _gate.PlatformSkips.FirstOrDefault(p => p.Test == $"{type.FullName}.{method.Name}");

        /// <summary>The manifest a named method is rendered into, or null when it is proven nowhere.</summary>
        private string? TargetManifest(Type type, MethodInfo method)
        {
            if (SkipRow(type, method) is { } skip)
                return skip.RunsUnskippedIn is null ? null : SuiteManifest(skip.RunsUnskippedIn);
            var run = inputs.Tests.RunOf(type, method);
            if (run == TestRun.ImageRuntime && !SuiteWired(ImageRuntimeManifest)) return null;
            return ManifestOf(run);
        }

        /// <summary>The manifest a mandatory suite's step checks with --require: "&lt;workflow&gt; &lt;job&gt;" or with " / &lt;step&gt;".</summary>
        private string? SuiteManifest(string reference)
        {
            var m = Regex.Match(reference, @"^(\S+\.yml) (\S+)(?: / (.+))?$");
            if (!m.Success) return null;
            var suites = _gate.Suites.Where(s => s.Workflow == m.Groups[1].Value && s.Job == m.Groups[2].Value
                                                 && (!m.Groups[3].Success || s.Step == m.Groups[3].Value)).ToList();
            var manifests = suites.Select(s => RequiredManifest(s)).OfType<string>().Distinct().ToList();
            return manifests.Count == 1 ? manifests[0] : null;
        }

        private string? RequiredManifest(MandatorySuite suite)
        {
            var run = StepRun(suite);
            if (run is null || !run.Contains("--logger \"trx", StringComparison.Ordinal)) return null;
            var m = Regex.Match(run, @"check-test-results\.sh \S+ [1-9]\d*(?: --no-skips)? --require \.github/required-tests/([\w.-]+\.txt)");
            return m.Success ? m.Groups[1].Value : null;
        }

        private bool SuiteWired(string manifest) => _gate.Suites.Any(s => RequiredManifest(s) == manifest);

        private string? StepRun(MandatorySuite suite)
        {
            if (!_jobs.TryGetValue(suite.Workflow, out var jobs) || !jobs.TryGetValue(suite.Job, out var job)) return null;
            var steps = Workflow.Steps(job).Where(s => suite.Step is null || Workflow.Scalar(s, "name") == suite.Step).ToList();
            return steps.Count == 0 ? null : string.Join("\n", steps.Select(s => Workflow.Scalar(s, "run")).OfType<string>());
        }

        private void Skips()
        {
            var namedByAny = _named.Values.SelectMany(v => v).Select(m => $"{m.Type.FullName}.{m.Method.Name}").ToHashSet(StringComparer.Ordinal);
            foreach (var (id, methods) in _named)
            foreach (var (type, method) in methods.Distinct())
            {
                if (inputs.Tests.IsStaticallySkipped(method))
                    Add("R7", id, $"{type.FullName}.{method.Name} is statically skipped (Skip = on the method or a data row); a skipped test proves nothing");
                if (SkipRow(type, method) is null && inputs.Tests.RunOf(type, method) is var run)
                {
                    if (run == TestRun.None) Add("R9", id, $"{type.FullName}.{method.Name} is selected by no CI run (not a public test class xUnit discovers)");
                    if (run == TestRun.ImageRuntime && !SuiteWired(ImageRuntimeManifest))
                        Add("R9", id, $"{type.FullName}.{method.Name} runs only in the image-runtime job, which checks no manifest with --require (D14)");
                }
            }
            foreach (var skip in _gate.PlatformSkips)
            {
                if (!namedByAny.Contains(skip.Test))
                    Add("R7", skip.Test, $"RELEASE_GATE.md:{skip.Line}: a platform skip for a method no invariant names, or that does not exist, is stale");
                if (skip.RunsUnskippedIn is { } job && SuiteManifest(job) is null)
                    Add("R7", skip.Test, $"RELEASE_GATE.md:{skip.Line}: '{job}' is not a mandatory suite ('<workflow> <job>[ / <step>]') whose step writes a TRX file and checks its own manifest with --require");
            }
        }

        // ---- R8 ----

        public Dictionary<string, string> RenderBlocks()
        {
            var excepted = ValidExceptions().ToHashSet(StringComparer.Ordinal);
            var entries = new Dictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal)
            {
                [InProcessManifest] = new(StringComparer.Ordinal),
                [DockerManifest] = new(StringComparer.Ordinal),
                [ImageRuntimeManifest] = new(StringComparer.Ordinal),
            };
            var sources = _rows.Where(r => r.Status == InvariantStatus.Holds || excepted.Contains(r.Id)).Select(r => r.Id).Concat(_claims);
            foreach (var id in sources)
            foreach (var (type, method) in _named[id])
            {
                if (inputs.Tests.IsStaticallySkipped(method)) continue;
                var manifest = TargetManifest(type, method);
                if (manifest is null) continue;
                var count = inputs.Tests.CaseCount(type, method);
                if (count == 0) continue;
                if (!entries.TryGetValue(manifest, out var list)) entries[manifest] = list = new(StringComparer.Ordinal);
                list[$"{type.FullName}.{method.Name}"] = count;
            }
            return entries.ToDictionary(kv => kv.Key, kv =>
            {
                var sb = new StringBuilder();
                sb.Append(BlockBegin).Append('\n');
                sb.Append("# Rendered by ReleaseGateTests from docs/v1/SECURITY_INVARIANTS.md, docs/v1/RELEASE_GATE.md and docs/v1/CLAIMS.md\n");
                sb.Append("# (contract 0008, R8; contract 0011): one entry per test method named by a ✅ invariant, an accepted exception or a\n");
                sb.Append("# registered claim, the minimum = the cases xUnit discovers. Do not edit by hand; a difference writes the fresh\n");
                sb.Append("# block to this file's .actual.\n");
                foreach (var (name, count) in kv.Value) sb.Append(count).Append(' ').Append(name).Append('\n');
                sb.Append(BlockEnd).Append('\n');
                return sb.ToString();
            }, StringComparer.Ordinal);
        }

        private void Manifests()
        {
            foreach (var (id, methods) in _named)
            foreach (var (type, method) in methods.Distinct())
                if (!inputs.Tests.IsStaticallySkipped(method) && inputs.Tests.RunOf(type, method) != TestRun.None && inputs.Tests.CaseCount(type, method) == 0)
                    Add("R8", id, $"xUnit's discovery reports no test case for {type.FullName}.{method.Name}");
            foreach (var (manifest, block) in RenderBlocks())
            {
                if (!inputs.Manifests.TryGetValue(manifest, out var text))
                {
                    Add("R8", manifest, $".github/required-tests/{manifest} is missing");
                    continue;
                }
                var current = BlockOf(text);
                if (current is null) Add("R8", manifest, $"has no generated block ('{BlockBegin}' … '{BlockEnd}', exactly once)");
                else if (current != block) Add("R8", manifest, "the generated block differs from the named tests; the fresh one is written beside the manifest as .actual");
            }
        }

        // ---- R10 ----

        private static readonly (string Workflow, string Job, string Step, string Pattern, string What)[] RequiredSuites =
        [
            ("ci.yml", "build-test", "Test (in-process)",
                @"check-test-results\.sh \S+ [1-9]\d* --require \.github/required-tests/in-process\.txt", "a floor and --require in-process.txt"),
            ("ci.yml", "build-test", "Test (Docker integration, mandatory)",
                @"check-test-results\.sh \S+ [1-9]\d* --no-skips --require \.github/required-tests/docker\.txt", "a floor, --no-skips and --require docker.txt"),
            ("ci.yml", "image-runtime", "Image runtime tests",
                @"check-test-results\.sh \S+ [1-9]\d* --no-skips --require \.github/required-tests/image-runtime\.txt", "a TRX file, a floor, --no-skips and --require image-runtime.txt (D14)"),
            ("ci.yml", "build-test", "Release gate (compare with the base)",
                @"check-test-results\.sh \S+ [1-9]\d* --no-skips", "a TRX check of the base comparison (D16)"),
        ];

        /// <summary>The only conditions a mandatory step may carry: the base comparison runs on every pull request and every
        /// release run (D16), and nowhere else is there a base to compare with. Any other step condition is an R10 finding.</summary>
        private static readonly Dictionary<(string Workflow, string Job, string? Step), string> AllowedStepConditions = new()
        {
            [("ci.yml", "build-test", "Release gate (compare with the base)")] = "github.event_name == 'pull_request' || inputs.release-gate == 'release'",
        };

        private void Suites()
        {
            foreach (var (wf, job, step, pattern, what) in RequiredSuites)
            {
                var row = _gate.Suites.FirstOrDefault(s => s.Workflow == wf && s.Job == job && s.Step == step);
                if (row is null) { Add("R10", $"{wf} {job} / {step}", "is not a row of 'Mandatory suites' in RELEASE_GATE.md"); continue; }
                var run = StepRun(row);
                if (run is null || !Regex.IsMatch(run, pattern) || !run.Contains("--logger \"trx", StringComparison.Ordinal))
                    Add("R10", $"{wf} {job} / {step}", $"the step no longer has {what}");
            }
            if (!_gate.Suites.Any(s => s.Workflow == "security.yml"))
                Add("R10", "security.yml", "no security.yml job is a mandatory suite (D8)");

            _jobs.TryGetValue("release.yml", out var release);
            var gated = new HashSet<string>(StringComparer.Ordinal);
            if (release is not null && release.ContainsKey("publish-image"))
            {
                gated = Workflow.Ancestors(release, "publish-image");
                gated.Add("publish-image");
            }
            else Add("R10", "release.yml", "has no 'publish-image' job");

            foreach (var s in _gate.Suites)
            {
                var subject = $"{s.Workflow} {s.Job}{(s.Step is null ? "" : " / " + s.Step)}";
                if (!_jobs.TryGetValue(s.Workflow, out var jobs)) { Add("R10", subject, $"RELEASE_GATE.md:{s.Line}: no workflow {s.Workflow}"); continue; }
                if (!jobs.TryGetValue(s.Job, out var job)) { Add("R10", subject, $"RELEASE_GATE.md:{s.Line}: the job does not exist"); continue; }
                if (Workflow.Truthy(Workflow.Scalar(job, "continue-on-error"))) Add("R10", subject, "the job may fail without failing the run (continue-on-error)");
                if (Workflow.Scalar(job, "if") is { } condition) Add("R10", subject, $"the job is conditional ('if: {condition}') and may be skipped in a release run");
                if (s.Step is not null)
                {
                    var steps = Workflow.Steps(job).Where(x => Workflow.Scalar(x, "name") == s.Step).ToList();
                    if (steps.Count != 1) Add("R10", subject, $"RELEASE_GATE.md:{s.Line}: the job has {steps.Count} steps of that name");
                    if (steps.Any(x => Workflow.Truthy(Workflow.Scalar(x, "continue-on-error")))) Add("R10", subject, "the step may fail without failing the job (continue-on-error)");
                    foreach (var stepCondition in steps.Select(x => Workflow.Scalar(x, "if")).OfType<string>())
                        if (!AllowedStepConditions.TryGetValue((s.Workflow, s.Job, s.Step), out var allowed) || stepCondition.Trim() != allowed)
                            Add("R10", subject, $"the step is conditional ('if: {stepCondition}') and may be skipped in a release run");
                }
                else
                {
                    foreach (var x in Workflow.Steps(job))
                        if (Workflow.Scalar(x, "if") is { } stepIf)
                            Add("R10", subject, $"step '{Workflow.Scalar(x, "name") ?? Workflow.Scalar(x, "uses") ?? "?"}' is conditional ('if: {stepIf}') and may be skipped in a release run");
                }
                switch (s.Workflow)
                {
                    case "release.yml":
                        if (!gated.Contains(s.Job)) Add("R10", subject, "the job is not in the needs of 'publish-image': a release could publish without it");
                        break;
                    case "ci.yml":
                    case "security.yml":
                        var callers = release?.Where(j => Workflow.Scalar(j.Value, "uses") == $"./.github/workflows/{s.Workflow}"
                                                          && Workflow.Scalar(j.Value, "if") is null).Select(j => j.Key).ToList() ?? [];
                        if (!callers.Any(gated.Contains))
                            Add("R10", subject, $"release.yml calls {s.Workflow} from no unconditional job that 'publish-image' needs");
                        break;
                    default:
                        Add("R10", subject, $"RELEASE_GATE.md:{s.Line}: only ci.yml, release.yml and security.yml are release workflows");
                        break;
                }
            }
        }

        // ---- decisions, R11, R17 ----

        private IEnumerable<string> DecisionProblems(string id, string kind, string invariant, string? release)
        {
            var matches = _decisions.Where(d => d.Id == id).ToList();
            if (matches.Count == 0) { yield return $"names the decision '{id}', which is not a row of PRODUCT_CONTRACT.md §6.1"; yield break; }
            var d = matches[0];
            if (d.Kind != kind) yield return $"{id} is of kind '{d.Kind}', not '{kind}'";
            if (d.Invariant != invariant) yield return $"{id} is a decision for {d.Invariant}, not for {invariant}";
            if (d.Status != "Accepted") yield return $"{id} is '{d.Status}', not Accepted";
            if (d.Date is null) yield return $"{id} has no ISO date of acceptance";
            else if (d.Date > inputs.Today) yield return $"{id} is dated {d.Date:yyyy-MM-dd}, in the future";
            if (string.IsNullOrWhiteSpace(d.DecidedBy) || ReleaseGateDocument.Dash(d.DecidedBy) is null) yield return $"{id} names no maintainer under 'Decided by'";
            if (release is not null && d.Release != release) yield return $"{id} covers release '{d.Release ?? "—"}', the record says '{release}'";
        }

        private void Decisions()
        {
            foreach (var dup in _decisions.GroupBy(d => d.Id).Where(g => g.Count() > 1))
                Add("R18", dup.Key, "is used by more than one decision in §6.1; an ID is never reused");
            foreach (var d in _decisions)
            {
                var at = $"PRODUCT_CONTRACT.md:{d.Line}";
                if (!DecisionId.IsMatch(d.Id)) Add("R11", d.Id, $"{at}: not a decision id RG-n");
                if (d.Kind is not ("exception" or "retirement" or "known violation")) Add("R11", d.Id, $"{at}: kind '{d.Kind}' is not exception, retirement or known violation");
                if (d.Status is not ("Proposed" or "Accepted")) Add("R11", d.Id, $"{at}: status '{d.Status}' is not Proposed or Accepted");
                if (d.Kind == "known violation" ? GateVersion.TryParse(d.Release) is null : d.Release is not null)
                    Add("R11", d.Id, $"{at}: 'Release' is the exact version X.Y.Z for a known violation and — otherwise");
                if (!InvariantId.IsMatch(d.Invariant)) Add("R11", d.Id, $"{at}: '{d.Invariant}' is not an invariant id");
            }
        }

        private IEnumerable<string> ValidExceptions()
        {
            var statuses = _rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Status, StringComparer.Ordinal);
            foreach (var x in _gate.Exceptions)
                if (statuses.GetValueOrDefault(x.Id) == InvariantStatus.Partial && ExceptionProblems(x).Count == 0)
                    yield return x.Id;
        }

        private List<string> ExceptionProblems(AcceptedException x)
        {
            var problems = new List<string>();
            var status = _rows.FirstOrDefault(r => r.Id == x.Id)?.Status;
            if (status is null) problems.Add("names an invariant the table does not have (or whose status cell is malformed)");
            else if (status is InvariantStatus.Violated or InvariantStatus.Unverified) problems.Add($"an exception for a {InvariantTableParser.Symbol(status.Value)} invariant is never accepted (D11)");
            else if (status == InvariantStatus.Holds) problems.Add("the invariant is ✅: the exception is stale, remove it");
            if (x.Shipped != InvariantStatus.Partial) problems.Add("'Status shipped' must be ⚠️");
            problems.AddRange(DecisionProblems(x.DecisionId, "exception", x.Id, null));
            problems.AddRange(LimitationProblems(x.LimitationLink, x.Id));
            var review = GateVersion.TryParse(x.ReviewBy);
            if (review is null) problems.Add($"'Review by' '{x.ReviewBy}' is not X.Y.Z");
            else if (review <= version) problems.Add($"is due: 'Review by' {review} is not after the current version {version}");
            return problems;
        }

        private void Exceptions()
        {
            foreach (var dup in _gate.Exceptions.GroupBy(x => x.Id).Where(g => g.Count() > 1))
                Add("R11", dup.Key, "has more than one accepted exception");
            foreach (var x in _gate.Exceptions)
                foreach (var p in ExceptionProblems(x))
                    Add("R11", x.Id, $"RELEASE_GATE.md:{x.Line}: {p}");
        }

        private IEnumerable<string> LimitationProblems(string link, string invariant)
        {
            var m = Regex.Match(link.Trim().Trim('`'), @"^([\w./-]+\.md)#([\w-]+)$");
            if (!m.Success) { yield return $"the limitation '{link}' is not a link <file>.md#<heading>"; yield break; }
            var path = m.Groups[1].Value;
            var allowed = path is "README.md" or "SECURITY.md" or "docs/v1/PRODUCT_CONTRACT.md"
                          || Regex.IsMatch(path, @"^docs/operations/[\w.-]+\.md$");
            if (!allowed) { yield return $"the limitation must be in README.md, SECURITY.md, docs/operations/*.md or PRODUCT_CONTRACT.md §5, not {path}"; yield break; }
            var text = inputs.ReadDocument(path);
            if (text is null) { yield return $"the limitation links to {path}, which does not exist"; yield break; }
            var lines = MarkdownTables.Lines(text);
            var at = Array.FindIndex(lines, l => l.StartsWith('#') && Slug(l) == m.Groups[2].Value);
            if (at < 0) { yield return $"{path} has no heading #{m.Groups[2].Value}"; yield break; }
            if (path == "docs/v1/PRODUCT_CONTRACT.md")
            {
                var five = Array.FindIndex(lines, l => l.StartsWith("## 5.", StringComparison.Ordinal));
                var six = Array.FindIndex(lines, l => l.StartsWith("## 6.", StringComparison.Ordinal));
                if (five < 0 || at < five || (six >= 0 && at >= six)) yield return "a limitation in PRODUCT_CONTRACT.md must be in §5";
            }
            var level = lines[at].TakeWhile(c => c == '#').Count();
            var section = lines.Skip(at).TakeWhile((l, i) => i == 0 || !(l.StartsWith('#') && l.TakeWhile(c => c == '#').Count() <= level));
            if (!section.Any(l => Regex.IsMatch(l, $@"\b{Regex.Escape(invariant)}\b")))
                yield return $"the section {path}#{m.Groups[2].Value} does not name {invariant}";
        }

        private HashSet<string> ValidKnownViolations()
            => _gate.Known.Where(k => KnownViolationProblems(k).Count == 0).Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

        private List<string> KnownViolationProblems(KnownViolation k)
        {
            var problems = new List<string>();
            var status = _rows.FirstOrDefault(r => r.Id == k.Id)?.Status;
            if (status is not (InvariantStatus.Violated or InvariantStatus.Unverified)) problems.Add("a known violation names an invariant that is ❌ or ❓ only");
            if (k.Status != status) problems.Add("'Status' differs from the table's");
            var release = GateVersion.TryParse(k.Release);
            if (release is null) problems.Add($"'Release' '{k.Release}' is not X.Y.Z");
            else
            {
                if (release != version) problems.Add($"covers {release}, not the current version {version}: a record covers exactly one release");
                if (release.Patch == 0) problems.Add($"{release} is not a patch release: no X.Y.0 ships with a ❌ or ❓");
                var review = GateVersion.TryParse(k.ReviewBy);
                if (review != release.NextPatch()) problems.Add($"'Review by' must be {release.NextPatch()}, the next patch after 'Release'");
            }
            if (!IsAdvisory(k.Advisory)) problems.Add($"'Advisory' '{k.Advisory}' is not GHSA-xxxx-xxxx-xxxx");
            problems.AddRange(DecisionProblems(k.DecisionId, "known violation", k.Id, k.Release));
            problems.AddRange(LimitationProblems(k.LimitationLink, k.Id));
            return problems;
        }

        private void KnownViolations()
        {
            foreach (var k in _gate.Known)
                foreach (var p in KnownViolationProblems(k))
                    Add("R17", k.Id, $"RELEASE_GATE.md:{k.Line}: {p}");
        }

        // ---- R12–R15 ----

        private void Inventory()
        {
            var problems = new List<string>();
            _paths = PathMatrixParser.Parse(inputs.Contract, inputs.PathCapabilities, problems).ToList();
            foreach (var p in problems) Add("R13", "PATH_CAPABILITIES.md", p);

            var rendered = CapabilityInventoryRenderer.Render(inputs.Capabilities, _paths, inputs.Surfaces, inputs.Compositions);
            if (inputs.CapabilityInventory is null) Add("R12", "CAPABILITY_INVENTORY.md", "docs/v1/CAPABILITY_INVENTORY.md is missing; it is generated (.actual)");
            else if (Normalise(inputs.CapabilityInventory) != Normalise(rendered))
                Add("R12", "CAPABILITY_INVENTORY.md", "differs from the catalog; the fresh render is written beside it as .actual");

            var catalog = inputs.Capabilities;
            var composed = inputs.Compositions.SelectMany(c => c.Inner).ToHashSet(StringComparer.Ordinal);
            var withoutPath = PathMatrixParser.WithoutPath(inputs.PathCapabilities, problems = []);
            foreach (var p in problems) Add("R13", "PATH_CAPABILITIES.md", p);
            // R13: code → matrix.
            foreach (var c in catalog.All)
            {
                var named = _paths.Any(r => r.McpTool is not null ? r.McpTool == c.McpTool : r.Capabilities.Contains(c.Id));
                if (named && withoutPath.ContainsKey(c.Id))
                    Add("R13", c.Id, "is listed under 'Capabilities without a path' but a §7 row names it: remove the stale line");
                if (!named && !composed.Contains(c.Id) && !withoutPath.ContainsKey(c.Id))
                    Add("R13", c.Id, c.McpTool is null
                        ? "no §7 row names this capability (PATH_CAPABILITIES.md), it is not composed by another and not listed as without a path"
                        : $"its MCP tool '{c.McpTool}' has no M row in §7");
            }
            foreach (var id in withoutPath.Keys.Where(id => catalog.Find(id) is null))
                Add("R14", id, "PATH_CAPABILITIES.md lists it under 'Capabilities without a path', but it is no capability of the catalog");
            // R14: matrix → code.
            foreach (var r in _paths)
            {
                if (r.Class == "M")
                {
                    if (r.McpTool is null) { Add("R14", r.Id, $"PRODUCT_CONTRACT.md:{r.Line}: the M row names no tool in backticks"); continue; }
                    if (catalog.FindByMcpTool(r.McpTool) is not null) continue;
                    if (!inputs.McpTools.Contains(r.McpTool)) Add("R14", r.Id, $"PRODUCT_CONTRACT.md:{r.Line}: '{r.McpTool}' is no MCP tool");
                    else if (!r.LevelCell.Trim().StartsWith("read", StringComparison.Ordinal))
                        Add("R14", r.Id, $"PRODUCT_CONTRACT.md:{r.Line}: '{r.McpTool}' performs no catalog capability, so the row must name it as a read tool");
                    continue;
                }
                foreach (var id in r.Capabilities)
                    if (catalog.Find(id) is null) Add("R14", r.Id, $"PATH_CAPABILITIES.md names '{id}', which is no capability of the catalog");
            }
            // R15: totals.
            var totals = PathMatrixParser.Totals(inputs.Contract, problems = []);
            foreach (var p in problems) Add("R15", "PRODUCT_CONTRACT.md §7.6", p);
            if (totals is not null)
            {
                foreach (var cls in new[] { "M", "A", "S", "T", "U" })
                {
                    var counted = _paths.Count(r => r.Class == cls);
                    if (totals.GetValueOrDefault(cls, -1) != counted)
                        Add("R15", $"§7.6 ({cls})", $"says {(totals.TryGetValue(cls, out var t) ? t : "nothing")}, §7 has {counted} rows");
                }
                if (totals.GetValueOrDefault("Total", -1) != _paths.Count)
                    Add("R15", "§7.6 (Total)", $"says {(totals.TryGetValue("Total", out var t) ? t : "nothing")}, §7 has {_paths.Count} rows");
            }
        }

        private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd();

        // ---- R16 ----

        private void ModeInput()
        {
            _jobs.TryGetValue("release.yml", out var release);
            _jobs.TryGetValue("ci.yml", out var ci);
            var callers = release?.Where(j => Workflow.Scalar(j.Value, "uses") == "./.github/workflows/ci.yml").ToList() ?? [];
            if (callers.Count != 1 || Workflow.Scalar(Workflow.Child(callers[0].Value, "with"), "release-gate") != "release")
                Add("R16", "release.yml", "the job that calls ci.yml does not pass 'release-gate: release'");
            var ciText = inputs.Workflows.GetValueOrDefault("ci.yml") ?? "";
            if (Workflow.Child(Workflow.Child(Workflow.Child(Workflow.Root(ciText), "on"), "workflow_call"), "inputs") is not { } inputsNode
                || Workflow.Child(inputsNode, "release-gate") is null)
                Add("R16", "ci.yml", "has no workflow_call input 'release-gate'");
            var setters = ci?.SelectMany(j => Workflow.Steps(j.Value).Select(s => (Job: j.Key, Step: s)))
                .Where(x => Workflow.Child(Workflow.Child(x.Step, "env"), ModeVariable) is not null).ToList() ?? [];
            if (setters.Count != 1
                || Workflow.Scalar(setters[0].Step, "name") != "Test (in-process)"
                || Workflow.Scalar(Workflow.Child(setters[0].Step, "env"), ModeVariable)?.Replace(" ", "") != "${{inputs.release-gate}}")
                Add("R16", "ci.yml", $"{ModeVariable} must be set once, on the in-process test step, from the input 'release-gate' only");
            foreach (var (file, text) in inputs.Workflows)
                if (file != "ci.yml" && text.Contains(ModeVariable, StringComparison.Ordinal))
                    Add("R16", file, $"sets {ModeVariable}; only ci.yml's in-process step may, from its input");
        }
    }
}
