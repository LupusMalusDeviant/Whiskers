namespace Whiskers.Models;

/// <summary>
/// The execution claim of one mutation (R1-18, INV-39): written before the mutation runs, keyed by its operation id,
/// so the same id cannot run twice — not concurrently, not after it finished, not after a restart. Rows are never
/// deleted.
/// </summary>
public class OperationRecordEntity
{
    /// <summary>The operation id: the caller's (an agent tool call's correlation id) or a new GUID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The operation this one runs inside (a mutation nested in another one's execution), or null.</summary>
    public string? ParentId { get; set; }

    public string CapabilityId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>SHA-256 of principal, capability and arguments: a retry with the same id must ask for the same thing.</summary>
    public string InputHash { get; set; } = string.Empty;

    /// <summary><c>Running</c>, <c>Executed</c>, <c>Failed</c> or <c>Released</c> (refused before it ran; the id may
    /// be used again).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The process that claimed it: a <c>Running</c> row of another process was interrupted by a restart,
    /// and its outcome is unknown.</summary>
    public string Instance { get; set; } = string.Empty;

    public string? DecisionId { get; set; }
    public string? ApprovalId { get; set; }
    public long? AuditId { get; set; }

    /// <summary>The verification of a finished run (<c>Verified</c>, <c>Failed</c>, …), null before it finished.</summary>
    public string? Verification { get; set; }

    public DateTime ClaimedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
