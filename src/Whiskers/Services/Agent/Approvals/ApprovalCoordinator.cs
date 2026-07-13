using System.Text.Json;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Guardrails;
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

    public ApprovalCoordinator(
        IApprovalStore store, INotificationService notify, IGuardrailStore guardrails,
        ILogger<ApprovalCoordinator>? logger = null)
    {
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
        var (targetServer, targetWorkload) = ExtractTarget(call.ArgumentsJson);
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
            RiskLevel: RiskOf(level),
            TargetServer: targetServer,
            TargetWorkload: targetWorkload,
            GuardrailPreset: _guardrails.Config.ActivePreset,
            GuardrailRule: rule);

        // The resolver reaches back to the still-running session that is awaiting the decision.
        var approval = _store.Create(request, approved => session.ResolveConfirmationAsync(call.Id, approved));

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

    /// <summary>Coarse risk band from the tool's required permission level (display-only).</summary>
    private static string RiskOf(string level) => McpPermissionLevels.Normalize(level) switch
    {
        "admin" => "high",
        "write" => "medium",
        _ => "low",
    };

    // Well-known argument keys that name the target of an operation. Best-effort and display-only —
    // it never changes what executes (the call is bound by its ToolCallId).
    private static readonly string[] ServerKeys = { "serverId", "server", "serverName" };
    private static readonly string[] WorkloadKeys = { "containerId", "container", "name", "pod", "workload", "service" };

    private static (string? server, string? workload) ExtractTarget(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            return (FirstString(doc.RootElement, ServerKeys), FirstString(doc.RootElement, WorkloadKeys));
        }
        catch (JsonException) { return (null, null); }
    }

    private static string? FirstString(JsonElement obj, string[] keys)
    {
        foreach (var k in keys)
            if (obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        return null;
    }

    private static (string actor, string actorType) ActorOf(AgentContext ctx)
    {
        var p = ctx.Principal;
        var actor = p.UserEmail ?? p.McpKeyId ?? p.DisplayName;
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
