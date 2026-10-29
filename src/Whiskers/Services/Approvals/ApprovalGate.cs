using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Notifications;
using Whiskers.Utils;

namespace Whiskers.Services.Approvals;

/// <summary>What a <c>Confirm</c> verdict asks a human to approve.</summary>
/// <param name="ToolName">MCP tool name (or capability id) shown on the card and bound into the approval.</param>
/// <param name="ActorKey">Stable caller identity for approve-then-retry; two callers never share a grant.</param>
/// <param name="ActorType">Shown on the card: <c>mcp-direct</c>, <c>agent</c>, <c>automation</c>, …</param>
/// <param name="OperationId">Set by an interactive caller (the agent session) whose approval was raised for this
/// very operation; the gate then looks the approval up by it instead of raising a new one.</param>
public sealed record ApprovalSubject(
    string ToolName,
    string Level,
    string ActorKey,
    string ActorDisplay,
    string ActorType,
    IReadOnlyDictionary<string, JsonElement> Arguments,
    string? OperationId = null,
    string? SessionId = null)
{
    /// <summary>The recorded policy decision that asked for the approval (INV-22, R1-12).</summary>
    public string? DecisionId { get; init; }

    /// <summary>The preset that decision was made under (a trigger's pinned one, or the active one).</summary>
    public string? Preset { get; init; }

    /// <summary>What the call is bound to (R1-14, <see cref="ApprovalBinding"/>): principal, credential, capability
    /// and level, normalized arguments, resolved target and policy version. An approval is usable only for a call
    /// with the same binding; a subject without one can never use an approval.</summary>
    public string? Binding { get; init; }

    /// <summary>The principal ids that ask (R1-15): the caller first, then whoever it acts for. None of them may decide
    /// the approval unless the request is interactive (<see cref="ApprovalEligibility"/>).</summary>
    public IReadOnlyList<string> Requesters { get; init; } = Array.Empty<string>();

    /// <summary>Keep free-text payloads (SQL, a command, a compose file) off the stored card, showing their length
    /// only (R1-16): a person in the UI typed them — they can carry credentials in any syntax — and the approval row is
    /// kept like an audit row.</summary>
    public bool WithholdPayloads { get; init; }

    /// <summary>The target as the gateway resolved it (R1-15). Shown on the card; without it the card falls back to
    /// the arguments.</summary>
    public string? TargetServer { get; init; }
    public string? TargetWorkload { get; init; }

    /// <summary>What the notification names of the target (R1-15, <see cref="ApprovalDisplay.NotifiedTarget"/>).</summary>
    public string? NotifiedTarget { get; init; }
}

public sealed record ApprovalGateResult(bool Approved, string Message, string? ApprovalId = null, string? ApprovedBy = null);

/// <summary>
/// Turns a <c>Confirm</c> verdict into "go" only when a human approved exactly this call. Used by the Mutation
/// Gateway for every surface and by <see cref="DirectMcpCallGuard"/> for direct MCP reads. An approval is single-use,
/// bound to the arguments hash, and valid for <see cref="GrantLifetime"/> after it was decided. There is no API to
/// grant without an approval decided in <see cref="IApprovalStore"/>.
/// </summary>
public interface IApprovalGate
{
    /// <summary>Finds a usable approval for this call, or raises one. Does not use it up: the caller does that with
    /// <see cref="TryConsumeAsync"/> right before it runs the call, so a refusal in between leaves the approval valid.</summary>
    Task<ApprovalGateResult> CheckAsync(ApprovalSubject subject, string reason, IReadOnlyList<string> matchedRules,
        CancellationToken ct = default);

    /// <summary>Uses the approval once, for operation <paramref name="operationId"/>. <c>false</c> when it was used
    /// already (also before a restart), is no longer approved or has outlived its lifetime — the caller must then not
    /// run the call.</summary>
    Task<bool> TryConsumeAsync(string approvalId, string operationId, CancellationToken ct = default);
}

/// <summary>
/// Two ways to an approved call:
/// <list type="number">
/// <item>An interactive caller raised the approval itself (the agent session through <c>ApprovalCoordinator</c>)
/// and passes its operation id; the approval with that correlation id, session and tool is used.</item>
/// <item>Approve-then-retry for callers that cannot wait (an HTTP request): the first call raises an approval and is
/// refused; once a human approved it, the identical call (same binding) runs once.</item>
/// </list>
/// Either way the approval must carry the call's <see cref="ApprovalSubject.Binding"/> (R1-14): a different caller,
/// credential, argument, target or policy version never inherits it. Approvals, their decisions and their single
/// use are persisted by <see cref="IApprovalStore"/>, so they survive a restart.
/// </summary>
public sealed class ApprovalGate : IApprovalGate
{
    internal static TimeSpan GrantLifetime => ApprovalStore.GrantLifetime;
    private const string DirectSessionId = "mcp-direct";
    private const int MaxParamChars = 4000;

    private readonly IApprovalStore _approvals;
    private readonly IGuardrailStore? _policy;
    private readonly INotificationService? _notify;
    private readonly ILogger<ApprovalGate>? _logger;
    private readonly TimeProvider _time;
    private readonly WhiskersPublicUrl? _publicUrl;

    // Two concurrent first calls must raise one approval, not two (only one of which could ever be used).
    private readonly SemaphoreSlim _raiseLock = new(1, 1);

    public ApprovalGate(IApprovalStore approvals, IGuardrailStore? policy = null, INotificationService? notify = null,
        ILogger<ApprovalGate>? logger = null, TimeProvider? time = null, WhiskersPublicUrl? publicUrl = null)
    {
        _publicUrl = publicUrl;
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _policy = policy;
        _notify = notify;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<ApprovalGateResult> CheckAsync(ApprovalSubject subject, string reason,
        IReadOnlyList<string> matchedRules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(subject.Arguments);
        ArgumentNullException.ThrowIfNull(matchedRules);
        // Fail closed: a call whose binding is unknown can neither raise nor use an approval.
        if (string.IsNullOrWhiteSpace(subject.Binding))
            return new ApprovalGateResult(false,
                $"'{subject.ToolName}' needs a human approval, but the call could not be bound to one; it was not executed.");
        var argumentsHash = ApprovalFingerprint.OfArguments(subject.Arguments);

        // 1. The approval an interactive caller raised for this very operation.
        if (subject.OperationId is { } operationId && subject.SessionId is { } sessionId)
        {
            var own = _approvals.GetAll().FirstOrDefault(a =>
                a.CorrelationId == operationId && a.SessionId == sessionId && a.ToolName == subject.ToolName);
            if (own is not null)
            {
                // R1-05: the approval must have been raised (and so approved) at the operation's effective level.
                if (!LevelCovers(own, subject))
                    return new ApprovalGateResult(false,
                        $"Approval {own.Id} was given at level '{own.Level}', but '{subject.ToolName}' requires " +
                        $"'{subject.Level}'; the call was not executed.", own.Id);
                if (own.ArgumentsHash != argumentsHash)
                    return new ApprovalGateResult(false,
                        $"Approval {own.Id} was given for different arguments; the call was not executed.", own.Id);
                // R1-14: the same caller, credential, target and policy version as when it was decided.
                if (own.Binding != subject.Binding)
                    return new ApprovalGateResult(false,
                        $"Approval {own.Id} was given for a different caller, target or policy version; the call was not executed.",
                        own.Id);
                return Usable(own) ?? new ApprovalGateResult(false, own.ConsumedAt is not null
                        ? $"Approval {own.Id} for '{subject.ToolName}' was already used; the call was not executed."
                        : $"Approval {own.Id} for '{subject.ToolName}' is {own.Status.ToString().ToLowerInvariant()}; the call was not executed.",
                    own.Id);
            }
        }

        // 2–3. Approve-then-retry: use the approval raised for this binding, or raise one. Checked and raised under
        // one lock, so concurrent identical calls share a single approval.
        Approval approval;
        await _raiseLock.WaitAsync(ct);
        try
        {
            if (_approvals.FindOpen(subject.Binding) is { } raised && LevelCovers(raised, subject))
            {
                if (raised.Status == ApprovalStatus.Pending)
                    return new ApprovalGateResult(false,
                        $"Approval {raised.Id} for '{subject.ToolName}' is still pending. Repeat this exact call once a human has approved it.",
                        raised.Id);
                if (raised.ArgumentsHash == argumentsHash && Usable(raised) is { } granted)
                    return granted;
            }
            // None, or raised below what the operation now needs (a level was raised meanwhile): ask again.
            approval = await RaiseAsync(subject, reason, matchedRules, argumentsHash, ct);
        }
        catch (ApprovalLimitException ex)
        {
            // C-19: refused, not queued — the caller has enough approvals waiting already.
            return new ApprovalGateResult(false, $"{ex.Message} '{subject.ToolName}' was not executed.");
        }
        finally
        {
            _raiseLock.Release();
        }

        await NotifyAsync(subject, approval);
        return new ApprovalGateResult(false,
            $"Human approval required: {reason} Approval {approval.Id} was raised (Whiskers → Approvals). " +
            $"After it is approved, repeat this exact call within {GrantLifetime.TotalMinutes:0} minutes; it then runs once.",
            approval.Id);
    }

    public Task<bool> TryConsumeAsync(string approvalId, string operationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return _approvals.TryConsumeAsync(approvalId, operationId, _time.GetUtcNow().UtcDateTime, ct);
    }

    /// <summary>Fail closed: an approval without a readable level, or below the subject's level, does not count.</summary>
    private static bool LevelCovers(Approval approval, ApprovalSubject subject) =>
        !string.IsNullOrWhiteSpace(approval.Level)
        && McpPermissionLevels.HasAccess(McpPermissionLevels.Normalize(approval.Level), McpPermissionLevels.Normalize(subject.Level));

    /// <summary>Approved, within its lifetime and not used yet → a grant. Otherwise null. Uses nothing up.</summary>
    private ApprovalGateResult? Usable(Approval approval) =>
        approval.Status == ApprovalStatus.Approved && approval.ConsumedAt is null
        && approval.GrantExpiresAt is { } until && until > _time.GetUtcNow().UtcDateTime
            ? new ApprovalGateResult(true, "Approved.", approval.Id, approval.ResolvedBy)
            : null;

    private Task<Approval> RaiseAsync(ApprovalSubject subject, string reason, IReadOnlyList<string> matchedRules,
        string argumentsHash, CancellationToken ct)
    {
        var argsJson = subject.WithholdPayloads
            ? JsonSerializer.Serialize(Whiskers.Services.Mutations.AuditLogMutationSink.WithoutPayloads(subject.Arguments))
            : JsonSerializer.Serialize(subject.Arguments);
        var resolved = subject.TargetServer is not null || subject.TargetWorkload is not null;
        var (server, workload) = resolved
            ? (subject.TargetServer, subject.TargetWorkload)
            : ApprovalDisplay.ExtractTarget(argsJson);
        return _approvals.CreateAsync(new ApprovalRequest(
            SessionId: subject.SessionId ?? DirectSessionId,
            ToolCallId: Guid.NewGuid().ToString("N"),
            Actor: subject.ActorDisplay,
            ActorType: subject.ActorType,
            ToolName: subject.ToolName,
            Level: subject.Level,
            ParamsJson: Cap(SecretRedactor.Redact(argsJson), MaxParamChars),
            Reason: reason,
            CorrelationId: subject.OperationId,
            RiskLevel: ApprovalDisplay.RiskOf(subject.Level),
            TargetServer: server,
            TargetWorkload: workload,
            // The preset the decision was made under, not whichever is active by now (R1-12).
            GuardrailPreset: subject.Preset ?? _policy?.Config.ActivePreset,
            GuardrailRule: matchedRules.Count > 0 ? string.Join(", ", matchedRules) : null,
            ArgumentsHash: argumentsHash,
            DecisionId: subject.DecisionId,
            Binding: subject.Binding,
            Requesters: subject.Requesters),
            // The decision is read back from the store on the retry; nothing waits on it here.
            _ => Task.CompletedTask, ct);
    }

    private async Task NotifyAsync(ApprovalSubject subject, Approval approval)
    {
        try
        {
            if (_notify is not null)
                await _notify.SendAsync(new NotificationEvent
                {
                    EventType = "agent_approval",
                    ImageInfo = ApprovalDisplay.NotificationText(approval, subject.NotifiedTarget, _publicUrl),
                    CorrelationId = subject.OperationId,
                    ApprovalId = approval.Id,
                });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Approval notification for {Tool} failed", subject.ToolName);
        }
    }

    private static string? Cap(string? s, int max) =>
        s is null ? null : (s.Length > max ? s[..max] + "…" : s);
}
