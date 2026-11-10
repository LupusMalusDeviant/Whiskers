using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ModelContextProtocol.Server;
using Whiskers.Services.Agent;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// INV-38 / ADR-0005, R1-48: every call to a <see cref="MutatingOperationAttribute"/> method runs inside a gateway
/// decision for that capability, or for one that composes it (<see cref="MutationCompositions"/>). The test reads the IL
/// of every method in the Whiskers assembly and refuses every call that is not in such a context. The baseline file is
/// gone and nothing is allowed by list.
///
/// <para>A body is in a context when it is (a) a lambda handed to a runner (<see cref="IUiMutationRunner"/>,
/// <see cref="IAutomationMutationRunner"/>) next to a capability id literal (WP2 step 2.2/2.3); (b) part of an MCP tool
/// that maps to a capability — both dispatchers run such a tool only inside <see cref="IMutationGateway"/> (step 2.1);
/// (c) part of a mutating service's own implementation of a marked operation (update = stop + recreate); (d) part of a
/// <see cref="MutationHandlerAttribute"/> class, which names the one capability it executes; or (e) a helper method of
/// the same class that is only ever called from such contexts — it inherits all of them, and every call in it must be
/// allowed in each. In every context a call to another capability is allowed only when the decided one composes it.
/// The runtime scope guard applies the same rule (<see cref="MutationScope.Authorizes"/>).</para>
/// </summary>
public class MutationBypassArchitectureTests
{
    [Fact]
    public void No_call_to_a_mutating_operation_runs_outside_a_decision_that_covers_it()
    {
        var bypasses = MutationCallScanner.FindBypasses(typeof(Program).Assembly);

        Assert.True(bypasses.Count == 0,
            "Calls to mutating operations (or the host primitive) outside a Mutation Gateway decision that covers them. " +
            "Route them through IMutationGateway (a runner lambda with the capability literal), or, for a nested step of a " +
            "decided operation, list the pair in MutationCompositions:\n  " + string.Join("\n  ", bypasses));
    }

    /// <summary>INV-45 review round 12 (MINOR-4): the scan above trusts every <see cref="MutationEntryPointAttribute"/>
    /// method to run the lambda handed to it inside the gateway. Which methods carry it is pinned: the two runner
    /// interfaces and the scheduler's forwarding wrapper. A new one is a deliberate change of this list.</summary>
    [Fact]
    public void The_mutation_entry_points_are_exactly_the_runners_and_the_schedulers_forwarding_wrapper()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                                 | BindingFlags.DeclaredOnly;
        Type[] types;
        try { types = typeof(Program).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>().ToArray(); }
        var entryPoints = types
            .SelectMany(t => t.GetMethods(all))
            .Where(m => m.IsDefined(typeof(MutationEntryPointAttribute), false))
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
        [
            "Whiskers.Services.Mutations.IAutomationMutationRunner.RunAsync",
            "Whiskers.Services.Mutations.IAutomationMutationRunner.RunAsync",
            "Whiskers.Services.Mutations.IUiMutationRunner.RunAsync",
            "Whiskers.Services.Mutations.IUiMutationRunner.RunAsync",
            "Whiskers.Services.Scheduler.TaskExecutor.RunGatedAsync",
            "Whiskers.Services.Scheduler.TaskExecutor.RunGatedAsync",
        ], entryPoints);
    }

    /// <summary>INV-45 review round 12 (MINOR-4): the scheduler's wrapper is trusted only because it forwards — its body
    /// is one call of <c>_automation.RunAsync</c> with its own <c>capabilityId</c>, <c>arguments</c> and
    /// <c>action</c>, none of them reassigned; it calls nothing else (only <c>ActorOf(task)</c>), hands the action on
    /// once and returns exactly the runner's task (rounds 12 and 13) — so a lambda handed to it is gated for the
    /// capability written next to it.</summary>
    [Fact]
    public void The_schedulers_wrapper_forwards_its_own_capability_and_action_to_the_runner()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(
            Path.Combine(dir.FullName, "src", "Whiskers", "Services", "Scheduler", "TaskExecutor.cs"))).GetRoot();
        var wrappers = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == "RunGatedAsync").ToList();

        Assert.Equal(2, wrappers.Count);
        foreach (var wrapper in wrappers)
        {
            var calls = wrapper.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                .Where(i => i.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax
                {
                    Name.Identifier.Text: "RunAsync",
                } access && access.Expression.ToString() == "_automation")
                .ToList();
            var call = Assert.Single(calls);
            Assert.Equal(["ActorOf(task)", "capabilityId", "arguments", "action"],
                call.ArgumentList.Arguments.Select(a => a.ToString()));
            Assert.Equal(["task", "capabilityId", "arguments", "action"],
                wrapper.ParameterList.Parameters.Select(p => p.Identifier.Text));
            // None of the forwarded parameters is written to in the body.
            Assert.DoesNotContain(wrapper.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax>(),
                a => a.Left.ToString() is "capabilityId" or "arguments" or "action" or "task");
            // Nothing else is called — the wrapper never runs the action itself — and the action is handed on once.
            Assert.Equal(["ActorOf(task)", call.ToString()],
                wrapper.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                    .Select(i => i.ToString()).Order(StringComparer.Ordinal));
            Assert.Single(wrapper.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>(),
                n => n.Identifier.Text == "action");
            // What it returns is exactly the runner's task.
            var returned = Assert.Single(wrapper.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax>());
            var local = Assert.IsType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>(returned.Expression).Identifier.Text;
            var declared = Assert.Single(wrapper.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>(),
                v => v.Identifier.Text == local);
            Assert.Same(call, declared.Initializer?.Value);
            // …and nothing replaces it before it is returned: no assignment, no ref/out use of the local (round 14).
            Assert.DoesNotContain(wrapper.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax>(),
                a => a.Left.ToString() == local);
            Assert.DoesNotContain(wrapper.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax>(),
                a => a.RefKindKeyword.Text is "ref" or "out" && a.Expression.ToString() == local);
        }
    }

    [Fact]
    public void Every_capability_is_refused_at_run_time_outside_a_decision()
    {
        // R1-48: no pending exception is left — the first-run wizard's writes are narrow first-admin methods.
        var monitor = new MutationScopeMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<MutationScopeMonitor>.Instance);
        Assert.True(monitor.Enforce);
        foreach (var capability in new CapabilityCatalog().All.Select(c => c.Id))
            Assert.True(monitor.Enforces(capability), capability);
    }
    [Fact]
    public void The_bypass_baseline_is_gone()
    {
        // R1-48: the baseline only ever shrank; it ended empty and was removed with the allowance it carried.
        Assert.False(File.Exists(Path.Combine(McpToolCatalogSnapshotTests.RepositoryRoot(), "docs", "v1",
            "mutation-bypass-baseline.txt")));
    }

    [Fact]
    public void Scanner_sees_calls_made_inside_async_methods_and_lambdas()
    {
        var bypasses = MutationCallScanner.FindBypasses(typeof(MutationBypassArchitectureTests).Assembly);

        Assert.Contains($"{typeof(ScannerProbe).FullName} -> container.start", bypasses);
        Assert.Contains($"{typeof(ScannerProbe).FullName} -> container.stop", bypasses);
    }

    [Fact]
    public void A_handler_class_is_checked_against_the_capability_it_names()
    {
        var bypasses = MutationCallScanner.FindBypasses(typeof(MutationBypassArchitectureTests).Assembly);

        Assert.DoesNotContain($"{typeof(ScannerHandlerProbe).FullName} -> container.restart", bypasses);
        Assert.Contains($"{typeof(ScannerHandlerProbe).FullName} -> container.remove", bypasses);   // not composed
    }

    [Fact]
    public void A_lambda_handed_to_the_ui_runner_counts_as_gated_for_its_own_capability_and_what_it_composes_only()
    {
        var bypasses = MutationCallScanner.FindBypasses(typeof(MutationBypassArchitectureTests).Assembly);

        Assert.DoesNotContain($"{typeof(UiRunnerProbe).FullName} -> container.start", bypasses);
        Assert.DoesNotContain($"{typeof(UiRunnerProbe).FullName} -> container.restart", bypasses);
        Assert.Contains($"{typeof(UiRunnerProbe).FullName} -> container.stop", bypasses);      // other capability
        Assert.Contains($"{typeof(UiRunnerProbe).FullName} -> container.remove", bypasses);    // outside the lambda
        // app.deploy composes container.disable_restart and container.stop (contract 0013, D7); nothing else.
        Assert.DoesNotContain($"{typeof(ComposingProbe).FullName} -> container.stop", bypasses);
        Assert.Contains($"{typeof(ComposingProbe).FullName} -> container.update", bypasses);
    }

    [Fact]
    public void A_helper_inherits_the_contexts_of_its_callers_and_must_be_allowed_in_each()
    {
        var bypasses = MutationCallScanner.FindBypasses(typeof(MutationBypassArchitectureTests).Assembly);

        // Called only from a container.start lambda: covered.
        Assert.DoesNotContain($"{typeof(HelperProbe).FullName} -> container.start", bypasses);
        // Called from a container.stop lambda and from an ungated entry point: not covered.
        Assert.Contains($"{typeof(HelperProbe).FullName} -> container.stop", bypasses);
    }

    // Probes for the scanner's own test: an async method and a lambda that call a mutating method directly.
    private sealed class ScannerProbe(Whiskers.Services.Docker.IDockerService docker)
    {
        public async Task StartAsync() => await docker.StartContainerAsync("x");

        public Func<Task> Stop => () => docker.StopContainerAsync("x");
    }

    [MutationHandler("container.restart")]
    private sealed class ScannerHandlerProbe(Whiskers.Services.Docker.IDockerService docker)
    {
        public Task RestartAsync() => docker.RestartContainerAsync("x");

        public Task RemoveAsync() => docker.RemoveContainerAsync("x");
    }

    // A page-like caller: lambdas handed to the UI runner, sync and async, capturing and not.
    private sealed class UiRunnerProbe(IUiMutationRunner mutations, Whiskers.Services.Docker.IDockerService docker)
    {
        public async Task StartAsync(string id)
        {
            await mutations.RunAsync("container.start", new { containerId = id }, _ => docker.StartContainerAsync(id));
            await mutations.RunAsync("container.restart", new { containerId = id }, async _ =>
            {
                await docker.RestartContainerAsync(id);
                await docker.StopContainerAsync(id);   // not the gated capability → still a bypass
            });
            await docker.RemoveContainerAsync(id);     // outside any lambda → still a bypass
        }
    }

    private sealed class ComposingProbe(IUiMutationRunner mutations, Whiskers.Services.Docker.IDockerService docker)
    {
        public Task DeployAsync(string id) =>
            mutations.RunAsync("app.deploy", new { serverId = "local" }, async _ =>
            {
                await docker.StopContainerAsync(id);
                await docker.RecreateContainerAsync("x");   // container.update: not composed by app.deploy
            });
    }

    private sealed class HelperProbe(IUiMutationRunner mutations, Whiskers.Services.Docker.IDockerService docker)
    {
        public Task StartAsync(string id) => mutations.RunAsync("container.start", new { containerId = id }, _ => StartCore(id));

        public Task StopAsync(string id) => mutations.RunAsync("container.stop", new { containerId = id }, _ => StopCore(id));

        public Task StopUngatedAsync(string id) => StopCore(id);

        private Task StartCore(string id) => docker.StartContainerAsync(id);

        private Task StopCore(string id) => docker.StopContainerAsync(id);
    }
}

/// <summary>Reads method IL and reports <c>caller -> capability</c> pairs for calls outside the gateway.</summary>
internal static class MutationCallScanner
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static IReadOnlyCollection<string> FindBypasses(Assembly assembly)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        var uiGated = UiRunnerLambdas(assembly);
        foreach (var group in SafeTypes(assembly).GroupBy(OwnerOf))
        {
            var owner = group.Key;
            var bodies = group
                .SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)).Select(m => (Type: t, Method: m)))
                .ToList();
            var contexts = new OwnerContexts(owner, bodies, uiGated);
            foreach (var (type, method) in bodies)
            {
                var called = CalledCapabilities(method).ToList();
                if (called.Count == 0) continue;
                var alternatives = contexts.Of(type, method);
                foreach (var capability in called)
                    // Allowed only when every context the body can run in decided the capability or one composing it.
                    if (!alternatives.All(alt => alt.Any(decided => MutationCompositions.Allows(decided, capability))))
                        result.Add($"{owner.FullName} -> {capability}");
            }
        }
        return result;
    }

    /// <summary>
    /// The capabilities a body of <c>owner</c> runs under, as alternatives (one set per way it can be reached). A body
    /// is in a context when it is (a) a lambda handed to a runner with a capability literal, (b) part of an MCP tool
    /// that maps to a capability, (c) part of one of the owner's own [MutatingOperation] implementations, or (d) part of
    /// a [MutationHandler(capability)] class. A private helper method of the owner inherits the contexts of every place
    /// in the owner that calls it; any other method (other code can call it) and one reached from nowhere in the owner
    /// also has the empty context.
    /// </summary>
    private sealed class OwnerContexts
    {
        private static readonly IReadOnlyList<IReadOnlySet<string>> Ungated = [new HashSet<string>()];

        private readonly Type _owner;
        private readonly List<(Type Type, MethodBase Method)> _bodies;
        private readonly Dictionary<object, string> _uiGated;
        private readonly Dictionary<string, HashSet<string>> _named = new(StringComparer.Ordinal);
        private readonly HashSet<string>? _handler;
        private readonly Dictionary<string, IReadOnlyList<IReadOnlySet<string>>> _helpers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);
        private Dictionary<string, List<(Type, MethodBase)>>? _callers;

        public OwnerContexts(Type owner, List<(Type, MethodBase)> bodies, Dictionary<object, string> uiGated)
        {
            _owner = owner;
            _bodies = bodies;
            _uiGated = uiGated;
            if (owner.GetCustomAttribute<MutationHandlerAttribute>() is { } handler) _handler = [handler.CapabilityId];
            foreach (var (source, capability) in OwnMutatingOperations(owner))
                Add(source, capability);
            foreach (var (tool, capability) in GatewayDispatchedToolCapabilities(owner))
                Add(tool, capability);
        }

        private void Add(string source, string capability)
        {
            if (!_named.TryGetValue(source, out var set)) _named[source] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(capability);
        }

        public IReadOnlyList<IReadOnlySet<string>> Of(Type type, MethodBase method)
        {
            if (UiGatedCapability(_uiGated, method) is { } gate) return [new HashSet<string>([gate])];
            return OfSource(HandlerName(type, method));
        }

        private IReadOnlyList<IReadOnlySet<string>> OfSource(string source)
        {
            if (_handler is not null) return [_handler];
            if (_named.TryGetValue(source, out var named)) return [named];
            if (_helpers.TryGetValue(source, out var known)) return known;
            if (!_inProgress.Add(source)) return [];   // a cycle adds no way in of its own
            var alternatives = new List<IReadOnlySet<string>>();
            // A method other code can call (anything but private) is also reached from outside, ungated.
            if (_owner.GetMethods(All).Any(m => m.Name == source && !m.IsPrivate)
                || _owner.GetConstructors(All).Any(c => c.Name == source))
                alternatives.Add(Ungated[0]);
            foreach (var (type, caller) in Callers().GetValueOrDefault(source) ?? [])
                foreach (var alt in Of(type, caller))
                    if (!alternatives.Any(a => a.SetEquals(alt))) alternatives.Add(alt);
            _inProgress.Remove(source);
            var result = alternatives.Count == 0 ? Ungated : alternatives;
            _helpers[source] = result;
            return result;
        }

        /// <summary>Source method name → the bodies of the owner that call it (other than its own bodies).</summary>
        private Dictionary<string, List<(Type, MethodBase)>> Callers()
        {
            if (_callers is not null) return _callers;
            _callers = new Dictionary<string, List<(Type, MethodBase)>>(StringComparer.Ordinal);
            foreach (var (type, method) in _bodies)
            {
                var from = HandlerName(type, method);
                foreach (var callee in CalledMethods(method))
                {
                    if (callee.DeclaringType is not { } declaring || OwnerOf(declaring) != _owner) continue;
                    var to = HandlerName(declaring, callee);
                    if (to == from) continue;
                    if (!_callers.TryGetValue(to, out var list)) _callers[to] = list = [];
                    list.Add((type, method));
                }
            }
            return _callers;
        }
    }

    /// <summary>
    /// Every call to a [MutatingOperation] method made by <paramref name="component"/> (its own methods, lambdas and
    /// state machines), as the source-level handler it sits in, the capability it performs, and the capability of the
    /// runner lambda it runs inside (null when it is not inside one). R1-11 maps handlers to capabilities with it.
    /// </summary>
    public static IReadOnlyList<(string Handler, string Capability, string? GatedAs)> CallsIn(Type component)
    {
        var uiGated = UiRunnerLambdas(component.Assembly);
        var calls = new List<(string, string, string?)>();
        foreach (var type in SafeTypes(component.Assembly).Where(t => OwnerOf(t) == component))
            foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
            {
                var gated = UiGatedCapability(uiGated, method);
                foreach (var capability in CalledCapabilities(method))
                    calls.Add((HandlerName(type, method), capability, gated));
            }
        return calls;
    }

    /// <summary>The outermost source method a body belongs to: <c>&lt;&lt;SaveAll&gt;b__0&gt;d.MoveNext</c>,
    /// <c>&lt;SaveAll&gt;b__0</c> and <c>&lt;SaveAll&gt;d__12.MoveNext</c> are all <c>SaveAll</c>.</summary>
    private static string HandlerName(Type type, MethodBase method)
    {
        static string? Inner(string name)
        {
            var trimmed = name.TrimStart('<');
            var end = trimmed.IndexOf('>');
            return name.StartsWith('<') && end > 0 ? trimmed[..end] : null;
        }

        string? found = null;
        for (var t = type; t is not null && t != OwnerOf(type); t = t.DeclaringType)
            if (Inner(t.Name) is { Length: > 0 } fromType) found = fromType;
        return found ?? Inner(method.Name) ?? method.Name;
    }

    /// <summary>The [MutatingOperation] methods <paramref name="owner"/> implements (or, for an interface, declares with
    /// a default body), by the name of the method that holds the body → the capability.</summary>
    private static IEnumerable<(string Source, string Capability)> OwnMutatingOperations(Type owner)
    {
        if (owner.IsInterface)
        {
            foreach (var (method, capability) in MutatingMethods.Of(owner))
                if (method.DeclaringType == owner) yield return (method.Name, capability);
            yield break;
        }
        foreach (var iface in owner.GetInterfaces())
        {
            var marked = MutatingMethods.Of(iface);
            if (marked.Count == 0) continue;
            InterfaceMapping map;
            try { map = owner.GetInterfaceMap(iface); }
            catch (ArgumentException) { continue; }
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
                if (marked.TryGetValue(map.InterfaceMethods[i], out var capability) && map.TargetMethods[i].DeclaringType == owner)
                    yield return (map.TargetMethods[i].Name, capability);
        }
    }

    private static readonly CapabilityCatalog Catalog = new();

    /// <summary>Methods of <paramref name="owner"/> that are MCP tools mapped to a capability → that capability.</summary>
    private static Dictionary<string, string> GatewayDispatchedToolCapabilities(Type owner)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in owner.GetMethods(All))
            if (m.GetCustomAttribute<McpServerToolAttribute>() is { } tool
                && Catalog.FindByMcpTool(tool.Name ?? AgentToolRegistry.ToSnakeCase(m.Name)) is { } capability)
                map[m.Name] = capability.Id;
        return map;
    }

    /// <summary>
    /// Lambdas handed to a <see cref="MutationEntryPointAttribute"/> runner: in every method body, a capability id loaded with
    /// <c>ldstr</c>, then a lambda loaded with <c>ldftn</c>, then the call to <c>RunAsync</c>. Keyed by the lambda's
    /// body and, for an async lambda, by its state machine type.
    /// </summary>
    private static Dictionary<object, string> UiRunnerLambdas(Assembly assembly)
    {
        var map = new Dictionary<object, string>();
        foreach (var type in SafeTypes(assembly))
            foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
            {
                byte[]? il;
                try { il = method.GetMethodBody()?.GetILAsByteArray(); }
                catch { continue; }
                if (il is null) continue;

                var module = method.Module;
                var typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
                var methodArgs = method is MethodInfo { IsGenericMethod: true } gm ? gm.GetGenericArguments() : null;
                string? capability = null;
                MethodBase? lambda = null;
                var pos = 0;
                while (pos < il.Length)
                {
                    short value = il[pos++];
                    if (value == 0xFE && pos < il.Length) value = (short)(0xFE00 | il[pos++]);
                    if (!OpCodesByValue.TryGetValue(value, out var op)) break;

                    if (op.OperandType == OperandType.InlineString)
                    {
                        string? text = null;
                        try { text = module.ResolveString(BitConverter.ToInt32(il, pos)); } catch { }
                        if (text is not null && Catalog.Find(text) is not null) capability = text;
                    }
                    else if (op == OpCodes.Ldftn)
                    {
                        try { lambda = module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, methodArgs); }
                        catch { lambda = null; }
                    }
                    else if (op.OperandType == OperandType.InlineMethod)
                    {
                        MethodBase? called = null;
                        try { called = module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, methodArgs); } catch { }
                        if (called is not null && IsEntryPoint(called) && capability is not null && lambda is not null)
                        {
                            map[Key(lambda)] = capability;
                            if (lambda.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType is { } machine)
                                map[machine] = capability;
                            capability = null;
                            lambda = null;
                        }
                    }
                    pos += OperandSize(op, il, pos);
                }
            }
        return map;
    }

    private static object Key(MethodBase method) => (method.Module, method.MetadataToken);

    private static bool IsEntryPoint(MethodBase called)
    {
        var definition = called is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : called;
        return definition.IsDefined(typeof(MutationEntryPointAttribute), false);
    }

    private static string? UiGatedCapability(Dictionary<object, string> gated, MethodBase method) =>
        gated.GetValueOrDefault(Key(method))
        ?? (method.DeclaringType is { } t && gated.TryGetValue(t, out var viaMachine) ? viaMachine : null);

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    /// <summary>Compiler-generated state machines, closures and lambdas belong to the type that declared them.</summary>
    private static Type OwnerOf(Type type)
    {
        while (type.DeclaringType is not null
               && (type.Name.Contains('<') || type.IsDefined(typeof(CompilerGeneratedAttribute), false)))
            type = type.DeclaringType;
        return type;
    }

    private static IEnumerable<string> CalledCapabilities(MethodBase method) =>
        CalledMethods(method).OfType<MethodInfo>().Select(CapabilityOf).OfType<string>();

    /// <summary>Every method <paramref name="method"/>'s IL references as a call or a delegate (call, callvirt, newobj,
    /// ldftn, ldvirtftn): a method group handed on runs where the delegate runs.</summary>
    internal static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { yield break; }
        if (il is null) yield break;

        var module = method.Module;
        var typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        var methodArgs = method is MethodInfo { IsGenericMethod: true } mi ? mi.GetGenericArguments() : null;

        var pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE && pos < il.Length) value = (short)(0xFE00 | il[pos++]);
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break;   // unknown byte: stop, never guess

            if (op.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, pos);
                MethodBase? target = null;
                try { target = module.ResolveMethod(token, typeArgs, methodArgs); }
                catch { /* token from a generic context we cannot close: not a service call */ }
                if (target is not null) yield return target;
            }
            pos += OperandSize(op, il, pos);
        }
    }

    /// <summary>Every string literal (<c>ldstr</c>) of <paramref name="method"/>'s IL (contract 0013, D7).</summary>
    internal static IEnumerable<string> StringLiterals(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { yield break; }
        if (il is null) yield break;
        var pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE && pos < il.Length) value = (short)(0xFE00 | il[pos++]);
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break;   // unknown byte: stop, never guess
            if (op.OperandType == OperandType.InlineString)
            {
                string? text = null;
                try { text = method.Module.ResolveString(BitConverter.ToInt32(il, pos)); } catch { }
                if (text is not null) yield return text;
            }
            pos += OperandSize(op, il, pos);
        }
    }

    /// <summary>Every method and constructor body of <paramref name="assembly"/> with its owner (closures and state
    /// machines belong to the type that declared them) and its handler name.</summary>
    internal static IEnumerable<(Type Owner, string Handler, MethodBase Body)> Bodies(Assembly assembly) =>
        SafeTypes(assembly)
            .SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All))
                .Select(m => (OwnerOf(t), HandlerName(t, m), m)));

    private static string? CapabilityOf(MethodInfo called)
    {
        var declaring = called.DeclaringType;
        if (declaring is null) return null;
        if (declaring.IsInterface)
            return called.GetCustomAttribute<MutatingOperationAttribute>()?.CapabilityId;

        // A direct call on the implementing class (DockerService.StartContainerAsync) counts as well.
        foreach (var iface in declaring.GetInterfaces().Where(i => MutatingMethods.Of(i).Count > 0))
        {
            var map = declaring.GetInterfaceMap(iface);
            var index = Array.IndexOf(map.TargetMethods, called);
            if (index >= 0 && MutatingMethods.Of(iface).TryGetValue(map.InterfaceMethods[index], out var capability))
                return capability;
        }
        return null;
    }

    /// <summary>Calls to a <see cref="MutationPrimitiveAttribute"/> method in <paramref name="assembly"/>, as
    /// (owner, handler) pairs.</summary>
    public static IReadOnlyList<(Type Owner, string Handler)> PrimitiveCallers(Assembly assembly) =>
        Callers(assembly, m => m.IsDefined(typeof(MutationPrimitiveAttribute), false));

    /// <summary>Calls to methods <paramref name="match"/> accepts, as (owner, handler) pairs.</summary>
    public static IReadOnlyList<(Type Owner, string Handler)> Callers(Assembly assembly, Func<MethodInfo, bool> match) =>
        SafeTypes(assembly)
            .SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)).Select(m => (Type: t, Method: m)))
            .Where(b => CalledMethods(b.Method).OfType<MethodInfo>().Any(match))
            .Select(b => (OwnerOf(b.Type), HandlerName(b.Type, b.Method)))
            .Distinct()
            .ToList();

    /// <summary>The owners of every place in <paramref name="assembly"/> that constructs <paramref name="constructed"/>.</summary>
    public static IReadOnlyList<Type> Constructors(Assembly assembly, Type constructed) =>
        SafeTypes(assembly)
            .SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)).Select(m => (Type: t, Method: m)))
            .Where(b => CalledMethods(b.Method).OfType<ConstructorInfo>().Any(c => c.DeclaringType == constructed))
            .Select(b => OwnerOf(b.Type))
            .Distinct()
            .ToList();

    private static int OperandSize(OpCode op, byte[] il, int pos) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
        _ => 4,
    };
}
