using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Whiskers.Services.Mutations;

/// <summary>One capability that ran outside a gateway scope, and how often.</summary>
public sealed record MutationBypassReport(string CapabilityId, string Method, long Count, DateTimeOffset LastSeen);

/// <summary>A marked call that no open gateway scope decided (INV-38, R1-48). It never ran.</summary>
public sealed class MutationScopeViolationException : InvalidOperationException
{
    public MutationScopeViolationException(string capabilityId, string method, string? openScope)
        : base(openScope is null
            ? $"'{capabilityId}' must run through the Mutation Gateway ({method}): no gateway scope is open."
            : $"'{capabilityId}' must run through the Mutation Gateway ({method}): the open scope decided '{openScope}', " +
              "which does not compose it.")
    {
        CapabilityId = capabilityId;
        OpenScope = openScope;
    }

    /// <summary>A call of a decided capability whose <see cref="MutationTargetAttribute"/> argument names something the
    /// open decision did not cover, or names it on another server.</summary>
    public MutationScopeViolationException(string capabilityId, string method, string? openScope, string? target)
        : base($"'{capabilityId}' must act on what the Mutation Gateway decided ({method}): '{target}' is not the target " +
               $"the open decision '{openScope}' was taken for, or not on its server.")
    {
        CapabilityId = capabilityId;
        OpenScope = openScope;
        Target = target;
    }

    public string CapabilityId { get; }

    /// <summary>The capability of the innermost open scope, or <c>null</c> when none was open.</summary>
    public string? OpenScope { get; }

    /// <summary>For a refused target binding: the target the call named; otherwise <c>null</c>.</summary>
    public string? Target { get; }
}

/// <summary>
/// Records mutating calls that ran without a <see cref="MutationScope"/> that decided them. Since R1-48 (WP2 step 2.6)
/// the guard enforces: such a call throws <see cref="MutationScopeViolationException"/> and is still recorded here, so
/// the self-status page and the log show it (INV-38). Report mode, in which the call proceeded, exists for tests only.
/// </summary>
public interface IMutationScopeMonitor
{
    bool Enforce { get; }

    /// <summary>Whether a call of <paramref name="capabilityId"/> that no open scope decided is refused (true) or only
    /// recorded (false).</summary>
    bool Enforces(string capabilityId) => Enforce;

    void ReportOutsideScope(string capabilityId, MethodInfo method);

    IReadOnlyList<MutationBypassReport> Snapshot();

    /// <summary>
    /// The canonical id (the registry's) of the server a call acts on (R1-48, review rounds 2 and 3): the named server,
    /// or — when the call's <c>serverId</c> argument was omitted — the server the operation's
    /// <see cref="OmittedServerAttribute"/> declares (the configured default, or a fixed id). Null when it cannot be
    /// resolved: an unknown server, an omitted argument without a declaration, no registry, or a failing lookup. A
    /// composed call then matches no decision taken for a server (fail closed). Without a registry nothing resolves.
    /// </summary>
    string? ResolveServer(string? serverId, OmittedServerAttribute? omitted = null) => null;
}

public sealed class MutationScopeMonitor : IMutationScopeMonitor
{
    private readonly ConcurrentDictionary<string, MutationBypassReport> _reports = new(StringComparer.Ordinal);
    private readonly ILogger<MutationScopeMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly IServiceProvider? _services;

    /// <summary>The production monitor: it enforces. There is no setting that turns it off. <paramref name="services"/>
    /// gives it the server registry, resolved on use (the registry is itself guarded, so it cannot be a dependency).</summary>
    public MutationScopeMonitor(ILogger<MutationScopeMonitor> logger, TimeProvider? time = null, IServiceProvider? services = null)
        : this(logger, time, enforce: true, services)
    {
    }

    private MutationScopeMonitor(ILogger<MutationScopeMonitor> logger, TimeProvider? time, bool enforce,
        IServiceProvider? services = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        Enforce = enforce;
        _services = services;
    }

    /// <inheritdoc />
    public string? ResolveServer(string? serverId, OmittedServerAttribute? omitted = null)
    {
        try
        {
            if (_services?.GetService(typeof(Whiskers.Services.ServerConfig.IServerConfigService))
                is not Whiskers.Services.ServerConfig.IServerConfigService servers)
                return null;
            Whiskers.Models.ServerConfig? server;
            if (serverId is not null)                                       // an empty name is no server (round 4)
                server = serverId.Length == 0 ? null : servers.GetServer(serverId);
            else if (omitted is null) return null;                          // no declared routing: unknown
            else server = omitted.ServerId is { } fixedId ? servers.GetServer(fixedId) : servers.GetDefaultServer();
            return server?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The server of a guarded call could not be resolved; it matches no decided server");
            return null;
        }
    }

    /// <summary>A monitor that only counts (the behaviour before R1-48). For tests of the counting itself.</summary>
    internal static MutationScopeMonitor ReportOnly(ILogger<MutationScopeMonitor> logger, TimeProvider? time = null) =>
        new(logger, time, enforce: false);

    public bool Enforce { get; }

    /// <summary>R1-48: every capability is refused outside a decision; no capability is only recorded any more (the
    /// first-run wizard's whitelist and role writes became narrow first-admin methods, see <c>IRoleService.SetFirstAdminAsync</c>).</summary>
    public bool Enforces(string capabilityId) => Enforce;

    public void ReportOutsideScope(string capabilityId, MethodInfo method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(method);
        var name = $"{method.DeclaringType?.Name}.{method.Name}";
        var now = _time.GetUtcNow();
        var report = _reports.AddOrUpdate(capabilityId,
            _ => new MutationBypassReport(capabilityId, name, 1, now),
            (_, r) => r with { Method = name, Count = r.Count + 1, LastSeen = now });

        if (Enforces(capabilityId))
            _logger.LogError("Mutation {Capability} ({Method}) was refused: it ran outside a Mutation Gateway scope " +
                             "that decided it (INV-38)", capabilityId, name);
        else if (report.Count == 1)
            _logger.LogInformation("Mutation {Capability} ({Method}) ran outside the Mutation Gateway (report mode)",
                capabilityId, name);
    }

    public IReadOnlyList<MutationBypassReport> Snapshot() =>
        _reports.Values.OrderBy(r => r.CapabilityId, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Wraps a service interface that has <see cref="MutatingOperationAttribute"/> or <see cref="MutationPrimitiveAttribute"/>
/// methods. A marked operation runs only when an open, attached <see cref="MutationScope"/> of this flow decided that
/// capability or one that composes it (<see cref="MutationScope.Authorizes"/>); a primitive runs only inside an open,
/// attached scope of a <see cref="MutationCompositions.RawHostCommands"/> capability. Every other call is passed straight through. The wrapper does not change arguments (except one: an omitted
/// <c>serverId</c> of a marked call becomes the canonical id the check resolved, review round 4), results or
/// exceptions of a call it lets through.
/// </summary>
public class MutationScopeProxy<TService> : DispatchProxy, IMutationScopeProxy where TService : class
{
    /// <summary>The label a primitive is reported under (it has no capability of its own).</summary>
    public const string PrimitiveLabel = "primitive";

    private TService _inner = null!;
    private IMutationScopeMonitor _monitor = null!;
    private IReadOnlyDictionary<MethodInfo, string> _capabilities = null!;
    private IReadOnlySet<MethodInfo> _primitives = null!;

    /// <inheritdoc />
    public object Inner => _inner;

    /// <summary>Returns <paramref name="inner"/> itself when <typeparamref name="TService"/> has no marked method.</summary>
    public static TService Wrap(TService inner, IMutationScopeMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(monitor);
        var capabilities = MutatingMethods.Of(typeof(TService));
        var primitives = MutatingMethods.PrimitivesOf(typeof(TService));
        if (capabilities.Count == 0 && primitives.Count == 0) return inner;

        var proxy = Create<TService, MutationScopeProxy<TService>>();
        var self = (MutationScopeProxy<TService>)(object)proxy;
        self._inner = inner;
        self._monitor = monitor;
        self._capabilities = capabilities;
        self._primitives = primitives;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        // A generic method arrives constructed (SaveSectionAsync<HealthSettings>); its attribute sits on the definition.
        var marked = targetMethod.IsGenericMethod ? targetMethod.GetGenericMethodDefinition() : targetMethod;
        if (_capabilities.TryGetValue(marked, out var capability))
        {
            // An omitted server argument is not "no server": the transport falls back to its default server, which is
            // resolved and compared with the decision like a named one (R1-48, review round 2).
            var index = ServerArgumentIndex(marked);
            var actsOnServer = index >= 0;
            var named = actsOnServer && args is not null && index < args.Length ? args[index] as string : null;
            // Review round 5: a server argument is a name or omitted (null) — never empty. An empty or blank one names no
            // server, whatever decision is open (the capability itself, or one without a server): refused before the
            // decision is even looked at, so it never reaches an implementation.
            if (named is not null && string.IsNullOrWhiteSpace(named))
                throw new ArgumentException(
                    $"{targetMethod.DeclaringType?.Name}.{targetMethod.Name}: serverId must name a server or be omitted; it was empty.",
                    "serverId");
            // Review round 3: the omitted server (null — an empty string is a name that resolves to nothing, round 4) is
            // resolved by the operation's own declaration (OmittedServerAttribute), the one its implementation routes by;
            // anything unresolvable matches no decided server.
            var server = actsOnServer ? _monitor.ResolveServer(named, named is null ? OmittedServer.Of(marked) : null) : null;
            Check(capability, targetMethod, MutationScope.Authorizes(capability, actsOnServer, server,
                id => _monitor.ResolveServer(id)));
            // The call is bound to the decision's target, not only its capability: whatever the execution delegate
            // computed or captured, it acts only on an identity the decision covered, on the decided server.
            if (TargetArgumentIndex(marked) is var t and >= 0)
            {
                var identity = args is not null && t < args.Length ? args[t] as string : null;
                if (!MutationScope.AuthorizesTarget(capability, identity, actsOnServer, named, server,
                        id => _monitor.ResolveServer(id)))
                {
                    _monitor.ReportOutsideScope(capability, targetMethod);
                    if (_monitor.Enforces(capability))
                        throw new MutationScopeViolationException(capability,
                            $"{targetMethod.DeclaringType?.Name}.{targetMethod.Name}", MutationScope.Current?.CapabilityId,
                            identity);
                }
            }
            // Review round 4: execution is bound to the identity that was checked. An omitted server is handed to the
            // implementation as the canonical id resolved above, so a default changed between this check and the
            // transport's own lookup cannot redirect the call to another server.
            if (named is null && server is not null && args is not null && index < args.Length)
                args[index] = server;
        }
        else if (_primitives.Contains(marked))
            // A raw host command runs only inside a decision whose execution is one (MutationCompositions.RawHostCommands),
            // never inside just any open scope (R1-48, review M1).
            Check($"{PrimitiveLabel}:{targetMethod.DeclaringType?.Name}.{targetMethod.Name}", targetMethod,
                MutationScope.AuthorizesRawHostCommand());

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;   // unreachable
        }
    }

    /// <summary>The position of the operation's <c>string serverId</c> parameter, or -1 when it has none (a serverless
    /// operation, R1-48, review m1 and round 2).</summary>
    private static int ServerArgumentIndex(MethodInfo method)
    {
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
            if (parameters[i].ParameterType == typeof(string)
                && string.Equals(parameters[i].Name, "serverId", StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>The position of the operation's <see cref="MutationTargetAttribute"/> parameter, or -1 when it has none.</summary>
    private static int TargetArgumentIndex(MethodInfo method) => MutatingMethods.TargetIndexOf(method);

    /// <summary>Refuses (enforced) or counts (report mode, tests) a call the open scope did not decide.</summary>
    private void Check(string capability, MethodInfo method, bool decided)
    {
        if (decided) return;
        _monitor.ReportOutsideScope(capability, method);
        if (_monitor.Enforces(capability))
            throw new MutationScopeViolationException(capability, $"{method.DeclaringType?.Name}.{method.Name}",
                MutationScope.Current?.CapabilityId);
    }
}

/// <summary>A scope guard around a service. The guard only implements the guarded interface; code that needs another
/// interface of the same instance (e.g. <c>IInitializable</c> at startup) unwraps it here.</summary>
public interface IMutationScopeProxy
{
    /// <summary>The service the guard forwards to.</summary>
    object Inner { get; }
}

/// <summary>Reads <see cref="MutatingOperationAttribute"/> and <see cref="MutationPrimitiveAttribute"/> from an interface
/// and the interfaces it extends.</summary>
public static class MutatingMethods
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<MethodInfo, string>> Cache = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlySet<MethodInfo>> PrimitiveCache = new();

    private static readonly ConcurrentDictionary<MethodInfo, int> TargetCache = new();

    /// <summary>The position of <paramref name="method"/>'s <see cref="MutationTargetAttribute"/> parameter, or -1. More
    /// than one is a declaration error.</summary>
    public static int TargetIndexOf(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return TargetCache.GetOrAdd(method, static m =>
        {
            var marked = m.GetParameters().Where(p => p.IsDefined(typeof(MutationTargetAttribute), false))
                .Select(p => p.Position).ToList();
            if (marked.Count > 1)
                throw new InvalidOperationException($"{m.DeclaringType?.Name}.{m.Name} marks more than one [MutationTarget] parameter.");
            if (marked.Count == 1 && m.GetParameters()[marked[0]].ParameterType != typeof(string))
                throw new InvalidOperationException($"{m.DeclaringType?.Name}.{m.Name}: a [MutationTarget] parameter must be a string.");
            return marked.Count == 1 ? marked[0] : -1;
        });
    }

    /// <summary>The <see cref="MutationPrimitiveAttribute"/> methods of <paramref name="serviceType"/>.</summary>
    public static IReadOnlySet<MethodInfo> PrimitivesOf(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return PrimitiveCache.GetOrAdd(serviceType, static t =>
            !t.IsInterface
                ? new HashSet<MethodInfo>()
                : t.GetInterfaces().Append(t)
                    .SelectMany(i => i.GetMethods())
                    .Where(m => m.IsDefined(typeof(MutationPrimitiveAttribute), false))
                    .ToHashSet());
    }

    public static IReadOnlyDictionary<MethodInfo, string> Of(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return Cache.GetOrAdd(serviceType, static t =>
        {
            if (!t.IsInterface) return new Dictionary<MethodInfo, string>();
            return t.GetInterfaces().Append(t)
                .SelectMany(i => i.GetMethods())
                .Select(m => (Method: m, Attr: m.GetCustomAttribute<MutatingOperationAttribute>()))
                .Where(x => x.Attr is not null)
                .ToDictionary(x => x.Method, x => x.Attr!.CapabilityId);
        });
    }
}
