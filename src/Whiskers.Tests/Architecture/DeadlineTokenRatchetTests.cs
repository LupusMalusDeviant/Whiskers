using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0023 (the token ratchet, INV-40): no new gateway action may discard its deadline token. The places that
/// still do are listed in <c>Architecture/deadline-token-baseline.txt</c>, one line per enclosing method and capability
/// (<c>&lt;file&gt;|&lt;method&gt;|&lt;capability&gt;|&lt;count&gt;</c>); the list can only shrink.
/// </summary>
public sealed class DeadlineTokenRatchetTests
{
    private const string BaselineName = "deadline-token-baseline.txt";

    private static string BaselinePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "Whiskers.Tests", "Architecture", BaselineName);
    }

    private static SortedDictionary<string, int> ReadBaseline(string path)
    {
        var lines = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cut = line.LastIndexOf('|');
            Assert.True(cut > 0 && int.TryParse(line[(cut + 1)..], out _), $"Baseline line is not '<file>|<method>|<capability>|<count>': {line}");
            lines.Add(line[..cut], int.Parse(line[(cut + 1)..]));
        }
        return lines;
    }

    [Fact]
    public void The_baseline_matches_the_compiled_actions_exactly()
    {
        var assembly = typeof(MutationGateway).Assembly;
        Assert.True(File.Exists(Path.ChangeExtension(assembly.Location, ".pdb")),
            "Whiskers.pdb is missing next to Whiskers.dll: the ratchet needs it to name each site's file.");
        var sites = DeadlineTokenScan.Sites(assembly);
        // The scan must see the gateway actions at all, or an empty result would pass.
        Assert.True(sites.Count > 100, $"only {sites.Count} gateway actions found; the scan no longer sees them");
        Assert.DoesNotContain(sites, s => s.File.StartsWith('?'));
        // An action the scan cannot see (held in a field or handed in from elsewhere) cannot be judged: load it in the
        // calling method, as a lambda or method group.
        var unresolved = sites.Where(s => s.Unresolved).Select(s => $"{s.Key} ({s.Entry.Name})").ToList();
        Assert.True(unresolved.Count == 0,
            "Gateway calls whose action the token ratchet cannot see (contract 0023): " + string.Join(", ", unresolved));

        var actual = DeadlineTokenScan.Discarding(sites);
        var path = BaselinePath();
        var baseline = ReadBaseline(path);

        var problems = new List<string>();
        foreach (var (key, count) in actual)
        {
            if (!baseline.TryGetValue(key, out var allowed))
                problems.Add($"New action that discards its deadline token: {key} ({count}). Pass the token on to the " +
                             "service call (contract 0023, INV-40).");
            else if (count > allowed)
                problems.Add($"More actions that discard their deadline token than the baseline allows: {key} " +
                             $"({count} > {allowed}). Pass the token on (contract 0023, INV-40).");
            else if (count < allowed)
                problems.Add($"Fewer discarding actions than the baseline says: {key} ({count} < {allowed}). Lower the " +
                             $"line in {BaselineName}.");
        }
        foreach (var key in baseline.Keys.Where(k => !actual.ContainsKey(k)))
            problems.Add($"Baseline line no longer matches any discarding action: {key}. Remove it from {BaselineName}.");

        if (problems.Count > 0)
            File.WriteAllLines(path + ".actual", actual.Select(kv => $"{kv.Key}|{kv.Value}"));
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Theory]
    [InlineData(nameof(DeadlineTokenFixtures.Discards))]
    [InlineData(nameof(DeadlineTokenFixtures.OnlyThrows))]
    [InlineData(nameof(DeadlineTokenFixtures.PassesNone))]
    [InlineData(nameof(DeadlineTokenFixtures.MethodGroup))]
    [InlineData(nameof(DeadlineTokenFixtures.VirtualMethodGroup))]
    [InlineData(nameof(DeadlineTokenFixtures.IgnoresUnnamed))]
    [InlineData(nameof(DeadlineTokenFixtures.DiscardsAsync))]
    [InlineData(nameof(DeadlineTokenFixtures.LocalFunction))]
    [InlineData(nameof(DeadlineTokenFixtures.DiscardsAtGateway))]
    [InlineData(nameof(DeadlineTokenFixtures.LogsOnly))]
    [InlineData(nameof(DeadlineTokenFixtures.ComparesOnly))]
    public void A_discarding_action_is_found(string method)
    {
        var site = Assert.Single(FixtureSites(method));
        Assert.NotNull(site.Action);
        Assert.False(site.PassesToken);
    }

    [Theory]
    [InlineData(nameof(DeadlineTokenFixtures.Passes))]
    [InlineData(nameof(DeadlineTokenFixtures.PassesThroughLocal))]
    [InlineData(nameof(DeadlineTokenFixtures.PassesAcrossAwait))]
    [InlineData(nameof(DeadlineTokenFixtures.PassesToGateway))]
    [InlineData(nameof(DeadlineTokenFixtures.PassesThroughClosure))]
    public void An_action_passing_its_token_on_is_not_found(string method) =>
        Assert.True(Assert.Single(FixtureSites(method)).PassesToken);

    [Fact]
    public void Every_action_a_call_may_hand_on_is_judged_also_the_branches_of_a_conditional()
    {
        var sites = FixtureSites(nameof(DeadlineTokenFixtures.Conditional));
        Assert.Equal(2, sites.Count);
        Assert.Single(sites, s => !s.PassesToken);
        Assert.Single(sites, s => s.PassesToken);
    }

    [Fact]
    public void A_call_whose_action_is_not_loaded_in_the_calling_method_is_unresolved()
    {
        var site = Assert.Single(FixtureSites(nameof(DeadlineTokenFixtures.HeldInField)));
        Assert.Null(site.Action);
        Assert.True(site.Unresolved);
    }

    [Fact]
    public void A_runner_called_on_its_class_is_an_entry_point_too()
    {
        var implementation = typeof(UiMutationRunner).GetMethods()
            .First(m => m.Name == nameof(IUiMutationRunner.RunAsync) && m.GetGenericArguments().Length == 2
                                                                     && m.DeclaringType == typeof(UiMutationRunner));
        Assert.True(DeadlineTokenScan.IsEntryPoint(implementation));
    }

    [Fact]
    public void A_capability_consumed_by_a_helper_does_not_name_a_later_call()
    {
        var site = Assert.Single(FixtureSites(nameof(DeadlineTokenFixtures.AfterHelper)));
        Assert.Equal("*", site.Capability);
    }

    [Fact]
    public void A_site_is_keyed_by_file_enclosing_method_and_capability_never_by_a_generated_name()
    {
        var site = Assert.Single(FixtureSites(nameof(DeadlineTokenFixtures.Discards)));
        Assert.Equal("Architecture/DeadlineTokenFixtures.cs|Discards|container.restart", site.Key);
    }

    [Fact]
    public void An_unrelated_lambda_added_to_the_same_type_leaves_the_baseline_valid()
    {
        // The second fixture type is the first with unrelated lambdas in front: every generated name shifts, the keys
        // do not.
        var first = DeadlineTokenScan.Discarding(DeadlineTokenScan.Sites(typeof(DeadlineTokenFixtures).Assembly,
            [typeof(DeadlineTokenFixtures)]));
        var second = DeadlineTokenScan.Discarding(DeadlineTokenScan.Sites(typeof(DeadlineTokenFixtures).Assembly,
            [typeof(DeadlineTokenFixturesWithMore)]));
        const string key = "Architecture/DeadlineTokenFixtures.cs|Discards|container.restart";
        Assert.Equal(1, first.GetValueOrDefault(key));
        Assert.Equal(1, second.GetValueOrDefault(key));
    }

    [Theory]
    [InlineData("<OnSaveAsync>b__42_0", "OnSaveAsync")]
    [InlineData("<BuildRenderTree>g__Local|3_1", "BuildRenderTree")]
    [InlineData("Plain", "Plain")]
    public void The_enclosing_method_is_the_user_methods_name(string generated, string expected)
    {
        var method = new FakeMethod(generated);
        Assert.Equal(expected, DeadlineTokenScan.EnclosingMethod(method));
    }

    private static IReadOnlyList<DeadlineTokenScan.Site> FixtureSites(string method) =>
        DeadlineTokenScan.Sites(typeof(DeadlineTokenFixtures).Assembly, [typeof(DeadlineTokenFixtures)])
            .Where(s => s.Method == method)
            .ToList();

    /// <summary>Only the name matters to <see cref="DeadlineTokenScan.EnclosingMethod"/>.</summary>
    private sealed class FakeMethod(string name) : System.Reflection.MethodInfo
    {
        public override string Name => name;
        public override Type? DeclaringType => typeof(FakeMethod);
        public override Type? ReflectedType => DeclaringType;
        public override System.Reflection.MethodAttributes Attributes => default;
        public override RuntimeMethodHandle MethodHandle => default;
        public override System.Reflection.ICustomAttributeProvider ReturnTypeCustomAttributes => null!;
        public override System.Reflection.MethodInfo GetBaseDefinition() => this;
        public override object[] GetCustomAttributes(bool inherit) => [];
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => [];
        public override System.Reflection.MethodImplAttributes GetMethodImplementationFlags() => default;
        public override System.Reflection.ParameterInfo[] GetParameters() => [];
        public override object? Invoke(object? obj, System.Reflection.BindingFlags invokeAttr,
            System.Reflection.Binder? binder, object?[]? parameters, System.Globalization.CultureInfo? culture) => null;
        public override bool IsDefined(Type attributeType, bool inherit) => false;
    }
}
