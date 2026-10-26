using System.Reflection;
using Whiskers.Services.Mutations;

namespace Whiskers.Startup;

/// <summary>
/// Registers the Mutation Gateway (ADR-0005) and wraps every service interface that has
/// <see cref="MutatingOperationAttribute"/> methods in the scope guard. Must run after every other registration —
/// core and modules — so that no mutating service escapes the wrapper.
/// </summary>
public static class WhiskersMutationGatewayExtensions
{
    public static void AddWhiskersMutationGateway(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddMutationGateway();
        builder.Services.AddMutationScopeGuard();
    }

    public static IServiceCollection AddMutationGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ICapabilityCatalog, CapabilityCatalog>();
        services.AddSingleton<IMutationPolicy, GuardrailMutationPolicy>();
        services.AddSingleton<IMutationAuditSink, AuditLogMutationSink>();
        services.AddSingleton<IMutationScopeMonitor, MutationScopeMonitor>();
        services.AddSingleton<IMutationGateway, MutationGateway>();
        return services;
    }

    /// <summary>
    /// Replaces each registration of a mutating interface with one that returns the guard around the original
    /// instance. The original instance is held by a keyed <see cref="GuardedInstance{TService}"/> registration with
    /// the same lifetime, so the container still disposes what it created. A concrete type that is also registered
    /// on its own (<c>AddSingletonWithInterface</c>) is resolved through that registration — no second singleton.
    /// </summary>
    public static IServiceCollection AddMutationScopeGuard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registeredTypes = services.Where(d => !d.IsKeyedService).Select(d => d.ServiceType).ToHashSet();
        var guard = typeof(WhiskersMutationGatewayExtensions).GetMethod(nameof(Guard), BindingFlags.NonPublic | BindingFlags.Static)!;

        var count = services.Count;
        for (var i = 0; i < count; i++)
        {
            var descriptor = services[i];
            if (descriptor.IsKeyedService || !descriptor.ServiceType.IsInterface
                || descriptor.ServiceType.IsGenericTypeDefinition || MutatingMethods.Of(descriptor.ServiceType).Count == 0)
                continue;
            guard.MakeGenericMethod(descriptor.ServiceType).Invoke(null, [services, i, registeredTypes]);
        }
        return services;
    }

    private static void Guard<TService>(IServiceCollection services, int index, HashSet<Type> registeredTypes)
        where TService : class
    {
        var original = services[index];
        var key = $"mutation-guard:{index}";
        Func<IServiceProvider, GuardedInstance<TService>> holder = original switch
        {
            { ImplementationInstance: { } instance } => _ => new GuardedInstance<TService>((TService)instance, owned: false),
            { ImplementationFactory: { } factory } => sp => new GuardedInstance<TService>((TService)factory(sp), owned: true),
            { ImplementationType: { } type } when registeredTypes.Contains(type) =>
                sp => new GuardedInstance<TService>((TService)sp.GetRequiredService(type), owned: false),
            { ImplementationType: { } type } =>
                sp => new GuardedInstance<TService>((TService)ActivatorUtilities.CreateInstance(sp, type), owned: true),
            _ => throw new InvalidOperationException($"Registration of {typeof(TService)} has no implementation."),
        };

        services.Add(new ServiceDescriptor(typeof(GuardedInstance<TService>), key, (sp, _) => holder(sp), original.Lifetime));
        services[index] = new ServiceDescriptor(typeof(TService), sp => MutationScopeProxy<TService>.Wrap(
            sp.GetRequiredKeyedService<GuardedInstance<TService>>(key).Instance,
            sp.GetRequiredService<IMutationScopeMonitor>()), original.Lifetime);
    }
}

/// <summary>Holds the real service behind the scope guard and disposes it when the container created it.</summary>
public sealed class GuardedInstance<TService> : IDisposable, IAsyncDisposable where TService : class
{
    private readonly bool _owned;

    public GuardedInstance(TService instance, bool owned)
    {
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _owned = owned;
    }

    public TService Instance { get; }

    public void Dispose()
    {
        if (_owned && Instance is IDisposable disposable) disposable.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (!_owned) return ValueTask.CompletedTask;
        if (Instance is IAsyncDisposable asyncDisposable) return asyncDisposable.DisposeAsync();
        (Instance as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
