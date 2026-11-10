using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0012 (D10): follows, in IL, what a lambda handed to a <see cref="MutationEntryPointAttribute"/> runner passes
/// to a <see cref="MutatingOperationAttribute"/> method. A value counts as the decided object only when it comes from the
/// lambda's <see cref="ResolvedTarget"/> parameter — loaded directly, through a local or a state machine field holding
/// it, or as a property chain on it (<c>c.Id</c>, <c>c.ServerId</c>). A captured local, a field of the page, a literal
/// or a method result is a finding. A call to an <see cref="InventoryLookupAttribute"/> method inside such a lambda is a
/// finding too. The walk is a linear abstract interpretation of the stack — the compiler keeps the stack empty between
/// statements, so a branch merges nothing a call argument depends on; where it does (<c>?? "local"</c>), the last value
/// pushed wins, which is the fallback the check is meant to catch.
/// </summary>
internal static class ResolvedTargetFlowScanner
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly CapabilityCatalog Catalog = new();

    /// <summary>One lambda handed to an entry point: where, for which capability (<c>null</c>: not a literal), and
    /// whether the overload hands it the resolved object.</summary>
    public sealed record Site(Type Owner, string Handler, string? Capability, MethodBase Lambda, bool Typed)
    {
        public string Key => $"{Owner.FullName}.{Handler} -> {Capability ?? "<variable>"}";
    }

    /// <summary>Every lambda handed to an entry point in <paramref name="types"/>.</summary>
    public static IReadOnlyList<Site> Sites(IEnumerable<Type> types)
    {
        var sites = new List<Site>();
        foreach (var type in types)
            foreach (var method in Bodies(type))
            {
                string? capability = null;
                MethodBase? lambda = null;
                foreach (var (op, operand) in Instructions(method))
                {
                    if (op.OperandType == OperandType.InlineString && operand is string text && Catalog.Find(text) is not null)
                        capability = text;
                    else if (op == OpCodes.Ldftn && operand is MethodBase fn)
                        lambda = fn;
                    else if (op.OperandType == OperandType.InlineMethod && operand is MethodBase called && IsEntryPoint(called))
                    {
                        if (lambda is not null)
                            sites.Add(new Site(OwnerOf(type), HandlerName(type, method), capability, lambda, IsTyped(called)));
                        capability = null;
                        lambda = null;
                    }
                }
            }
        return sites;
    }

    /// <summary>The findings of one typed site (D10 items 1 and 2), as text.</summary>
    public static IReadOnlyList<string> Findings(Site site, IReadOnlySet<string> lookupExceptions)
    {
        var findings = new List<string>();
        var lambda = site.Lambda;
        var parameters = lambda.GetParameters();
        if (parameters.Length == 0 || !typeof(ResolvedTarget).IsAssignableFrom(parameters[0].ParameterType))
            return [$"{site.Key}: the lambda takes no resolved object"];
        Analyze(site, lambda, new HashSet<int> { 0 }, lookupExceptions, findings, new HashSet<MethodBase>(), depth: 0);
        return findings;
    }

    /// <summary>Walks <paramref name="method"/> (and its async state machine) with the parameters at
    /// <paramref name="targetParameters"/> holding the decided object; a helper of the owner it hands that object to is
    /// walked the same way — it inherits the lambda's context (D10 item 1).</summary>
    private static void Analyze(Site site, MethodBase method, IReadOnlySet<int> targetParameters,
        IReadOnlySet<string> lookupExceptions, List<string> findings, HashSet<MethodBase> seen, int depth)
    {
        if (depth > 4 || !seen.Add(method)) return;
        var parameters = method.GetParameters();
        var names = targetParameters.Where(i => i < parameters.Length).Select(i => parameters[i].Name).ToHashSet();
        var shift = method.IsStatic ? 0 : 1;
        Walk(site, method, arg => targetParameters.Contains(arg - shift), _ => false, lookupExceptions, findings, seen, depth);
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType is { } machine
            && machine.GetMethod("MoveNext", All) is { } moveNext)
            Walk(site, moveNext, _ => false, f => f.DeclaringType == machine && names.Contains(f.Name), lookupExceptions,
                findings, seen, depth);
    }

    private enum Origin { Other, Target, Literal, Captured }

    private static void Walk(Site site, MethodBase body, Func<int, bool> isTargetArg, Func<FieldInfo, bool> isTargetField,
        IReadOnlySet<string> lookupExceptions, List<string> findings, HashSet<MethodBase> seen, int depth)
    {
        var stack = new List<Origin>();
        var locals = new Dictionary<int, Origin>();
        var fields = new Dictionary<FieldInfo, Origin>();
        var handlers = new HashSet<int>();
        try
        {
            foreach (var clause in body.GetMethodBody()?.ExceptionHandlingClauses ?? [])
                if (clause.Flags is ExceptionHandlingClauseOptions.Clause or ExceptionHandlingClauseOptions.Filter)
                    handlers.Add(clause.HandlerOffset);
        }
        catch (InvalidOperationException) { }

        Origin Pop() { if (stack.Count == 0) return Origin.Other; var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
        void Push(Origin o) => stack.Add(o);

        foreach (var (op, operand, offset) in InstructionsWithOffsets(body))
        {
            if (handlers.Contains(offset)) { stack.Clear(); Push(Origin.Other); }
            switch (op.Name)
            {
                case "ldarg.0": case "ldarg.1": case "ldarg.2": case "ldarg.3": case "ldarg.s": case "ldarg":
                {
                    var index = operand is int n1 ? n1 : op.Name[6] - '0';
                    Push(isTargetArg(index) ? Origin.Target : Origin.Other);
                    continue;
                }
                case "ldloc.0": case "ldloc.1": case "ldloc.2": case "ldloc.3": case "ldloc.s": case "ldloc":
                    Push(locals.GetValueOrDefault(operand is int n2 ? n2 : op.Name[6] - '0'));
                    continue;
                case "stloc.0": case "stloc.1": case "stloc.2": case "stloc.3": case "stloc.s": case "stloc":
                    locals[operand is int n3 ? n3 : op.Name[6] - '0'] = Pop();
                    continue;
                case "ldstr": case "ldnull":
                    Push(Origin.Literal);
                    continue;
                case "dup":
                {
                    var top = Pop();
                    Push(top);
                    Push(top);
                    continue;
                }
                case "leave": case "leave.s": case "endfinally": case "ret": case "throw": case "rethrow":
                    stack.Clear();
                    continue;
                case "ldfld":
                {
                    Pop();
                    var field = operand as FieldInfo;
                    Push(field is null ? Origin.Other
                        : isTargetField(field) ? Origin.Target
                        : fields.TryGetValue(field, out var held) ? held
                        // A field of a closure or of the page: captured from outside the lambda.
                        : Origin.Captured);
                    continue;
                }
                case "ldsfld":
                    Push(Origin.Captured);
                    continue;
                case "stfld":
                {
                    var value = Pop();
                    Pop();
                    if (operand is FieldInfo field) fields[field] = value;
                    continue;
                }
            }

            if (op.FlowControl is FlowControl.Call && operand is MethodBase called)
            {
                var parameters = called.GetParameters();
                var args = new Origin[parameters.Length];
                for (var i = parameters.Length - 1; i >= 0; i--) args[i] = Pop();
                var instance = called.IsStatic || op == OpCodes.Newobj ? Origin.Other : Pop();
                Check(site, called, parameters, args, lookupExceptions, findings);
                // A helper of the owner (not another runner lambda) runs in this lambda's context: walk it too, with the
                // parameters that received the decided object.
                if (called.DeclaringType is { } helperType && OwnerOf(helperType) == site.Owner && !IsEntryPoint(called)
                    && called.GetMethodBody() is not null)
                {
                    var targets = Enumerable.Range(0, args.Length).Where(i => args[i] == Origin.Target).ToHashSet();
                    Analyze(site, called, targets, lookupExceptions, findings, seen, depth + 1);
                }
                var returns = called is MethodInfo mi ? mi.ReturnType != typeof(void) : op == OpCodes.Newobj;
                if (op == OpCodes.Newobj) returns = true;
                // A property read on the decided object is the decided object's own value.
                if (returns)
                    Push(instance == Origin.Target && called.Name.StartsWith("get_", StringComparison.Ordinal) && parameters.Length == 0
                        ? Origin.Target : Origin.Other);
                continue;
            }

            for (var i = PopCount(op.StackBehaviourPop); i > 0; i--) Pop();
            for (var i = PushCount(op.StackBehaviourPush); i > 0; i--) Push(Origin.Other);
        }
    }

    private static void Check(Site site, MethodBase called, ParameterInfo[] parameters, Origin[] args,
        IReadOnlySet<string> lookupExceptions, List<string> findings)
    {
        if (called is not MethodInfo method) return;
        if (IsInventoryLookup(method))
        {
            var name = $"{method.DeclaringType?.Name}.{method.Name}";
            if (!lookupExceptions.Contains($"{site.Key}: {name}"))
                findings.Add($"{site.Key}: looks its target up again ({name})");
        }
        if (InterfaceMethodOf(method) is not { } operation) return;
        if (operation.GetCustomAttribute<MutatingOperationAttribute>() is null) return;
        var operationParameters = operation.GetParameters();
        for (var i = 0; i < operationParameters.Length && i < args.Length; i++)
        {
            var p = operationParameters[i];
            var bound = p.IsDefined(typeof(MutationTargetAttribute), false)
                        || string.Equals(p.Name, "serverId", StringComparison.Ordinal);
            if (bound && args[i] != Origin.Target)
                findings.Add($"{site.Key}: {operation.DeclaringType?.Name}.{operation.Name}({p.Name}) gets a " +
                             $"{args[i].ToString().ToLowerInvariant()} value, not the resolved object");
        }
    }

    /// <summary>The interface method a call resolves to (itself for an interface call, the mapped one for a direct
    /// call on an implementation).</summary>
    private static MethodInfo? InterfaceMethodOf(MethodInfo called)
    {
        if (called.DeclaringType is not { } declaring) return null;
        if (declaring.IsInterface) return called;
        foreach (var iface in declaring.GetInterfaces())
        {
            InterfaceMapping map;
            try { map = declaring.GetInterfaceMap(iface); }
            catch (ArgumentException) { continue; }
            var index = Array.IndexOf(map.TargetMethods, called);
            if (index >= 0) return map.InterfaceMethods[index];
        }
        return null;
    }

    private static bool IsInventoryLookup(MethodInfo called) =>
        called.IsDefined(typeof(InventoryLookupAttribute), false)
        || InterfaceMethodOf(called)?.IsDefined(typeof(InventoryLookupAttribute), false) == true;

    private static bool IsEntryPoint(MethodBase called)
    {
        var definition = called is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : called;
        return definition.IsDefined(typeof(MutationEntryPointAttribute), false);
    }

    /// <summary>The overload's action takes the resolved object (<c>Func&lt;TTarget, CancellationToken, …&gt;</c>).</summary>
    private static bool IsTyped(MethodBase called) =>
        called.GetParameters().Any(p => p.ParameterType is { IsGenericType: true } t
                                        && t.GetGenericArguments() is [var first, ..]
                                        && typeof(ResolvedTarget).IsAssignableFrom(first));

    public static IEnumerable<MethodBase> Bodies(Type type) =>
        type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));

    public static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static IEnumerable<(OpCode Op, object? Operand)> Instructions(MethodBase method) =>
        InstructionsWithOffsets(method).Select(i => (i.Op, i.Operand));

    private static IEnumerable<(OpCode Op, object? Operand, int Offset)> InstructionsWithOffsets(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { yield break; }
        if (il is null) yield break;
        var module = method.Module;
        var typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        var methodArgs = method is MethodInfo { IsGenericMethod: true } gm ? gm.GetGenericArguments() : null;
        var pos = 0;
        while (pos < il.Length)
        {
            var offset = pos;
            short value = il[pos++];
            if (value == 0xFE && pos < il.Length) value = (short)(0xFE00 | il[pos++]);
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break;   // unknown byte: stop, never guess
            object? operand = null;
            try
            {
                operand = op.OperandType switch
                {
                    OperandType.InlineString => module.ResolveString(BitConverter.ToInt32(il, pos)),
                    OperandType.InlineMethod => module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, methodArgs),
                    OperandType.InlineField => module.ResolveField(BitConverter.ToInt32(il, pos), typeArgs, methodArgs),
                    OperandType.ShortInlineVar => (int)il[pos],
                    OperandType.InlineVar => (int)BitConverter.ToUInt16(il, pos),
                    _ => null,
                };
            }
            catch { /* a token from a generic context we cannot close */ }
            yield return (op, operand, offset);
            pos += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
                _ => 4,
            };
        }
    }

    private static int PopCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1
            or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
            or StackBehaviour.Popref_popi_pop1 => 3,
        _ => 0,
    };

    private static int PushCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Varpush => 0,
        _ => 1,
    };

    /// <summary>Compiler-generated state machines, closures and lambdas belong to the type that declared them.</summary>
    public static Type OwnerOf(Type type)
    {
        while (type.DeclaringType is not null
               && (type.Name.Contains('<') || type.IsDefined(typeof(CompilerGeneratedAttribute), false)))
            type = type.DeclaringType;
        return type;
    }

    /// <summary>The outermost source method a body belongs to (as in <c>MutationCallScanner</c>).</summary>
    public static string HandlerName(Type type, MethodBase method)
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
}
