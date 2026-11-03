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

    public string CapabilityId { get; }

    /// <summary>The capability of the innermost open scope, or <c>null</c> when none was open.</summary>
    public string? OpenScope { get; }
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
}

public sealed class MutationScopeMonitor : IMutationScopeMonitor
{
    private readonly ConcurrentDictionary<string, MutationBypassReport> _reports = new(StringComparer.Ordinal);
    private readonly ILogger<MutationScopeMonitor> _logger;
    private readonly TimeProvider _time;

    /// <summary>The production monitor: it enforces. There is no setting that turns it off.</summary>
    public MutationScopeMonitor(ILogger<MutationScopeMonitor> logger, TimeProvider? time = null)
        : this(logger, time, enforce: true)
    {
    }

    private MutationScopeMonitor(ILogger<MutationScopeMonitor> logger, TimeProvider? time, bool enforce)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        Enforce = enforce;
    }

    /// <summary>A monitor that only counts (the behaviour before R1-48). For tests of the counting itself.</summary>
    internal static MutationScopeMonitor ReportOnly(ILogger<MutationScopeMonitor> logger, TimeProvider? time = null) =>
        new(logger, time, enforce: false);

    public bool Enforce { get; }

    /// <summary>
    /// The capabilities whose undecided calls are still only recorded, not refused (R1-48). One is left:
    /// <c>config.whitelist</c>, whose only caller outside a decision is the first-run wizard
    /// (<c>SetupStateService.CompleteSetupAsync</c>), which writes the first admin to the sign-in whitelist before any
    /// person exists the gateway could authorize. That path belongs to the protected sign-in code and waits for the
    /// maintainer's decision on its authority (setup token, host-level); see <c>docs/v1/SECURITY_INVARIANTS.md</c>
    /// INV-38. <c>MutationBypassArchitectureTests</c> keeps this set equal to what the scanner still finds, so it can
    /// only shrink.
    /// </summary>
    public static IReadOnlySet<string> PendingCapabilities { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "config.whitelist" };

    public bool Enforces(string capabilityId) => Enforce && !PendingCapabilities.Contains(capabilityId);

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
/// attached scope. Every other call is passed straight through. The wrapper does not change arguments, results or
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
            Check(capability, targetMethod, MutationScope.Authorizes(capability));
        else if (_primitives.Contains(marked))
            Check($"{PrimitiveLabel}:{targetMethod.DeclaringType?.Name}.{targetMethod.Name}", targetMethod, MutationScope.Current is not null);

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
