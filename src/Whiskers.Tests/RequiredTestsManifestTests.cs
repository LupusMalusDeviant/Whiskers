using System.Reflection;
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

    // ---- helpers ----------------------------------------------------------------------------------------------

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
