namespace Whiskers.Models.Agent;

/// <summary>Lifecycle of a Human-in-the-Loop approval.</summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Cancelled,
}

/// <summary>The inputs needed to raise an approval — captured when an agent tool call
/// returns a <c>Confirm</c> verdict and a human must decide.</summary>
public sealed record ApprovalRequest(
    string SessionId,
    string ToolCallId,
    string Actor,
    string ActorType,
    string ToolName,
    string Level,
    string? ParamsJson,
    string Reason,
    string? Diff = null,
    // WP-05 correlation + richer approval context (all display-only; the executed call is bound by
    // ToolCallId, never by these fields).
    string? CorrelationId = null,
    string? RiskLevel = null,
    string? TargetServer = null,
    string? TargetWorkload = null,
    string? GuardrailPreset = null,
    string? GuardrailRule = null,
    // ADR-0005: hash of exactly the arguments that were shown for approval. The Mutation Gateway runs an approved
    // mutation only when its arguments hash to the same value.
    string? ArgumentsHash = null,
    // R1-12: the recorded policy decision that asked for this approval.
    string? DecisionId = null,
    // R1-14: what the approval is bound to (ApprovalBinding); null binds it to nothing, so it can never be used.
    string? Binding = null,
    // R1-14: a live agent session waits on it (the resolver reaches back to that session).
    bool Interactive = false);

/// <summary>A pending (or resolved) request for a human to approve/reject a single call. Persisted from the moment it
/// is raised (R1-14), with its decision and its single use; an <see cref="Interactive"/> one, whose resolver reaches a
/// live agent session, is cancelled by a restart (the session does not survive it).</summary>
public sealed class Approval
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string SessionId { get; init; }
    public required string ToolCallId { get; init; }

    /// <summary>Email / key name / "ai-trigger:…" that the agent is acting as.</summary>
    public required string Actor { get; init; }
    /// <summary>agent-web | agent-mcp | trigger.</summary>
    public required string ActorType { get; init; }

    public required string ToolName { get; init; }
    /// <summary>Required permission level of the tool: read | write | admin.</summary>
    public string Level { get; init; } = "write";

    /// <summary>The tool arguments as JSON, secrets redacted.</summary>
    public string? ParamsJson { get; init; }
    /// <summary>Why the guardrail asked for confirmation.</summary>
    public required string Reason { get; init; }
    /// <summary>Optional human-readable change preview (reserved; real per-tool diffs are roadmap).</summary>
    public string? Diff { get; init; }

    // WP-05: correlation + richer context so the approval card explains what is being decided.
    // These are display-only — execution is bound to <see cref="ToolCallId"/>, not to these values.
    /// <summary>Correlation id shared with the recorded history entry and the raised notification.</summary>
    public string? CorrelationId { get; init; }
    /// <summary>Coarse risk level derived from the tool's required permission: low | medium | high.</summary>
    public string? RiskLevel { get; init; }
    /// <summary>Best-effort target server id/name parsed from the (redacted) arguments, if present.</summary>
    public string? TargetServer { get; init; }
    /// <summary>Best-effort target workload (container/pod/name) parsed from the arguments, if present.</summary>
    public string? TargetWorkload { get; init; }
    /// <summary>The active guardrail preset that produced the Confirm verdict.</summary>
    public string? GuardrailPreset { get; init; }
    /// <summary>The guardrail rule id(s) that matched.</summary>
    public string? GuardrailRule { get; init; }
    /// <summary>Hash of the approved arguments (<c>ApprovalFingerprint.OfArguments</c>); the gateway executes only
    /// a call whose arguments produce the same hash. Null for approvals raised before ADR-0005.</summary>
    public string? ArgumentsHash { get; init; }

    /// <summary>The recorded policy decision (INV-22) that asked for this approval — the gateway's decision when it
    /// raised the approval, or the one an agent session had the gateway record before it paused (R1-12). The audit
    /// row of the execution names its own decision and this approval.</summary>
    public string? DecisionId { get; init; }

    /// <summary>What the approval is bound to (R1-14, <c>ApprovalBinding</c>): principal, credential, capability and
    /// level, normalized arguments, resolved target and policy version. Only a call with the same binding can use it;
    /// an approval without one can never be used.</summary>
    public string? Binding { get; init; }

    /// <summary>A live agent session waits on this approval.</summary>
    public bool Interactive { get; init; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; } = DateTime.UtcNow.AddMinutes(15);

    public DateTime? ResolvedAt { get; set; }
    /// <summary>Who decided (web user email), or "system" for an expiry/cancel.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>Until when an approved approval can be used (set when it is approved).</summary>
    public DateTime? GrantExpiresAt { get; set; }

    /// <summary>When the approval was used, and by which operation. A used approval is spent.</summary>
    public DateTime? ConsumedAt { get; set; }
    public string? ConsumedBy { get; set; }

    public bool IsPending => Status == ApprovalStatus.Pending;
    public bool IsExpired => Status == ApprovalStatus.Pending && DateTime.UtcNow >= ExpiresAt;
}
