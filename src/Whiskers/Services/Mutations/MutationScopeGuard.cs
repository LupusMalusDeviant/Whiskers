using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Whiskers.Services.Mutations;

/// <summary>One capability that ran outside a gateway scope, and how often (report mode, ADR-0005 step 2.0).</summary>
public sealed record MutationBypassReport(string CapabilityId, string Method, long Count, DateTimeOffset LastSeen);

/// <summary>
/// Collects mutating calls that ran without a <see cref="MutationScope"/>. In report mode (WP2 steps 2.0–2.5) the
/// call proceeds and is counted; step 2.6 switches to enforce, after which such a call throws (INV-38).
/// </summary>
public interface IMutationScopeMonitor
{
    bool Enforce { get; }

    void ReportOutsideScope(string capabilityId, MethodInfo method);

    IReadOnlyList<MutationBypassReport> Snapshot();
}

public sealed class MutationScopeMonitor : IMutationScopeMonitor
{
    private readonly ConcurrentDictionary<string, MutationBypassReport> _reports = new(StringComparer.Ordinal);
    private readonly ILogger<MutationScopeMonitor> _logger;
    private readonly TimeProvider _time;

    public MutationScopeMonitor(ILogger<MutationScopeMonitor> logger, TimeProvider? time = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    public bool Enforce => false;

    public void ReportOutsideScope(string capabilityId, MethodInfo method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(method);
        var name = $"{method.DeclaringType?.Name}.{method.Name}";
        var now = _time.GetUtcNow();
        var report = _reports.AddOrUpdate(capabilityId,
            _ => new MutationBypassReport(capabilityId, name, 1, now),
            (_, r) => r with { Method = name, Count = r.Count + 1, LastSeen = now });

        // Every path bypasses the gateway until it is migrated, so log the first sighting only.
        if (report.Count == 1)
            _logger.LogInformation("Mutation {Capability} ({Method}) ran outside the Mutation Gateway (report mode)",
                capabilityId, name);
    }

    public IReadOnlyList<MutationBypassReport> Snapshot() =>
        _reports.Values.OrderBy(r => r.CapabilityId, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Wraps a service interface that has <see cref="MutatingOperationAttribute"/> methods. Calls to those methods are
/// checked against <see cref="MutationScope.Current"/>; every other call is passed straight through. The wrapper
/// does not change arguments, results or exceptions.
/// </summary>
public class MutationScopeProxy<TService> : DispatchProxy, IMutationScopeProxy where TService : class
{
    private TService _inner = null!;
    private IMutationScopeMonitor _monitor = null!;
    private IReadOnlyDictionary<MethodInfo, string> _capabilities = null!;

    /// <inheritdoc />
    public object Inner => _inner;

    /// <summary>Returns <paramref name="inner"/> itself when <typeparamref name="TService"/> has no marked method.</summary>
    public static TService Wrap(TService inner, IMutationScopeMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(monitor);
        var capabilities = MutatingMethods.Of(typeof(TService));
        if (capabilities.Count == 0) return inner;

        var proxy = Create<TService, MutationScopeProxy<TService>>();
        var self = (MutationScopeProxy<TService>)(object)proxy;
        self._inner = inner;
        self._monitor = monitor;
        self._capabilities = capabilities;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        // A generic method arrives constructed (SaveSectionAsync<HealthSettings>); its attribute sits on the definition.
        var marked = targetMethod.IsGenericMethod ? targetMethod.GetGenericMethodDefinition() : targetMethod;
        if (_capabilities.TryGetValue(marked, out var capability) && MutationScope.Current is null)
        {
            if (_monitor.Enforce)
                throw new InvalidOperationException(
                    $"'{capability}' must run through the Mutation Gateway ({targetMethod.DeclaringType?.Name}.{targetMethod.Name}).");
            _monitor.ReportOutsideScope(capability, targetMethod);
        }

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
}

/// <summary>A scope guard around a service. The guard only implements the guarded interface; code that needs another
/// interface of the same instance (e.g. <c>IInitializable</c> at startup) unwraps it here.</summary>
public interface IMutationScopeProxy
{
    /// <summary>The service the guard forwards to.</summary>
    object Inner { get; }
}

/// <summary>Reads <see cref="MutatingOperationAttribute"/> from an interface and the interfaces it extends.</summary>
public static class MutatingMethods
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<MethodInfo, string>> Cache = new();

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
