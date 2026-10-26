using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models.Agent;
using Whiskers.Modules;
using Whiskers.Services.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>Shared helper for the agent-tooling tests: builds an <see cref="AgentToolRegistry"/> from the MCP
/// tool types the default-enabled modules contribute — the production tool set the registry now derives from
/// (RoadToSAP §2.3) via <see cref="IModuleRegistry"/>, instead of the old whole-assembly reflection scan.</summary>
internal static class AgentToolTestHelpers
{
    public static IReadOnlyList<Type> DefaultModuleToolTypes() =>
        ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build())
            .SelectMany(m => m.McpToolTypes)
            .ToList();

    public static AgentToolRegistry DefaultRegistry() => new(DefaultModuleToolTypes());

    /// <summary>A real Mutation Gateway (catalog, guardrail policy, approval gate) with an in-memory audit sink, for
    /// tests that drive mutating tools through <see cref="AgentToolInvoker"/>.</summary>
    public static MutationGateway Gateway(GuardrailPolicy policy, IApprovalStore? approvals = null,
        IMutationAuditSink? audit = null)
    {
        var store = new FixedGuardrailStore(policy);
        return new MutationGateway(new CapabilityCatalog(), Policy(store), audit ?? new NullMutationAudit(), [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(approvals ?? new ApprovalStore(), store));
    }

    /// <summary>The production policy adapter over the default rule set and a fixed preset.</summary>
    public static IMutationPolicy Policy(IGuardrailStore store) =>
        new GuardrailMutationPolicy(new ServiceCollection()
            .AddSingleton<IAgentGuardrailEngine>(GuardrailEngine.CreateDefault())
            .AddSingleton(store)
            .BuildServiceProvider());

    internal sealed class FixedGuardrailStore(GuardrailPolicy policy) : IGuardrailStore
    {
        public GuardrailPolicy Current => policy;
        public GuardrailConfig Config { get; } = new();
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy p, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    /// <summary>Accepts every write and remembers the capabilities it saw.</summary>
    internal sealed class NullMutationAudit : IMutationAuditSink
    {
        public List<string> Events { get; } = [];
        public List<string?> Reasons { get; } = [];

        /// <summary>Makes the next intent write fail, as a full disk or a lost database would.</summary>
        public bool FailNextBegin { get; set; }

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            if (FailNextBegin)
            {
                FailNextBegin = false;
                throw new IOException("audit store unavailable");
            }
            lock (Events) Events.Add($"begin:{capability.Id}:{approval?.ApprovalId}");
            return Task.FromResult((long)Events.Count);
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct)
        {
            lock (Events) Events.Add($"complete:{status}");
            lock (Events) Reasons.Add(reason);
            return Task.CompletedTask;
        }

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            lock (Events) Events.Add($"denied:{status}");
            return Task.CompletedTask;
        }
    }
}
