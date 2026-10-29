namespace Whiskers.Models;

/// <summary>
/// One Human-in-the-Loop approval (R1-14, INV-30/31/35), persisted from the moment it is raised so that its decision
/// and its single use survive a restart. Rows are never deleted: the approvals history is complete.
/// </summary>
public class ApprovalRecordEntity
{
    /// <summary>A new GUID ("N" format) per approval.</summary>
    public string Id { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;
    public string ToolCallId { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string ActorType { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;

    /// <summary>The level an approver must hold (read, write, admin).</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>The arguments as shown on the card, secrets redacted.</summary>
    public string? ParamsJson { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Diff { get; set; }
    public string? CorrelationId { get; set; }
    public string? RiskLevel { get; set; }
    public string? TargetServer { get; set; }
    public string? TargetWorkload { get; set; }
    public string? GuardrailPreset { get; set; }
    public string? GuardrailRule { get; set; }
    public string? ArgumentsHash { get; set; }

    /// <summary>The recorded policy decision that asked for the approval (R1-12).</summary>
    public string? DecisionId { get; set; }

    /// <summary>What the approval is bound to (R1-14): a hash over the principal, credential, capability and level,
    /// the normalized arguments, the resolved target and the policy version. Only a call with the same binding can
    /// use it.</summary>
    public string? Binding { get; set; }

    /// <summary>Raised for a live agent session that waits on it; such an approval cannot outlive the process.</summary>
    public bool Interactive { get; set; }

    /// <summary>The principal ids that asked for the approval (R1-15), one per line, the requesting principal first.
    /// None of them may decide a non-interactive request.</summary>
    public string? RequesterIds { get; set; }

    /// <summary>Decided by one of its requesters (an interactive request, R1-15): a self-approval on record.</summary>
    public bool SelfApproved { get; set; }

    /// <summary>Pending, Approved, Rejected, Expired or Cancelled.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Until when a pending approval can be decided.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }

    /// <summary>Until when an approved approval can be used.</summary>
    public DateTime? GrantExpiresAt { get; set; }

    /// <summary>When the approval was used, and by which gateway operation. Set once; a used approval is spent.</summary>
    public DateTime? ConsumedAt { get; set; }
    public string? ConsumedBy { get; set; }
}
