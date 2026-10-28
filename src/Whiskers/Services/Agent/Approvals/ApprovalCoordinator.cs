using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Utils;

namespace Whiskers.Services.Agent.Approvals;

/// <summary>Bridges a guardrail <c>Confirm</c> into a Human-in-the-Loop approval: registers it in the
/// <see cref="IApprovalStore"/> (with a resolver wired to the waiting session) and pushes a notification
/// to the in-app bell (and Mattermost/Matrix if configured). The "Freigaben" page and the bell then
/// resolve it via <see cref="IApprovalStore.ResolveAsync"/>.</summary>
public interface IApprovalCoordinator
{
    /// <summary>Raises an approval for a tool call awaiting confirmation on <paramref name="session"/>.
    /// Returns the created approval (its <c>Id</c> is what the UI resolves).</summary>
    Task<Approval> RaiseAsync(IAgentSession session, AgentToolCall call, AgentContext context, string reason, GuardrailDecision decision);
}

public sealed class ApprovalCoordinator : IApprovalCoordinator
{
    private const int MaxParamChars = 4000;

    private readonly IApprovalStore _store;
    private readonly INotificationService _notify;
    private readonly IGuardrailStore _guardrails;
    private readonly ILogger<ApprovalCoordinator>? _logger;
    private readonly ICapabilityOverrides? _overrides;
    private readonly ICapabilityCatalog? _catalog;
    private readonly IEffectiveRequirementResolver? _requirements;

    public ApprovalCoordinator(
        IApprovalStore store, INotificationService notify, IGuardrailStore guardrails,
        ILogger<ApprovalCoordinator>? logger = null, ICapabilityOverrides? overrides = null,
        ICapabilityCatalog? catalog = null, IEffectiveRequirementResolver? requirements = null)
    {
        _overrides = overrides;
        _catalog = catalog;
        _requirements = requirements;
        _store = store;
        _notify = notify;
        _guardrails = guardrails;
        _logger = logger;
    }

    public async Task<Approval> RaiseAsync(IAgentSession session, AgentToolCall call, AgentContext context, string reason, GuardrailDecision decision)
    {
        var (actor, actorType) = ActorOf(context);
        var paramsJson = Cap(SecretRedactor.Redact(call.ArgumentsJson), MaxParamChars);
        // Real required level of the tool (was hardcoded "write"), so the card and the resolver-level
        // check reflect what the tool actually needs.
        var level = McpPermissionLevels.DefaultToolLevels.GetValueOrDefault(call.Name, "write");
        // A stricter level stored in Settings → MCP raises what the approver must hold.
        if (_overrides is not null)
            level = _overrides.ResolveTool(call.Name, level).RequiredLevel;
        var arguments = AgentArgumentBinder.ParseArguments(call.ArgumentsJson);
        // R1-05: the approver is held to what the call really does (a scheduled CustomCommand is host.exec), the same
        // effective requirement the Mutation Gateway checks before it accepts this approval. Fail closed: if it
        // cannot be determined, the approval asks for Admin.
        if (_requirements is not null && _catalog?.FindByMcpTool(call.Name) is { } capability)
        {
            try
            {
                var effective = await _requirements.ResolveAsync(capability with { RequiredLevel = level }, arguments, default);
                level = SchedulerRequirementResolver.Stricter(level, effective);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Effective requirement of {Tool} could not be resolved; the approval asks for Admin", call.Name);
                level = McpPermissionLevels.Admin;
            }
        }
        var (targetServer, targetWorkload) = ApprovalDisplay.ExtractTarget(call.ArgumentsJson);
        var rule = decision.MatchedRuleIds.Count > 0 ? string.Join(", ", decision.MatchedRuleIds) : null;

        var request = new ApprovalRequest(
            SessionId: session.SessionId,
            ToolCallId: call.Id,
            Actor: actor,
            ActorType: actorType,
            ToolName: call.Name,
            Level: level,
            ParamsJson: paramsJson,
            Reason: reason,
            CorrelationId: call.CorrelationId,
            RiskLevel: ApprovalDisplay.RiskOf(level),
            TargetServer: targetServer,
            TargetWorkload: targetWorkload,
            // The preset of the recorded decision that asked (a pinned run's own), else the active one.
            GuardrailPreset: decision.Preset ?? _guardrails.Config.ActivePreset,
            GuardrailRule: rule,
            // Binds the approval to these exact arguments: the Mutation Gateway runs the call only if they match.
            ArgumentsHash: ApprovalFingerprint.OfArguments(arguments),
            // R1-12: the recorded gateway decision that asked for this approval.
            DecisionId: decision.DecisionId,
            // R1-14: the gateway runs the call only for the caller, target and policy version decided here.
            Binding: decision.Binding,
            Interactive: true);

        // The resolver reaches back to the still-running session that is awaiting the decision.
        var approval = await _store.CreateAsync(request, approved => session.ResolveConfirmationAsync(call.Id, approved));

        // Push to the in-app bell (+ external channels if active). Never let a push failure block the flow.
        try
        {
            await _notify.SendAsync(new NotificationEvent
            {
                EventType = "agent_approval",
                ImageInfo = $"Agent \"{actor}\" wants to run \"{call.Name}\" — approval required.",
                CorrelationId = call.CorrelationId,
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Approval notification for {Tool} failed", call.Name);
        }

        return approval;
    }

    private static (string actor, string actorType) ActorOf(AgentContext ctx)
    {
        var actor = PrincipalIds.Of(ctx.Principal);
        var type = ctx.Origin switch
        {
            AgentOrigin.WebUi => "agent-web",
            AgentOrigin.McpTool => "agent-mcp",
            AgentOrigin.Trigger => "trigger",
            _ => "agent",
        };
        return (actor, type);
    }

    private static string? Cap(string? s, int max) =>
        s is null ? null : (s.Length > max ? s[..max] + "…" : s);
}
