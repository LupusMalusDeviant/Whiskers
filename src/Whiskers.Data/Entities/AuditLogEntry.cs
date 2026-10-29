namespace Whiskers.Models;

public class AuditLogEntity
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "";          // email or API key name
    public string ActorType { get; set; } = "";       // "web", "mcp", "system"
    public string Action { get; set; } = "";          // "container.start", "server.add", etc.
    public string TargetType { get; set; } = "";      // "container", "server", "config", "firewall"
    public string TargetId { get; set; } = "";        // container ID, server ID, etc.
    public string TargetName { get; set; } = "";      // human-readable name
    public string? Details { get; set; }              // JSON or free-text with additional context
    public string? ServerId { get; set; }
    public bool Success { get; set; } = true;

    // R1-19 (INV-45): the security fields of a gateway event, as columns of their own — never shortened with
    // Details, which is capped. Null on rows written outside the gateway (and before R1-19).

    /// <summary>The operation this event is about (R1-18).</summary>
    public string? OperationId { get; set; }
    /// <summary>The operation it ran inside, if nested.</summary>
    public string? ParentOperationId { get; set; }
    /// <summary>The recorded policy decision (INV-22); null when refused before the policy step.</summary>
    public string? DecisionId { get; set; }
    /// <summary>The AI trigger behind the call (<c>trigger:&lt;id&gt;</c>, R1-13).</summary>
    public string? TriggerId { get; set; }
    /// <summary>Where the call came from: Ui, Mcp, Agent, Automation …</summary>
    public string? Surface { get; set; }
    public string? PrincipalKind { get; set; }
    public string? PrincipalId { get; set; }
    public string? CredentialId { get; set; }
    /// <summary>Whom the principal acts for (an automation's owner, an agent's user).</summary>
    public string? OnBehalfOfId { get; set; }
    /// <summary>The approval the mutation ran under, or the one it waits for (pending).</summary>
    public string? ApprovalId { get; set; }
    /// <summary>Who gave that approval; null while it is pending.</summary>
    public string? ApprovedBy { get; set; }
    /// <summary><c>intent</c> while the mutation runs (or when its outcome could not be written yet), then
    /// <c>outcome</c>.</summary>
    public string? Phase { get; set; }
    /// <summary>Executed, Denied, PendingApproval, Failed — or Unknown (it was running when Whiskers stopped).</summary>
    public string? Outcome { get; set; }
    public string? Verification { get; set; }
    public long? DurationMs { get; set; }
}
