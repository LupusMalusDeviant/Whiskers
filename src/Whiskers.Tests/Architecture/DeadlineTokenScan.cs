using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0023 (the token ratchet): finds, in compiled IL, every action handed to a gateway entry point
/// (<see cref="IMutationGateway.ExecuteAsync{T}"/> and every <see cref="MutationEntryPointAttribute"/> method) — a
/// capability id loaded with <c>ldstr</c>, then the action with <c>ldftn</c> or <c>ldvirtftn</c> (lambdas, method
/// groups, local functions), then the call — and decides whether the action passes its deadline token on.
/// </summary>
internal static class DeadlineTokenScan
{
    /// <summary>One action handed to an entry point. <see cref="File"/> is relative to its project directory (the
    /// <c>.razor</c> or <c>.cs</c> file), <see cref="Method"/> the enclosing user method (never a compiler-generated
    /// name), <see cref="Capability"/> the id or <c>*</c> when it is not a literal at the call.</summary>
    internal sealed record Site(string File, string Method, string Capability, MethodBase Action, bool PassesToken)
    {
        public string Key => $"{File}|{Method}|{Capability}";
    }

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly CapabilityCatalog Catalog = new();

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>Every site in the given types (and the types nested in them).</summary>
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
        foreach (var site in sites.Where(s => !s.PassesToken))
            counts[site.Key] = counts.GetValueOrDefault(site.Key) + 1;
        return counts;
    }

    private static IEnumerable<Type> WithNested(Type type) =>
        new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested));

    private static IEnumerable<Site> SitesIn(MethodBase body, MetadataReader? pdb)
    {
        var instructions = Decode(body);
        if (instructions is null) yield break;
        var module = body.Module;
        var (typeArgs, methodArgs) = GenericContext(body);
        string? capability = null;
        MethodBase? action = null;
        foreach (var (op, operand) in instructions)
        {
            if (op.OperandType == OperandType.InlineString)
            {
                string? text = null;
                try { text = module.ResolveString(operand); } catch { }
                if (text is not null && Catalog.Find(text) is not null) capability = text;
            }
            else if (op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
            {
                try { action = module.ResolveMethod(operand, typeArgs, methodArgs); }
                catch { action = null; }
            }
            else if (op.OperandType == OperandType.InlineMethod && op != OpCodes.Newobj)
            {
                MethodBase? called = null;
                try { called = module.ResolveMethod(operand, typeArgs, methodArgs); } catch { }
                if (called is null || !IsEntryPoint(called)) continue;
                if (action is not null)
                    yield return new Site(FileOf(body, pdb), EnclosingMethod(body), capability ?? "*", action,
                        PassesToken(action));
                capability = null;
                action = null;
            }
        }
    }

    /// <summary>A gateway entry point: <c>IMutationGateway.ExecuteAsync</c> (or its implementation), or a method marked
    /// <see cref="MutationEntryPointAttribute"/>.</summary>
    internal static bool IsEntryPoint(MethodBase called)
    {
        var definition = called is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : called;
        if (definition.IsDefined(typeof(MutationEntryPointAttribute), false)) return true;
        return definition.Name == nameof(IMutationGateway.ExecuteAsync)
               && definition.DeclaringType is { } declaring
               && typeof(IMutationGateway).IsAssignableFrom(declaring);
    }

    /// <summary>
    /// The action passes its token on when the token parameter — or the state-machine field it was hoisted into, or a
    /// local or hoisted local copied from it — is loaded by value and handed to something other than <c>pop</c>.
    /// Loading its address (<c>ct.ThrowIfCancellationRequested()</c>, <c>ct.IsCancellationRequested</c>) is not
    /// passing it on; <c>CancellationToken.None</c> and <c>default</c> are never the token.
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
            return fields.Count > 0 && moveNext is not null && Passes(moveNext, [], fields);
        }
        return Passes(action, [token.Position + (action.IsStatic ? 0 : 1)], []);
    }

    private static bool Passes(MethodBase body, HashSet<int> args, HashSet<string> fields)
    {
        var instructions = Decode(body);
        if (instructions is null) return false;
        var module = body.Module;
        var (typeArgs, methodArgs) = GenericContext(body);
        var locals = new HashSet<int>();
        var passed = false;
        bool grew;
        do
        {
            grew = false;
            for (var i = 0; i < instructions.Count; i++)
            {
                var (op, operand) = instructions[i];
                var isSource = ArgLoaded(op, operand) is { } a ? args.Contains(a)
                    : LocalLoaded(op, operand) is { } l ? locals.Contains(l)
                    : op == OpCodes.Ldfld && FieldName(module, operand, typeArgs, methodArgs) is { } f && fields.Contains(f);
                if (!isSource) continue;
                if (i + 1 >= instructions.Count) { passed = true; continue; }
                var (next, nextOperand) = instructions[i + 1];
                if (next == OpCodes.Pop) continue;
                if (LocalStored(next, nextOperand) is { } stored)
                {
                    grew |= locals.Add(stored);
                    continue;
                }
                if (next == OpCodes.Stfld && FieldName(module, nextOperand, typeArgs, methodArgs) is { } copy
                    && body.DeclaringType is { } owner && owner.GetField(copy, All) is { FieldType: var t }
                    && t == typeof(CancellationToken))
                {
                    // A hoisted local of the same state machine copied from the token.
                    grew |= fields.Add(copy);
                    continue;
                }
                passed = true;
            }
        } while (grew && !passed);
        return passed;
    }

    private static int? ArgLoaded(OpCode op, int operand) =>
        op == OpCodes.Ldarg_0 ? 0 : op == OpCodes.Ldarg_1 ? 1 : op == OpCodes.Ldarg_2 ? 2 : op == OpCodes.Ldarg_3 ? 3
        : op == OpCodes.Ldarg_S || op == OpCodes.Ldarg ? operand : null;

    private static int? LocalLoaded(OpCode op, int operand) =>
        op == OpCodes.Ldloc_0 ? 0 : op == OpCodes.Ldloc_1 ? 1 : op == OpCodes.Ldloc_2 ? 2 : op == OpCodes.Ldloc_3 ? 3
        : op == OpCodes.Ldloc_S || op == OpCodes.Ldloc ? operand : null;

    private static int? LocalStored(OpCode op, int operand) =>
        op == OpCodes.Stloc_0 ? 0 : op == OpCodes.Stloc_1 ? 1 : op == OpCodes.Stloc_2 ? 2 : op == OpCodes.Stloc_3 ? 3
        : op == OpCodes.Stloc_S || op == OpCodes.Stloc ? operand : null;

    private static string? FieldName(Module module, int token, Type[]? typeArgs, Type[]? methodArgs)
    {
        try { return module.ResolveField(token, typeArgs, methodArgs)?.Name; }
        catch { return null; }
    }

    private static (Type[]? TypeArgs, Type[]? MethodArgs) GenericContext(MethodBase method) =>
        (method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null,
            method is MethodInfo { IsGenericMethod: true } gm ? gm.GetGenericArguments() : null);

    /// <summary>The IL of <paramref name="method"/> as (opcode, operand) pairs; <c>null</c> when it has none or holds a
    /// byte that is not an opcode (never guessed).</summary>
    private static List<(OpCode Op, int Operand)>? Decode(MethodBase method)
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
    /// relative to its project directory; the owner type's name when the PDB does not say.</summary>
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
