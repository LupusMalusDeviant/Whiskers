using System.Reflection;
using System.Text.RegularExpressions;
using Whiskers.Tests.ReleaseGate;

namespace Whiskers.Tests;

/// <summary>
/// R1-47: the manifests under <c>.github/required-tests/</c> name the tests CI requires in each run
/// (<c>check-test-results.sh --require</c>): each must be in the TRX file and pass, whatever the floor says. These tests
/// keep the manifests honest against the test assembly — an entry that names nothing (a renamed test would then be
/// "missing" in CI only), an entry in the wrong run, or a container test class that is not required at all.
/// </summary>
public class RequiredTestsManifestTests
{
    private const string InProcess = "in-process.txt";
    private const string Docker = "docker.txt";
    private const string ImageRuntime = "image-runtime.txt";

    [Theory]
    [InlineData(InProcess)]
    [InlineData(Docker)]
    [InlineData(ImageRuntime)]
    public void Every_entry_names_tests_of_its_run(string manifest)
    {
        var entries = Entries(manifest);
        Assert.NotEmpty(entries);
        var problems = new List<string>();
        foreach (var (minimum, name) in entries)
        {
            var tests = Resolve(name);
            if (tests.Count == 0)
            {
                problems.Add($"{name}: no test class or test method of that name");
                continue;
            }
            foreach (var (type, method) in tests)
                if (TestCatalog.Instance.RunOf(type, method) != RunOf(manifest))
                    problems.Add($"{name}: {type.Name}.{method.Name} does not run in the {manifest} run");
            // A single [Fact] yields one result: a higher minimum could never be met.
            if (tests.Count == 1 && tests[0].Method.GetCustomAttribute<TheoryAttribute>() is null && minimum > 1)
                problems.Add($"{name}: one fact, but a minimum of {minimum}");
            if (tests.Count > 1 && minimum < tests.Count(t => t.Method.GetCustomAttribute<TheoryAttribute>() is null))
                problems.Add($"{name}: a minimum of {minimum} lets some of its {tests.Count} tests vanish");
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void Every_container_test_class_is_required_in_the_docker_run()
    {
        var required = Entries(Docker).SelectMany(e => Resolve(e.Name)).Select(t => (t.Type, t.Method.Name)).ToHashSet();
        var missing = TestClasses()
            .SelectMany(type => TestMethods(type).Where(m => TestCatalog.Instance.RunOf(type, m) == TestRun.Docker).Select(m => (type, m)))
            .Where(t => !required.Contains((t.type, t.m.Name)))
            .Select(t => $"{t.type.FullName}.{t.m.Name}")
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_image_runtime_test_is_required_in_the_image_runtime_run()
    {
        var required = Entries(ImageRuntime).SelectMany(e => Resolve(e.Name)).Select(t => (t.Type, t.Method.Name)).ToHashSet();
        var missing = TestClasses()
            .SelectMany(type => TestMethods(type).Where(m => TestCatalog.Instance.RunOf(type, m) == TestRun.ImageRuntime).Select(m => (type, m)))
            .Where(t => !required.Contains((t.type, t.m.Name)))
            .Select(t => $"{t.type.FullName}.{t.m.Name}")
            .ToList();
        Assert.NotEmpty(required);
        Assert.Empty(missing);
    }

    [Fact]
    public void The_release_gate_and_its_parsers_are_required_in_the_in_process_run()
    {
        // Contract 0008: "It is named in .github/required-tests/in-process.txt so it cannot vanish" (review M2).
        var entries = Entries(InProcess);
        Assert.Contains(entries, e => e.Name == typeof(ReleaseGateTests).FullName + "." + nameof(ReleaseGateTests.The_repository_passes_the_gate_in_its_mode));
        Assert.Contains(entries, e => e.Name == typeof(ReleaseGateBaseTests).FullName + "." + nameof(ReleaseGateBaseTests.The_records_keep_every_id_and_decision_of_the_base));
        foreach (var type in new[] { typeof(ReleaseGateTests), typeof(InvariantTableParserTests), typeof(PathMatrixParserTests), typeof(ReleaseGateDocumentTests) })
        {
            var entry = entries.SingleOrDefault(e => e.Name == type.FullName);
            Assert.True(entry.Name is not null, $"{type.FullName} is not a class entry of {InProcess}");
            var cases = TestMethods(type).Sum(m => TestCatalog.Instance.CaseCount(type, m));
            Assert.True(entry.Minimum == cases, $"{type.FullName}: the minimum is {entry.Minimum}, xUnit discovers {cases} cases");
        }
    }

    [Fact]
    public void The_endpoint_matrix_and_the_postgres_identity_and_metrics_suites_are_required_test_by_test()
    {
        void AllRequired(string manifest, Type type)
        {
            var required = Entries(manifest).SelectMany(e => Resolve(e.Name)).Select(t => (t.Type, t.Method.Name)).ToHashSet();
            var methods = TestMethods(type).ToList();
            Assert.NotEmpty(methods);
            Assert.All(methods, m => Assert.Contains((type, m.Name), required));
            // Named one by one: a class entry with a count would let one of them vanish behind a new test.
            Assert.DoesNotContain(Entries(manifest), e => e.Name == type.FullName);
        }
        AllRequired(InProcess, typeof(EndpointAuthMatrixTests));
        AllRequired(Docker, typeof(PostgresIdentityTests));
        AllRequired(Docker, typeof(PostgresSmokeTests));
    }

    /// <summary>
    /// In-process tests ("Namespace.Class.Method") that deliberately have no line in <c>in-process.txt</c>, each with its
    /// reason. Keep it short: a test lands here only when no entry can require it on the Linux runner — never to silence
    /// a forgotten one.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotRequiredInProcess = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [$"{typeof(PerHostCredentialTests).FullName}.{nameof(PerHostCredentialTests.On_windows_a_key_readable_by_everyone_is_exposed)}"] =
            "Windows only (it checks Windows ACLs and skips elsewhere): it skips on the Linux runner, and check-test-results.sh fails a required test that skipped.",
    };

    [Fact]
    public void Every_in_process_test_is_named_in_the_in_process_manifest()
    {
        // A test without a line would only be guarded by the floor: it could vanish (made internal, filtered out, its
        // attributes lost) and CI would still pass. A test counts as named only when a class entry or its own method
        // entry stands for it. Statically skipped tests are left out, as in the generated block: CI cannot require them.
        var entries = Entries(InProcess);
        var classEntries = entries.Select(e => typeof(RequiredTestsManifestTests).Assembly.GetType(e.Name)).OfType<Type>().ToHashSet();
        var covered = entries.SelectMany(e => Resolve(e.Name)).Select(t => (t.Type, t.Method.Name)).ToHashSet();
        var classes = TestCatalog.TestClasses(typeof(RequiredTestsManifestTests).Assembly).ToList();
        var excluded = NotRequiredInProcess.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = classes
            .Select(type => (type, all: TestMethods(type).ToList()))
            .Select(c => (c.type, c.all, uncovered: c.all
                .Where(m => RequiredInProcess(c.type, m) && !covered.Contains((c.type, m.Name)) && !excluded.Contains($"{c.type.FullName}.{m.Name}"))
                .ToList()))
            .Where(c => c.uncovered.Count > 0)
            // A class entry matches every result of the class in the run, so it may stand only for a class whose tests
            // all run and pass here: one that also holds tests of another run (image-runtime tests skip in this one),
            // a skipped or excluded test or a test that skips by operating system is named test by test.
            .SelectMany(c => c.all.All(m => RequiredInProcess(c.type, m) && !SkipsByOperatingSystem(m) && !excluded.Contains($"{c.type.FullName}.{m.Name}"))
                ? [$"{c.all.Sum(m => TestCatalog.Instance.CaseCount(c.type, m))} {c.type.FullName}"]
                : c.uncovered.Select(m => $"{TestCatalog.Instance.CaseCount(c.type, m)} {c.type.FullName}.{m.Name}"
                    + (SkipsByOperatingSystem(m) ? "    <-- SKIPS BY OPERATING SYSTEM: add it only if it runs on the Linux runner, otherwise put it on " + nameof(NotRequiredInProcess) + " with the reason" : "")))
            .OrderBy(line => line[(line.IndexOf(' ') + 1)..], StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0,
            $"in-process tests have no line in .github/required-tests/{InProcess}. Add these {missing.Count} line(s) to its " +
            "hand-written part (above '# BEGIN release-gate (generated)'), under a comment that says what they guard.\n" +
            "CAUTION: check-test-results.sh fails a required test that skipped. Operating-system skips (Skip.If/IfNot over " +
            "OperatingSystem.*) in a test's own body are detected and named test by test; skips inside helpers are NOT — if a " +
            "class line below holds such a test, name the class test by test instead:\n" + string.Join("\n", missing));

        // The other way round: a class entry over a test that skips by operating system fails on whichever runner skips.
        var osSkipped = classEntries
            .SelectMany(type => TestMethods(type).Where(SkipsByOperatingSystem).Select(m => $"{type.FullName}.{m.Name}"))
            .ToList();
        Assert.True(osSkipped.Count == 0,
            $"{InProcess} names these classes whole, but these tests skip by operating system; name the class test by test:\n" +
            string.Join("\n", osSkipped));
        var allTests = classes.SelectMany(type => TestMethods(type).Select(m => $"{type.FullName}.{m.Name}")).ToHashSet(StringComparer.Ordinal);
        var coveredNames = covered.Select(t => $"{t.Type.FullName}.{t.Name}").ToHashSet(StringComparer.Ordinal);
        Assert.All(NotRequiredInProcess, kv =>
        {
            Assert.True(allTests.Contains(kv.Key), $"{nameof(NotRequiredInProcess)}: {kv.Key} is no test (renamed or removed?)");
            Assert.False(coveredNames.Contains(kv.Key), $"{kv.Key} is named in {InProcess}; take it off {nameof(NotRequiredInProcess)}");
            Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{nameof(NotRequiredInProcess)}: {kv.Key} has no reason");
        });
    }

    [Theory]
    [InlineData(InProcess)]
    [InlineData(Docker)]
    [InlineData(ImageRuntime)]
    public void No_test_is_named_twice(string manifest)
    {
        // A second line for the same name checks nothing new and goes stale on its own. The generated block is rewritten
        // by the release gate, so a name it already holds is removed from the hand-written part.
        var twice = Entries(manifest)
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({g.Count()} lines)")
            .ToList();
        Assert.True(twice.Count == 0,
            $"{manifest} names these more than once; keep one line each (the one in the generated block, if it has one):\n" +
            string.Join("\n", twice));
    }

    [Theory]
    [InlineData(InProcess)]
    [InlineData(Docker)]
    [InlineData(ImageRuntime)]
    public void Every_minimum_is_the_cases_xunit_discovers(string manifest)
    {
        // A minimum below the cases lets that many of them vanish unnoticed (a class entry left at its old count when the
        // class grew); one above them fails CI only. Statically skipped tests are not counted: CI cannot require them.
        var wrong = Entries(manifest)
            .Select(e => (e.Minimum, e.Name, Cases: Resolve(e.Name)
                .Where(t => !TestCatalog.Instance.IsStaticallySkipped(t.Method))
                .Sum(t => TestCatalog.Instance.CaseCount(t.Type, t.Method))))
            .Where(e => e.Cases > 0 && e.Minimum != e.Cases)
            .Select(e => $"'{e.Minimum} {e.Name}' → '{e.Cases} {e.Name}'")
            .ToList();
        Assert.True(wrong.Count == 0, $"{manifest}: set these minimums to the cases xUnit discovers:\n" + string.Join("\n", wrong));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static bool RequiredInProcess(Type type, MethodInfo method)
        => TestCatalog.Instance.RunOf(type, method) == TestRun.InProcess && !TestCatalog.Instance.IsStaticallySkipped(method);

    private static readonly Lazy<string[]> TestSources = new(() =>
    {
        var root = Path.Combine(RepoRoot(), "src", "Whiskers.Tests");
        string[] generated = [Path.Combine(root, "obj") + Path.DirectorySeparatorChar, Path.Combine(root, "bin") + Path.DirectorySeparatorChar];
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !generated.Any(g => f.StartsWith(g, StringComparison.OrdinalIgnoreCase)))
            .Select(File.ReadAllText)
            .ToArray();
    });

    private static readonly Regex OperatingSystemSkip = new(@"Skip\.If(Not)?\s*\([^;]*OperatingSystem\.", RegexOptions.Compiled);
    private static readonly Regex TestAttribute = new(@"\[(Skippable)?(Fact|Theory)\b", RegexOptions.Compiled);

    /// <summary>True when the test's own body (from its name to the next test attribute of the file) skips by operating
    /// system: <c>Skip.If</c>/<c>Skip.IfNot</c> over <c>OperatingSystem.*</c>. Skips inside helpers are not seen; a
    /// mention of the name elsewhere in the file may add a false positive, which only asks for method lines.</summary>
    private static bool SkipsByOperatingSystem(MethodInfo method)
    {
        var declaring = method.DeclaringType!.Name;
        var tick = declaring.IndexOf('`');
        if (tick >= 0) declaring = declaring[..tick];
        var cls = new Regex($@"\bclass\s+{Regex.Escape(declaring)}\b");
        var name = new Regex($@"\b{Regex.Escape(method.Name)}\s*\(");
        foreach (var source in TestSources.Value.Where(s => cls.IsMatch(s)))
        foreach (Match m in name.Matches(source))
        {
            var next = TestAttribute.Match(source, m.Index);
            if (OperatingSystemSkip.IsMatch(source[m.Index..(next.Success ? next.Index : source.Length)])) return true;
        }
        return false;
    }

    private static IReadOnlyList<(int Minimum, string Name)> Entries(string manifest)
    {
        var path = Path.Combine(RepoRoot(), ".github", "required-tests", manifest);
        Assert.True(File.Exists(path), $"{path} is missing");
        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l =>
            {
                var space = l.IndexOf(' ');
                Assert.True(space > 0 && int.TryParse(l[..space], out var n) && n > 0, $"{manifest}: not '<minimum> <name>': '{l}'");
                return (int.Parse(l[..space]), l[(space + 1)..].Trim());
            })
            .ToList();
    }

    /// <summary>The tests an entry stands for, the way check-test-results.sh matches TRX names: a class (every test
    /// method, inherited ones included) or one method of it.</summary>
    private static List<(Type Type, MethodInfo Method)> Resolve(string name)
    {
        var type = typeof(RequiredTestsManifestTests).Assembly.GetType(name);
        if (type is not null) return TestMethods(type).Select(m => (type, m)).ToList();
        var dot = name.LastIndexOf('.');
        if (dot < 0) return [];
        type = typeof(RequiredTestsManifestTests).Assembly.GetType(name[..dot]);
        if (type is null) return [];
        return TestMethods(type).Where(m => m.Name == name[(dot + 1)..]).Select(m => (type, m)).ToList();
    }

    private static IEnumerable<Type> TestClasses()
        => typeof(RequiredTestsManifestTests).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true });

    private static IEnumerable<MethodInfo> TestMethods(Type type)
        => type.IsAbstract
            ? []
            : type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<FactAttribute>(inherit: true) is not null);

    /// <summary>The run a manifest belongs to, the same classification the release gate uses (contract 0008, R8).</summary>
    private static TestRun RunOf(string manifest) => manifest switch
    {
        InProcess => TestRun.InProcess,
        Docker => TestRun.Docker,
        ImageRuntime => TestRun.ImageRuntime,
        _ => throw new ArgumentOutOfRangeException(nameof(manifest), manifest, "no run for this manifest"),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"could not find Whiskers.slnx above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }
}
