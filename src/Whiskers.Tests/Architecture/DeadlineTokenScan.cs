using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0023 (the token ratchet): finds, in compiled IL, every action handed to a gateway entry point
/// (<see cref="IMutationGateway.ExecuteAsync{T}"/> and every <see cref="MutationEntryPointAttribute"/> method, also
/// when called on a class implementing it) — the capability id loaded with <c>ldstr</c>, the action with <c>ldftn</c>
/// or <c>ldvirtftn</c> (lambdas, method groups, local functions), then the call — and decides whether the action passes
/// its deadline token on. It is also the scan <c>MutationBypassArchitectureTests</c> reads its runner lambdas from.
/// </summary>
internal static class DeadlineTokenScan
{
    /// <summary>One action handed to an entry point. <see cref="File"/> is relative to its project directory (the
    /// <c>.razor</c> or <c>.cs</c> file), <see cref="Method"/> the enclosing user method (never a compiler-generated
    /// name), <see cref="Capability"/> the id or <c>*</c> when it is not a literal at the call. <see cref="Action"/> is
    /// <c>null</c> when the call has no action loaded in the same body (a delegate from a field or parameter): the
    /// ratchet cannot judge it and fails on it, unless the body is itself an entry point that forwards its action.</summary>
    internal sealed record Site(string File, string Method, string Capability, MethodBase? Action, bool PassesToken,
        MethodBase Entry, MethodBase Body)
    {
        public string Key => $"{File}|{Method}|{Capability}";

        /// <summary>No action in the body, and the body does not forward its own action as an entry point.</summary>
        public bool Unresolved => Action is null && !IsEntryPoint(Body);
    }

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly CapabilityCatalog Catalog = new();

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>Every site in the given types (and the types nested in them), or in the whole assembly.</summary>
    public static IReadOnlyList<Site> Sites(Assembly assembly, IEnumerable<Type>? only = null)
    {
        using var pdb = PdbOf(assembly);
        var types = only is null ? SafeTypes(assembly) : only.SelectMany(WithNested);
        var sites = new List<Site>();
        foreach (var type in types)
            foreach (var body in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                sites.AddRange(SitesIn(body, pdb?.GetMetadataReader()));
        return sites;
    }

    /// <summary>The discarding sites counted per key — what the baseline holds.</summary>
    public static SortedDictionary<string, int> Discarding(IEnumerable<Site> sites)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var site in sites.Where(s => s.Action is not null && !s.PassesToken))
            counts[site.Key] = counts.GetValueOrDefault(site.Key) + 1;
        return counts;
    }

    private static IEnumerable<Type> WithNested(Type type) =>
        new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested));

    /// <summary>
    /// The sites of one body. Every action with a token parameter loaded since the previous entry call belongs to the
    /// next one — a conditional (<c>fast ? a : b</c>) hands either, so each is judged. The capability is the last
    /// catalog id loaded before the call and after the previous call that took a string (a helper that consumed it).
    /// </summary>
    private static IEnumerable<Site> SitesIn(MethodBase body, MetadataReader? pdb)
    {
        var instructions = Decode(body);
        if (instructions is null) yield break;
        var module = body.Module;
        var (typeArgs, methodArgs) = GenericContext(body);
        string? capability = null;
        var lastLiteralIsCapability = false;
        var actions = new List<MethodBase>();
        foreach (var (op, operand) in instructions)
        {
            if (op.OperandType == OperandType.InlineString)
            {
                string? text = null;
                try { text = module.ResolveString(operand); } catch { }
                lastLiteralIsCapability = text is not null && Catalog.Find(text) is not null;
                if (lastLiteralIsCapability) capability = text;
            }
            else if (op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
            {
                MethodBase? loaded = null;
                try { loaded = module.ResolveMethod(operand, typeArgs, methodArgs); } catch { }
                if (loaded is not null && Takes(loaded, typeof(CancellationToken)))
                    actions.Add(loaded);
            }
            else if (op.OperandType == OperandType.InlineMethod && (op == OpCodes.Call || op == OpCodes.Callvirt))
            {
                MethodBase? called = null;
                try { called = module.ResolveMethod(operand, typeArgs, methodArgs); } catch { }
                if (called is null) continue;
                if (!IsEntryPoint(called))
                {
                    // A helper that takes a capability id consumed the literal before it (RequireAsync("x", …),
                    // RefusedAsync(task, "x", …)); a call that only takes some other string (Path.GetFileName) does not.
                    if (lastLiteralIsCapability && TakesCapabilityId(called))
                        capability = null;
                    continue;
                }
                var file = FileOf(body, pdb);
                var method = EnclosingMethod(body);
                if (actions.Count == 0)
                    yield return new Site(file, method, capability ?? "*", null, false, called, body);
                foreach (var action in actions)
                    yield return new Site(file, method, capability ?? "*", action, PassesToken(action), called, body);
                capability = null;
                actions.Clear();
            }
        }
    }

    /// <summary>A gateway entry point: <c>IMutationGateway.ExecuteAsync</c> (or its implementation), a method marked
    /// <see cref="MutationEntryPointAttribute"/>, or a class method implementing a marked interface method.</summary>
    internal static bool IsEntryPoint(MethodBase called)
    {
        var definition = called is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : called;
        if (definition.IsDefined(typeof(MutationEntryPointAttribute), false)) return true;
        if (definition is not MethodInfo info || definition.DeclaringType is not { } declaring) return false;
        if (info.Name == nameof(IMutationGateway.ExecuteAsync) && typeof(IMutationGateway).IsAssignableFrom(declaring))
            return true;
        if (declaring.IsInterface) return false;
        var type = declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : declaring;
        foreach (var iface in type.GetInterfaces())
        {
            InterfaceMapping map;
            try { map = type.GetInterfaceMap(iface); }
            catch { continue; }
            for (var i = 0; i < map.TargetMethods.Length; i++)
                if (map.TargetMethods[i].MetadataToken == info.MetadataToken && map.TargetMethods[i].Module == info.Module
                    && map.InterfaceMethods[i].IsDefined(typeof(MutationEntryPointAttribute), false))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// The action passes its token on when the token parameter — or the state-machine field it was hoisted into, a
    /// local or hoisted local copied from it, or a closure field it was captured into and that the closure passes on —
    /// is loaded by value and reaches a call as an argument (only other argument loads in between). Loading its
    /// address (<c>ct.ThrowIfCancellationRequested()</c>, <c>ct.IsCancellationRequested</c>), discarding it
    /// (<c>_ = ct</c>), boxing it (a log line) or comparing it is not passing it on; <c>CancellationToken.None</c> and
    /// <c>default</c> are never the token.
    /// </summary>
    internal static bool PassesToken(MethodBase action)
    {
        var token = action.GetParameters().FirstOrDefault(p => p.ParameterType == typeof(CancellationToken));
        if (token is null) return false;
        if (action.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType is { } machine)
        {
            var fields = machine.GetFields(All)
                .Where(f => f.FieldType == typeof(CancellationToken) && f.Name == token.Name)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);
            var moveNext = machine.GetMethod("MoveNext", All);
            return fields.Count > 0 && moveNext is not null && Passes(moveNext, [], fields, 0);
        }
        return Passes(action, [token.Position + (action.IsStatic ? 0 : 1)], [], 0);
    }

    private static bool Passes(MethodBase body, HashSet<int> args, HashSet<string> fields, int depth)
    {
        var instructions = Decode(body);
        if (instructions is null || depth > 4) return false;
        var module = body.Module;
        var (typeArgs, methodArgs) = GenericContext(body);
        var locals = new HashSet<int>();
        bool grew;
        do
        {
            grew = false;
            for (var i = 0; i < instructions.Count; i++)
            {
                var (op, operand) = instructions[i];
                var isSource = ArgLoaded(op, operand) is { } a ? args.Contains(a)
                    : LocalLoaded(op, operand) is { } l ? locals.Contains(l)
                    : op == OpCodes.Ldfld && ResolveField(module, operand, typeArgs, methodArgs) is { } f && fields.Contains(f.Name);
                if (!isSource || i + 1 >= instructions.Count) continue;
                var (next, nextOperand) = instructions[i + 1];
                if (LocalStored(next, nextOperand) is { } stored)
                {
                    grew |= locals.Add(stored);
                    continue;
                }
                if (next == OpCodes.Stfld && ResolveField(module, nextOperand, typeArgs, methodArgs) is { } target
                    && target.FieldType == typeof(CancellationToken))
                {
                    if (target.DeclaringType == body.DeclaringType)
                        grew |= fields.Add(target.Name);   // a hoisted local of the same state machine
                    else if (target.DeclaringType is { } closure && ClosurePasses(closure, target.Name, depth))
                        return true;                       // captured, and the closure passes it on
                    continue;
                }
                if (ReachesCall(instructions, i + 1, module, typeArgs, methodArgs)) return true;
            }
        } while (grew);
        return false;
    }

    /// <summary>A method of the closure type passes the captured token field on.</summary>
    /// <remarks>An async lambda of the closure reads the field through the closure in its state machine's
    /// <c>MoveNext</c> (<c>ldfld &lt;&gt;4__this; ldfld ct</c>), so that is read too.</remarks>
    private static bool ClosurePasses(Type closure, string field, int depth) =>
        closure.GetMethods(All).Any(m =>
            Passes(m, [], [field], depth + 1)
            || (m.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType?.GetMethod("MoveNext", All) is { } moveNext
                && Passes(moveNext, [], [field], depth + 1)));

    /// <summary>From <paramref name="from"/> on, only further argument loads (and getters that produce one) come before
    /// a call or newobj that takes a <see cref="CancellationToken"/> — a box (a log line), a comparison
    /// (<c>ct == CancellationToken.None</c>), a branch or a pop in between is not passing the token on.</summary>
    private static bool ReachesCall(List<(OpCode Op, int Operand)> instructions, int from, Module module,
        Type[]? typeArgs, Type[]? methodArgs)
    {
        for (var j = from; j < instructions.Count && j < from + 24; j++)
        {
            var (op, operand) = instructions[j];
            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
            {
                MethodBase? called = null;
                try { called = module.ResolveMethod(operand, typeArgs, methodArgs); } catch { }
                if (called is null) return true;   // unresolvable in this context: counted as the accepted limit
                if (called.Name is "op_Equality" or "op_Inequality" or nameof(Equals)) return false;
                if (Takes(called, typeof(CancellationToken))) return true;
                // A call producing a further argument (c.Id, a callback delegate, CancellationToken.None for a
                // comparison) — read on to the call that takes the token.
                continue;
            }
            if (!IsArgumentLoad(op)) return false;
        }
        return false;
    }

    /// <summary>The method has a string parameter named for a capability (<c>capabilityId</c>, <c>capability</c>).</summary>
    private static bool TakesCapabilityId(MethodBase method)
    {
        try
        {
            return method.GetParameters().Any(p => p.ParameterType == typeof(string)
                                                   && p.Name?.Contains("capability", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch { return false; }
    }

    /// <summary>The method has a parameter of <paramref name="type"/>; false when its signature cannot be read.</summary>
    private static bool Takes(MethodBase method, Type type)
    {
        try { return method.GetParameters().Any(p => p.ParameterType == type); }
        catch { return false; }
    }

    /// <summary>Instructions that only push a further argument (a load, a constant, a field, a conversion).</summary>
    private static bool IsArgumentLoad(OpCode op) =>
        op.StackBehaviourPop == StackBehaviour.Pop0 && op.StackBehaviourPush is StackBehaviour.Push1 or StackBehaviour.Pushi
            or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref
        || op == OpCodes.Ldfld || op == OpCodes.Ldflda || op == OpCodes.Dup
        || op.Name.StartsWith("conv.", StringComparison.Ordinal);

    private static int? ArgLoaded(OpCode op, int operand) =>
        op == OpCodes.Ldarg_0 ? 0 : op == OpCodes.Ldarg_1 ? 1 : op == OpCodes.Ldarg_2 ? 2 : op == OpCodes.Ldarg_3 ? 3
        : op == OpCodes.Ldarg_S || op == OpCodes.Ldarg ? operand : null;

    private static int? LocalLoaded(OpCode op, int operand) =>
        op == OpCodes.Ldloc_0 ? 0 : op == OpCodes.Ldloc_1 ? 1 : op == OpCodes.Ldloc_2 ? 2 : op == OpCodes.Ldloc_3 ? 3
        : op == OpCodes.Ldloc_S || op == OpCodes.Ldloc ? operand : null;

    private static int? LocalStored(OpCode op, int operand) =>
        op == OpCodes.Stloc_0 ? 0 : op == OpCodes.Stloc_1 ? 1 : op == OpCodes.Stloc_2 ? 2 : op == OpCodes.Stloc_3 ? 3
        : op == OpCodes.Stloc_S || op == OpCodes.Stloc ? operand : null;

    private static FieldInfo? ResolveField(Module module, int token, Type[]? typeArgs, Type[]? methodArgs)
    {
        try { return module.ResolveField(token, typeArgs, methodArgs); }
        catch { return null; }
    }

    private static (Type[]? TypeArgs, Type[]? MethodArgs) GenericContext(MethodBase method) =>
        (method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null,
            method is MethodInfo { IsGenericMethod: true } gm ? gm.GetGenericArguments() : null);

    /// <summary>The IL of <paramref name="method"/> as (opcode, operand) pairs; <c>null</c> when it has none or holds a
    /// byte that is not an opcode (never guessed).</summary>
    internal static List<(OpCode Op, int Operand)>? Decode(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { return null; }
        if (il is null) return null;
        var list = new List<(OpCode, int)>();
        var pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE && pos < il.Length) value = (short)(0xFE00 | il[pos++]);
            if (!OpCodesByValue.TryGetValue(value, out var op)) return null;
            var operand = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => il[pos],
                OperandType.InlineVar => BitConverter.ToUInt16(il, pos),
                OperandType.InlineI8 or OperandType.InlineR => 0,
                _ => BitConverter.ToInt32(il, pos),
            };
            list.Add((op, operand));
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
        return list;
    }

    /// <summary>The user method a body belongs to: <c>&lt;X&gt;</c> of a lambda (<c>&lt;X&gt;b__4_0</c>), a local
    /// function (<c>&lt;X&gt;g__Local|4_0</c>) or a state machine (<c>&lt;X&gt;d__7.MoveNext</c>), else its own name.
    /// Compiler-generated ordinals are never part of it.</summary>
    internal static string EnclosingMethod(MethodBase body)
    {
        var name = body.Name == "MoveNext" && body.DeclaringType is { Name: var machine } && machine.StartsWith('<')
            ? machine
            : body.Name;
        if (!name.StartsWith('<')) return name;
        var trimmed = name.TrimStart('<');
        var end = trimmed.IndexOf('>');
        return end > 0 ? trimmed[..end] : name;
    }

    private static MetadataReaderProvider? PdbOf(Assembly assembly)
    {
        var path = Path.ChangeExtension(assembly.Location, ".pdb");
        if (!File.Exists(path)) return null;
        return MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(path));
    }

    /// <summary>The source file of <paramref name="body"/> from its sequence points (a <c>.razor</c> file for markup),
    /// relative to its project directory; the owner type's name, marked with <c>?</c>, when the PDB does not say.</summary>
    private static string FileOf(MethodBase body, MetadataReader? pdb)
    {
        if (pdb is not null)
        {
            try
            {
                var handle = MetadataTokens.MethodDefinitionHandle(body.MetadataToken & 0x00FFFFFF);
                foreach (var point in pdb.GetMethodDebugInformation(handle).GetSequencePoints())
                {
                    if (point.IsHidden || point.Document.IsNil) continue;
                    return Relative(pdb.GetString(pdb.GetDocument(point.Document).Name));
                }
            }
            catch
            {
                // Falls back to the type below.
            }
        }
        var owner = body.DeclaringType;
        while (owner?.DeclaringType is not null && owner.Name.Contains('<')) owner = owner.DeclaringType;
        return "?" + (owner?.FullName ?? "unknown");
    }

    /// <summary><c>…/src/Whiskers/Components/Pages/X.razor</c> → <c>Components/Pages/X.razor</c>.</summary>
    private static string Relative(string path)
    {
        var normalized = path.Replace('\\', '/');
        var src = normalized.LastIndexOf("/src/", StringComparison.Ordinal);
        if (src < 0) return normalized;
        var rest = normalized[(src + "/src/".Length)..];
        var slash = rest.IndexOf('/');
        return slash >= 0 ? rest[(slash + 1)..] : rest;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
