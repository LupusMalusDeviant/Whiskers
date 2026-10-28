using System.Reflection;
using Whiskers.Configuration;
using Whiskers.Services.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
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
        builder.Services.AddGuardrailPolicy();
        builder.Services.AddMutationGateway();
        builder.Services.AddMutationScopeGuard();
    }

    /// <summary>
    /// Policy and approvals are core (PC-D8, WP2 step 2.1): the guardrail engine, its store and rule catalog, the
    /// approval store and gate, the direct-MCP guard and the principal resolver it needs. They used to be registered
    /// by the Agent module, so disabling the agent left direct MCP calls unguarded (TA-27).
    /// </summary>
    public static IServiceCollection AddGuardrailPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // The guardrail engine is stateless → a shared default rule set is enough.
        services.AddSingleton<IAgentGuardrailEngine>(GuardrailEngine.CreateDefault());
        services.AddSingletonWithInterface<GuardrailStore, IGuardrailStore>();
        services.AddSingleton<IGuardrailRuleCatalog, GuardrailRuleCatalog>();
        // R1-14: approvals live in the database; the store loads the open ones after the schema is migrated.
        services.AddSingleton<IApprovalPersistence, DbApprovalPersistence>();
        services.AddSingleton<IApprovalStore>(sp => new ApprovalStore(sp.GetService<ILogger<ApprovalStore>>(),
            persistence: sp.GetRequiredService<IApprovalPersistence>(),
            // R1-15: RequireSecondApprover is read from the active policy at each decision.
            policy: sp.GetRequiredService<Whiskers.Services.Guardrails.IGuardrailStore>()));
        // R1-15: absolute links in notifications come only from the configured public address, never a request host.
        services.AddSingleton(sp => Whiskers.Configuration.WhiskersPublicUrl.From(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IApprovalGate, ApprovalGate>();
        // R1-15: a chat ends its session (and its approvals) when the browser connection drops.
        services.AddScoped<Whiskers.Services.Agent.Approvals.ChatConnectionWatcher>();
        services.AddScoped<Whiskers.Services.Agent.Approvals.IChatConnectionWatcher>(sp =>
            sp.GetRequiredService<Whiskers.Services.Agent.Approvals.ChatConnectionWatcher>());
        services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(sp =>
            sp.GetRequiredService<Whiskers.Services.Agent.Approvals.ChatConnectionWatcher>());
        services.AddSingleton<IDirectMcpCallGuard, DirectMcpCallGuard>();
        services.AddSingleton<IAgentPrincipalResolver, AgentPrincipalResolver>();
        // Tool metadata for the guardrail editor; executing tools stays with the Agent module's invoker.
        services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        services.AddInitializable<GuardrailStore>();
        return services;
    }

    public static IServiceCollection AddMutationGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ICapabilityCatalog, CapabilityCatalog>();
        services.AddSingleton<ICapabilityOverrides, McpToolCapabilityOverrides>();
        services.AddSingleton<IMutationTargetResolver, MutationTargetResolver>();
        services.AddSingleton<IMutationPolicy, GuardrailMutationPolicy>();
        services.AddSingleton<IMutationAuditSink, AuditLogMutationSink>();
        // INV-22 (R1-12): every policy decision is recorded before it is acted on; a failed write refuses the call.
        services.AddSingleton<IPolicyDecisionStore, DbPolicyDecisionStore>();
        services.AddSingleton<IMutationScopeMonitor, MutationScopeMonitor>();
        services.AddSingleton<IEffectiveRequirementResolver, SchedulerRequirementResolver>();
        services.AddSingleton<IMutationGateway, MutationGateway>();
        // Per circuit: it reads the signed-in user.
        services.AddScoped<IUiMutationRunner, UiMutationRunner>();
        // Unattended runs on their owner's authority (PC-D6).
        services.AddOptions<AutomationOptions>().BindConfiguration(AutomationOptions.Section);
        services.AddSingleton<IOwnerAccounts, OwnerAccounts>();
        // Observes federated sign-ins of Blazor circuits so owners without a local account count as verified (R1-04).
        services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, FederatedSignInRecorder>();
        services.AddSingleton<IAutomationOwnerResolver, AutomationOwnerResolver>();
        services.AddSingleton<IAutomationOwnerStore, AutomationOwnerStore>();
        services.AddSingleton<IAutomationMutationRunner, AutomationMutationRunner>();
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
