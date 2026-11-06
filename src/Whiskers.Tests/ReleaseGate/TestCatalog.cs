using System.Collections.Concurrent;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>What the gate needs to know about the tests: which exist, how many cases xUnit reports, where they run.</summary>
public interface ITestCatalogView
{
    /// <summary>The test methods a name stands for: a class (every test method, inherited ones included) or one method
    /// of it. The class is a simple or full name. Empty when nothing matches or the type holds no test.</summary>
    IReadOnlyList<(Type Type, MethodInfo Method)> Resolve(string className, string? method);

    /// <summary>True when a type of that name exists at all (to tell "not a test class" from "no such class").</summary>
    bool TypeExists(string className);

    /// <summary>The number of test cases xUnit's own discovery reports for the method (= the TRX results).</summary>
    int CaseCount(Type type, MethodInfo method);

    /// <summary>Method-level <c>Skip =</c>, or a <c>Skip =</c> on any of its data rows.</summary>
    bool IsStaticallySkipped(MethodInfo method);

    /// <summary>The CI run that proves the method.</summary>
    TestRun RunOf(Type type, MethodInfo method);
}

/// <summary>
/// Reflection over the test assembly, shared by the release gate and <see cref="RequiredTestsManifestTests"/>, plus
/// xUnit's own discovery for the case counts (contract 0008, R8): exactly what the runner reports, so the minimum of
/// a generated manifest entry is the number of results the TRX file holds.
/// </summary>
public sealed class TestCatalog : ITestCatalogView
{
    public const string ImageRuntimeCategory = "ImageRuntime";

    public static TestCatalog Instance { get; } = new(typeof(TestCatalog).Assembly);

    private readonly Assembly _assembly;
    private readonly Lazy<Dictionary<(string Type, string Method), int>> _cases;
    private readonly ConcurrentDictionary<string, Dictionary<(string Type, string Method), int>> _typeCases = new();

    public TestCatalog(Assembly assembly)
    {
        _assembly = assembly;
        _cases = new(() => Discover(null));
    }

    // ---- static helpers, the same rules as check-test-results.sh ----

    /// <summary>Public, non-abstract classes that hold at least one test method.</summary>
    public static IEnumerable<Type> TestClasses(Assembly assembly)
        => assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && (t.IsPublic || t.IsNestedPublic) && TestMethods(t).Any());

    /// <summary>The test methods of a class, inherited ones included.</summary>
    public static IEnumerable<MethodInfo> TestMethods(Type type)
        => type.IsAbstract
            ? []
            : type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<FactAttribute>(inherit: true) is not null);

    public static bool HasCategory(Type type, MethodInfo method, string category)
    {
        static bool Has(IEnumerable<CustomAttributeData> attributes, string value) => attributes.Any(a =>
            a.AttributeType == typeof(TraitAttribute)
            && a.ConstructorArguments is [{ Value: "Category" }, { Value: string v }]
            && v == value);
        for (var t = type; t is not null; t = t.BaseType)
            if (Has(t.GetCustomAttributesData(), category)) return true;
        return Has(method.GetCustomAttributesData(), category);
    }

    // ---- ITestCatalogView ----

    public IReadOnlyList<(Type Type, MethodInfo Method)> Resolve(string className, string? method)
    {
        var types = FindTypes(className);
        if (types.Count != 1) return [];
        var type = types[0];
        var methods = TestMethods(type).Where(m => method is null || m.Name == method).ToList();
        return methods.Select(m => (type, m)).ToList();
    }

    public bool TypeExists(string className) => FindTypes(className).Count > 0;

    private List<Type> FindTypes(string className)
    {
        var full = _assembly.GetType(className);
        if (full is not null) return [full];
        return _assembly.GetTypes().Where(t => t.Name == className && !t.IsGenericTypeDefinition).ToList();
    }

    public int CaseCount(Type type, MethodInfo method)
    {
        var key = (type.FullName!, method.Name);
        if (_cases.Value.TryGetValue(key, out var n)) return n;
        // A type the assembly-wide discovery does not see (not public): ask for that type alone.
        var single = _typeCases.GetOrAdd(type.FullName!, name => Discover(name));
        return single.GetValueOrDefault(key);
    }

    public bool IsStaticallySkipped(MethodInfo method)
        => !string.IsNullOrEmpty(method.GetCustomAttribute<FactAttribute>(inherit: true)?.Skip)
           || method.GetCustomAttributes<DataAttribute>(inherit: true).Any(d => !string.IsNullOrEmpty(d.Skip));

    public TestRun RunOf(Type type, MethodInfo method)
    {
        if (type.IsAbstract || !(type.IsPublic || type.IsNestedPublic) || _cases.Value.Keys.All(k => k.Type != type.FullName))
            return TestRun.None;
        if (HasCategory(type, method, DockerRequirement.Category)) return TestRun.Docker;
        if (HasCategory(type, method, ImageRuntimeCategory)) return TestRun.ImageRuntime;
        return TestRun.InProcess;
    }

    // ---- xUnit discovery ----

    private Dictionary<(string Type, string Method), int> Discover(string? typeName)
    {
        var assemblyInfo = new ReflectionAssemblyInfo(_assembly);
        using var discoverer = new XunitTestFrameworkDiscoverer(assemblyInfo, new NoSourceInformation(), new NullMessageSink());
        using var sink = new DiscoverySink();
        var options = new DiscoveryOptions();
        if (typeName is null) discoverer.Find(includeSourceInformation: false, sink, options);
        else discoverer.Find(typeName, includeSourceInformation: false, sink, options);
        if (!sink.Finished.Wait(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("xUnit discovery did not finish");
        return sink.Cases
            .GroupBy(c => (c.TestMethod.TestClass.Class.Name, c.TestMethod.Method.Name))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private sealed class DiscoverySink : LongLivedMarshalByRefObject, IMessageSink, IDisposable
    {
        public ConcurrentBag<ITestCase> Cases { get; } = [];
        public ManualResetEventSlim Finished { get; } = new();

        public bool OnMessage(IMessageSinkMessage message)
        {
            if (message is ITestCaseDiscoveryMessage found) Cases.Add(found.TestCase);
            if (message is IDiscoveryCompleteMessage) Finished.Set();
            return true;
        }

        public void Dispose() => Finished.Dispose();
    }

    private sealed class NoSourceInformation : LongLivedMarshalByRefObject, ISourceInformationProvider
    {
        public ISourceInformation GetSourceInformation(ITestCase testCase) => new SourceInformation();
        public void Dispose() { }
    }

    /// <summary>The runner's defaults: theories pre-enumerated, as <c>dotnet test</c> does without a runner config.</summary>
    private sealed class DiscoveryOptions : ITestFrameworkDiscoveryOptions
    {
        private readonly Dictionary<string, object?> _values = new();
        public TValue GetValue<TValue>(string name) => _values.TryGetValue(name, out var v) && v is TValue t ? t : default!;
        public void SetValue<TValue>(string name, TValue value) => _values[name] = value;
    }
}
