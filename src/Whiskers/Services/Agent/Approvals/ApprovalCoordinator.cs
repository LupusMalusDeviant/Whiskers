using Whiskers.Models;
using Whiskers.Models.Agent;
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
    Task<Approval> RaiseAsync(IAgentSession session, AgentToolCall call, AgentContext context, string reason);
}

public sealed class ApprovalCoordinator : IApprovalCoordinator
{
    private const int MaxParamChars = 4000;

    private readonly IApprovalStore _store;
    private readonly INotificationService _notify;
    private readonly ILogger<ApprovalCoordinator>? _logger;

    public ApprovalCoordinator(
        IApprovalStore store, INotificationService notify, ILogger<ApprovalCoordinator>? logger = null)
    {
        _store = store;
        _notify = notify;
        _logger = logger;
    }

    public async Task<Approval> RaiseAsync(IAgentSession session, AgentToolCall call, AgentContext context, string reason)
    {
        var (actor, actorType) = ActorOf(context);
        var paramsJson = Cap(SecretRedactor.Redact(call.ArgumentsJson), MaxParamChars);

        var request = new ApprovalRequest(
            SessionId: session.SessionId,
            ToolCallId: call.Id,
            Actor: actor,
            ActorType: actorType,
            ToolName: call.Name,
            Level: "write",
            ParamsJson: paramsJson,
            Reason: reason);

        // The resolver reaches back to the still-running session that is awaiting the decision.
        var approval = _store.Create(request, approved => session.ResolveConfirmationAsync(call.Id, approved));

        // Push to the in-app bell (+ external channels if active). Never let a push failure block the flow.
        try
        {
            await _notify.SendAsync(new NotificationEvent
            {
                EventType = "agent_approval",
                ImageInfo = $"Agent \"{actor}\" wants to run \"{call.Name}\" — approval required.",
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
