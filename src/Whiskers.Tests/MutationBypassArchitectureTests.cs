using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ModelContextProtocol.Server;
using Whiskers.Services.Agent;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// INV-38 / ADR-0005: every call to a <see cref="MutatingOperationAttribute"/> method must come from the Mutation
/// Gateway or a <see cref="MutationHandlerAttribute"/> class. The test reads the IL of every method in the Whiskers
/// assembly, collects the callers that break that rule, and compares them with a checked-in baseline.
///
/// <para>Inside an MCP tool method whose tool maps to a capability, calls to <b>that</b> capability count as inside
/// the gateway: both dispatchers (<c>McpGuardrailFilter</c> for direct calls, <c>AgentToolInvoker</c> for the agent)
/// run such a tool only inside <see cref="IMutationGateway"/> (WP2 step 2.1). Calls to any other capability from a
/// tool (deploy_app creating a container, a preview starting a background cycle) stay reported: the gateway decided
/// on the tool's capability, not on theirs.</para>
///
/// <para>The baseline (<c>docs/v1/mutation-bypass-baseline.txt</c>) lists today's bypasses. It may only shrink: a
/// new bypass fails the test, and so does a baseline line that no longer occurs — remove it in the PR that migrates
/// the path. WP2 step 2.6 ends with an empty baseline. Regenerate with <c>WHISKERS_WRITE_MUTATION_BASELINE=1</c>
/// only when a reviewer accepts the diff.</para>
/// </summary>
public class MutationBypassArchitectureTests
{
    private const string BaselinePath = "docs/v1/mutation-bypass-baseline.txt";

    [Fact]
    public void Calls_to_mutating_operations_outside_the_gateway_match_the_baseline()
    {
        var actual = MutationCallScanner.FindBypasses(typeof(Program).Assembly);
        var path = Path.Combine(McpToolCatalogSnapshotTests.RepositoryRoot(), BaselinePath.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable("WHISKERS_WRITE_MUTATION_BASELINE") == "1")
        {
            File.WriteAllText(path, Render(actual));
            return;
        }

        var baseline = File.Exists(path)
            ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var added = actual.Where(a => !baseline.Contains(a)).ToList();
        var gone = baseline.Where(b => !actual.Contains(b)).OrderBy(b => b, StringComparer.Ordinal).ToList();

        Assert.True(added.Count == 0,
            "New calls to mutating operations outside the Mutation Gateway. Route them through IMutationGateway " +
            "(or a [MutationHandler] class):\n  " + string.Join("\n  ", added));
        Assert.True(gone.Count == 0,
            $"These bypasses no longer exist — delete them from {BaselinePath}:\n  " + string.Join("\n  ", gone));
    }

    [Fact]
    public void Scanner_sees_calls_made_inside_async_methods_and_lambdas()
    {
        var bypasses = MutationCallScanner.FindBypasses(typeof(MutationBypassArchitectureTests).Assembly);

        Assert.Contains($"{typeof(ScannerProbe).FullName} -> container.start", bypasses);
        Assert.Contains($"{typeof(ScannerProbe).FullName} -> container.stop", bypasses);
        Assert.DoesNotContain(bypasses, b => b.StartsWith(typeof(ScannerHandlerProbe).FullName!, StringComparison.Ordinal));
    }

    private static string Render(IReadOnlyCollection<string> lines) =>
        "# Calls to [MutatingOperation] methods that do not go through the Mutation Gateway (ADR-0005, INV-38).\n" +
        "# Format: <calling type> -> <capability>. This list may only shrink; see MutationBypassArchitectureTests.\n" +
        string.Join("\n", lines) + "\n";

    // Probes for the scanner's own test: an async method and a lambda that call a mutating method directly.
    private sealed class ScannerProbe(Whiskers.Services.Docker.IDockerService docker)
    {
        public async Task StartAsync() => await docker.StartContainerAsync("x");

        public Func<Task> Stop => () => docker.StopContainerAsync("x");
    }

    [MutationHandler]
    private sealed class ScannerHandlerProbe(Whiskers.Services.Docker.IDockerService docker)
    {
        public Task RestartAsync() => docker.RestartContainerAsync("x");
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
        foreach (var type in SafeTypes(assembly))
        {
            var owner = OwnerOf(type);
            if (owner.IsDefined(typeof(MutationHandlerAttribute), false) || owner == typeof(MutationGateway)) continue;
            var ownOperations = OwnMutatingOperationNames(owner);
            var gatewayTools = GatewayDispatchedToolCapabilities(owner);

            var members = type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));
            foreach (var method in members)
            {
                // A mutating service composing other operations inside one of its own mutating operations (update =
                // stop + recreate) runs inside the outer operation's scope; that is not a bypass.
                var source = SourceMethodName(type, method);
                if (ownOperations.Contains(source)) continue;
                var toolCapability = gatewayTools.GetValueOrDefault(source);
                foreach (var capability in CalledCapabilities(method))
                    if (capability != toolCapability)
                        result.Add($"{owner.FullName} -> {capability}");
            }
        }
        return result;
    }

    /// <summary>Names of the [MutatingOperation] interface methods <paramref name="owner"/> implements.</summary>
    private static HashSet<string> OwnMutatingOperationNames(Type owner) =>
        owner.IsInterface
            ? []
            : owner.GetInterfaces().SelectMany(i => MutatingMethods.Of(i).Keys).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

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

    /// <summary>The source-level method a compiled body belongs to: <c>&lt;RunAsync&gt;d__4.MoveNext</c> and
    /// <c>&lt;RunAsync&gt;b__0</c> both map to <c>RunAsync</c>.</summary>
    private static string SourceMethodName(Type type, MethodBase method)
    {
        static string? Inner(string name) =>
            name.StartsWith('<') && name.IndexOf('>') is var end and > 1 ? name[1..end] : null;

        if (Inner(method.Name) is { } fromMethod) return fromMethod;
        for (var t = type; t is not null; t = t.DeclaringType)
            if (Inner(t.Name) is { } fromType && fromType.Length > 0) return fromType;
        return method.Name;
    }

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

    private static IEnumerable<string> CalledCapabilities(MethodBase method)
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
                if (target is MethodInfo called && CapabilityOf(called) is { } capability)
                    yield return capability;
            }
            pos += OperandSize(op, il, pos);
        }
    }

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
