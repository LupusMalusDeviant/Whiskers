using System.Collections.Concurrent;
using System.Text.Json;
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
    /// <see cref="TryConsume"/> right before it runs the call, so a refusal in between leaves the approval valid.</summary>
    Task<ApprovalGateResult> CheckAsync(ApprovalSubject subject, string reason, IReadOnlyList<string> matchedRules,
        CancellationToken ct = default);

    /// <summary>Uses the approval once. <c>false</c> when it was used already, is no longer approved or has outlived
    /// its lifetime — the caller must then not run the call.</summary>
    bool TryConsume(string approvalId);
}

/// <summary>
/// Two ways to an approved call:
/// <list type="number">
/// <item>An interactive caller raised the approval itself (the agent session through <c>ApprovalCoordinator</c>)
/// and passes its operation id; the approval with that correlation id, session and tool is used.</item>
/// <item>Approve-then-retry for callers that cannot wait (an HTTP request): the first call raises an approval and is
/// refused; once a human approved it, the identical call (same caller, tool and arguments) runs once.</item>
/// </list>
/// </summary>
public sealed class ApprovalGate : IApprovalGate
{
    internal static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ConsumedRetention = TimeSpan.FromHours(1);
    private const string DirectSessionId = "mcp-direct";
    private const int MaxParamChars = 4000;

    private readonly IApprovalStore _approvals;
    private readonly IGuardrailStore? _policy;
    private readonly INotificationService? _notify;
    private readonly ILogger<ApprovalGate>? _logger;
    private readonly TimeProvider _time;

    // call fingerprint → approval raised for it; approval id → when it was used (single use).
    private readonly ConcurrentDictionary<string, string> _raised = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new();
    // Two concurrent first calls must raise one approval, not two (only one of which could ever be used).
    private readonly object _raiseLock = new();

    public ApprovalGate(IApprovalStore approvals, IGuardrailStore? policy = null, INotificationService? notify = null,
        ILogger<ApprovalGate>? logger = null, TimeProvider? time = null)
    {
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
        Sweep();
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
                return Usable(own) ?? new ApprovalGateResult(false, _consumed.ContainsKey(own.Id)
                        ? $"Approval {own.Id} for '{subject.ToolName}' was already used; the call was not executed."
                        : $"Approval {own.Id} for '{subject.ToolName}' is {own.Status.ToString().ToLowerInvariant()}; the call was not executed.",
                    own.Id);
            }
        }

        // 2–3. Approve-then-retry: use the approval raised for this call, or raise one. Checked and raised under one
        // lock, so concurrent identical calls share a single approval.
        var fingerprint = ApprovalFingerprint.Of(subject.ActorKey, subject.ToolName, subject.Arguments);
        Approval approval;
        lock (_raiseLock)
        {
            if (_raised.TryGetValue(fingerprint, out var raisedId) && _approvals.Get(raisedId) is { } raised)
            {
                if (!LevelCovers(raised, subject))
                {
                    // Raised below what the operation now needs (a level was raised meanwhile): ask again.
                    _raised.TryRemove(fingerprint, out _);
                }
                else if (raised.Status == ApprovalStatus.Pending && !raised.IsExpired)
                    return new ApprovalGateResult(false,
                        $"Approval {raised.Id} for '{subject.ToolName}' is still pending. Repeat this exact call once a human has approved it.",
                        raised.Id);
                else if (raised.ArgumentsHash == argumentsHash && Usable(raised) is { } granted)
                    return granted;
                else
                    // Rejected, expired or used: this call needs a new decision.
                    _raised.TryRemove(fingerprint, out _);
            }

            approval = Raise(subject, reason, matchedRules, argumentsHash);
            _raised[fingerprint] = approval.Id;
        }

        await NotifyAsync(subject);
        return new ApprovalGateResult(false,
            $"Human approval required: {reason} Approval {approval.Id} was raised (Whiskers → Approvals). " +
            $"After it is approved, repeat this exact call within {GrantLifetime.TotalMinutes:0} minutes; it then runs once.",
            approval.Id);
    }

    public bool TryConsume(string approvalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        if (_approvals.Get(approvalId) is not { } approval || Usable(approval) is null)
            return false;
        if (!_consumed.TryAdd(approvalId, _time.GetUtcNow()))
            return false;
        // A used approval answers no further retry; the next identical call raises a new one.
        foreach (var (fingerprint, id) in _raised)
            if (id == approvalId) _raised.TryRemove(fingerprint, out _);
        return true;
    }

    /// <summary>Approved, within its lifetime and not used yet → a grant. Otherwise null. Uses nothing up.</summary>
    /// <summary>Fail closed: an approval without a readable level, or below the subject's level, does not count.</summary>
    private static bool LevelCovers(Approval approval, ApprovalSubject subject) =>
        !string.IsNullOrWhiteSpace(approval.Level)
        && McpPermissionLevels.HasAccess(McpPermissionLevels.Normalize(approval.Level), McpPermissionLevels.Normalize(subject.Level));

    private ApprovalGateResult? Usable(Approval approval)
    {
        if (!IsLive(approval) || _consumed.ContainsKey(approval.Id))
            return null;
        return new ApprovalGateResult(true, "Approved.", approval.Id, approval.ResolvedBy);
    }

    private bool IsLive(Approval approval) =>
        approval.Status == ApprovalStatus.Approved && approval.ResolvedAt is { } decidedAt
        && DateTime.SpecifyKind(decidedAt, DateTimeKind.Utc) + GrantLifetime > _time.GetUtcNow().UtcDateTime;

    private Approval Raise(ApprovalSubject subject, string reason, IReadOnlyList<string> matchedRules,
        string argumentsHash)
    {
        var argsJson = JsonSerializer.Serialize(subject.Arguments);
        var (server, workload) = ApprovalDisplay.ExtractTarget(argsJson);
        var approval = _approvals.Create(new ApprovalRequest(
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
            DecisionId: subject.DecisionId),
            // The decision is read back from the store on the retry; nothing waits on it here.
            _ => Task.CompletedTask);
        return approval;
    }

    private async Task NotifyAsync(ApprovalSubject subject)
    {
        try
        {
            if (_notify is not null)
                await _notify.SendAsync(new NotificationEvent
                {
                    EventType = "agent_approval",
                    ImageInfo = $"\"{subject.ActorDisplay}\" wants to run \"{subject.ToolName}\" — approval required.",
                    CorrelationId = subject.OperationId,
                });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Approval notification for {Tool} failed", subject.ToolName);
        }
    }

    /// <summary>Forgets used approvals after <see cref="ConsumedRetention"/>, and retry entries whose approval can no
    /// longer answer a retry (gone, rejected, expired, or approved longer ago than its lifetime).</summary>
    private void Sweep()
    {
        var cutoff = _time.GetUtcNow() - ConsumedRetention;
        foreach (var (id, at) in _consumed)
            if (at < cutoff) _consumed.TryRemove(id, out _);

        lock (_raiseLock)
        {
            foreach (var (fingerprint, id) in _raised)
            {
                var approval = _approvals.Get(id);
                var open = approval is not null
                           && (approval.Status == ApprovalStatus.Pending ? !approval.IsExpired : IsLive(approval));
                if (!open) _raised.TryRemove(fingerprint, out _);
            }
        }
    }

    private static string? Cap(string? s, int max) =>
        s is null ? null : (s.Length > max ? s[..max] + "…" : s);
}
