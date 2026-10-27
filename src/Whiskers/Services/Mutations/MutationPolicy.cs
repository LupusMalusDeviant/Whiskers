using System.Text.Json;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Guardrails;

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
/// Adapter onto the guardrail engine, which is core since WP2 step 2.1 (PC-D8) and evaluates the same active preset
/// for every surface. Should the engine or the policy store ever be missing, every mutation is refused here —
/// fail-closed, never a silent Allow (INV-25, INV-26).
/// </summary>
public sealed class GuardrailMutationPolicy : IMutationPolicy
{
    // PC-D1: a human in the UI is bound by authorization, the kill switch and protected resources only.
    private static readonly IAgentGuardrailEngine HumanEngine = GuardrailEngine.CreateForHumans();

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
                "No policy engine is registered; the mutation was refused.", []);

        var context = new AgentContext(
            request.SessionId ?? request.OperationId ?? "mutation-gateway",
            ToAgentPrincipal(request.Principal),
            ToOrigin(request),
            _store.Current);
        // A person in the UI, and an unattended item running on a person's authority (PC-D6), are bound by the PC-D1
        // rules: the owner decided when creating the item. An item owned by an API key keeps every rule that binds
        // the key (tool lists, forbidden arguments, confirmation) — scheduling is not a way around them; a run that
        // needs an approval is refused, since nobody is there to give it. An item from before owners were recorded
        // runs in the grace period with the PC-D1 rules (user decision).
        var engine = (request.Surface == MutationSurface.Ui && request.Principal.Kind == MutationPrincipalKind.Human)
                     || (request.Surface == MutationSurface.Automation && request.Principal.Kind == MutationPrincipalKind.Automation
                         && request.Principal.OnBehalfOf?.Kind is null or MutationPrincipalKind.Human)
            ? HumanEngine
            : _engine;
        var decision = engine.Evaluate(new GuardrailRequest(
            capability.McpTool ?? capability.Id, capability.RequiredLevel, WithResolvedTarget(request), context));

        var verdict = decision.Verdict switch
        {
            GuardrailVerdict.Allow => MutationVerdict.Allow,
            GuardrailVerdict.Confirm => MutationVerdict.Confirm,
            _ => MutationVerdict.Deny,
        };
        return new MutationPolicyDecision(verdict, decision.Reason, decision.MatchedRuleIds);
    }

    /// <summary>The rules match argument values, so the resolved target (full container id and name) is added for
    /// evaluation only: a unique id prefix of a protected container is then caught as well. The approval hash and
    /// the audit keep the caller's original arguments.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> WithResolvedTarget(MutationRequest request)
    {
        var t = request.Target;
        if (string.IsNullOrEmpty(t.Id) && string.IsNullOrEmpty(t.Name))
            return request.Arguments;
        var args = new Dictionary<string, JsonElement>(request.Arguments);
        if (!string.IsNullOrEmpty(t.Id)) args["__resolvedTargetId"] = JsonSerializer.SerializeToElement(t.Id);
        if (!string.IsNullOrEmpty(t.Name)) args["__resolvedTargetName"] = JsonSerializer.SerializeToElement(t.Name);
        return args;
    }

    /// <summary>The engine sees the principal whose rights bound the call: for an agent that is the user or key it
    /// acts for, with that key's tool list, exactly as <c>AgentSession</c> evaluates it.</summary>
    private static AgentPrincipal ToAgentPrincipal(MutationPrincipal principal)
    {
        var p = principal.Kind is MutationPrincipalKind.Agent or MutationPrincipalKind.Automation
                && principal.OnBehalfOf is { } source ? source : principal;
        var level = McpPermissionLevels.Normalize(p.PermissionLevel);
        return p.Kind is MutationPrincipalKind.ApiKey or MutationPrincipalKind.Automation
            ? new AgentPrincipal(AgentPrincipalKind.McpKey, p.DisplayName, level, p.AllowedTools,
                McpKeyId: p.CredentialId ?? p.Id)
            : new AgentPrincipal(AgentPrincipalKind.WebUser, p.DisplayName, level, p.AllowedTools, UserEmail: p.Id);
    }

    private static AgentOrigin ToOrigin(MutationRequest request) => request.Surface switch
    {
        MutationSurface.Mcp => AgentOrigin.McpDirect,
        MutationSurface.Automation or MutationSurface.Webhook => AgentOrigin.Trigger,
        MutationSurface.Agent => request.Principal.OnBehalfOf?.Kind switch
        {
            MutationPrincipalKind.Automation => AgentOrigin.Trigger,
            MutationPrincipalKind.ApiKey => AgentOrigin.McpTool,
            _ => AgentOrigin.WebUi,
        },
        _ => AgentOrigin.WebUi,
    };
}
