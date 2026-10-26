using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Guardrails;

namespace Whiskers.Services.Mutations;

public enum MutationVerdict { Allow, Confirm, Deny }

/// <summary>The policy step's answer for one request. WP4 persists it as a decision record (INV-22).</summary>
public sealed record MutationPolicyDecision(MutationVerdict Verdict, string Reason, IReadOnlyList<string> MatchedRules);

/// <summary>Step 3 of the gateway pipeline: may this principal perform this capability on this target now?</summary>
public interface IMutationPolicy
{
    MutationPolicyDecision Evaluate(MutationRequest request, Capability capability);
}

/// <summary>
/// Adapter onto today's guardrail engine. Until WP2 step 2.1 moves the engine into core (PC-D8), it lives in the
/// Agent module; when that module is off, no engine is registered and every mutation is refused here — fail-closed,
/// never a silent Allow (INV-25, INV-26). Nothing calls the gateway yet, so this changes no current behaviour.
/// </summary>
public sealed class GuardrailMutationPolicy : IMutationPolicy
{
    private readonly IAgentGuardrailEngine? _engine;
    private readonly IGuardrailStore? _store;

    public GuardrailMutationPolicy(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _engine = services.GetService<IAgentGuardrailEngine>();
        _store = services.GetService<IGuardrailStore>();
    }

    public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capability);

        if (_engine is null || _store is null)
            return new MutationPolicyDecision(MutationVerdict.Deny,
                "No policy engine is registered (Agent module disabled); the mutation was refused.", []);

        var context = new AgentContext(
            request.SessionId ?? request.OperationId ?? "mutation-gateway",
            ToAgentPrincipal(request.Principal),
            ToOrigin(request.Surface),
            _store.Current);
        var decision = _engine.Evaluate(new GuardrailRequest(
            capability.McpTool ?? capability.Id, capability.RequiredLevel, request.Arguments, context));

        var verdict = decision.Verdict switch
        {
            GuardrailVerdict.Allow => MutationVerdict.Allow,
            GuardrailVerdict.Confirm => MutationVerdict.Confirm,
            _ => MutationVerdict.Deny,
        };
        return new MutationPolicyDecision(verdict, decision.Reason, decision.MatchedRuleIds);
    }

    private static AgentPrincipal ToAgentPrincipal(MutationPrincipal p) => p.Kind == MutationPrincipalKind.ApiKey
        ? new AgentPrincipal(AgentPrincipalKind.McpKey, p.DisplayName, McpPermissionLevels.Normalize(p.PermissionLevel),
            null, McpKeyId: p.CredentialId ?? p.Id)
        : new AgentPrincipal(AgentPrincipalKind.WebUser, p.DisplayName, McpPermissionLevels.Normalize(p.PermissionLevel),
            null, UserEmail: p.Id);

    private static AgentOrigin ToOrigin(MutationSurface surface) => surface switch
    {
        MutationSurface.Mcp => AgentOrigin.McpDirect,
        MutationSurface.Automation or MutationSurface.Webhook => AgentOrigin.Trigger,
        _ => AgentOrigin.WebUi,
    };
}
