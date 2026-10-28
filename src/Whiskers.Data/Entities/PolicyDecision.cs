namespace Whiskers.Models;

/// <summary>
/// One policy decision of the Mutation Gateway (INV-22, R1-12), written before the mutation runs. Approvals and
/// audit rows name it by <see cref="Id"/>. Rows are only ever inserted, never changed.
/// </summary>
public class PolicyDecisionEntity
{
    /// <summary>A new GUID ("N" format) per decision.</summary>
    public string Id { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    /// <summary>The gateway operation the decision belongs to.</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Stable principal id (<c>user:…</c>, <c>key:…</c>, <c>trigger:…</c>, …) and its kind.</summary>
    public string PrincipalId { get; set; } = string.Empty;
    public string PrincipalKind { get; set; } = string.Empty;

    /// <summary>The principal an agent or automation acted for, if any.</summary>
    public string? OnBehalfOfId { get; set; }

    /// <summary>The MCP key that authenticated the call, if any.</summary>
    public string? CredentialId { get; set; }

    /// <summary>The surface the call came in through (Mcp, Agent, Ui, Automation, Webhook, Terminal).</summary>
    public string Surface { get; set; } = string.Empty;

    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>The level the capability required when it was decided (after tool settings and R1-05).</summary>
    public string RequiredLevel { get; set; } = string.Empty;

    /// <summary>The resolved target: kind, canonical id, name, server and what else it touches (JSON list).</summary>
    public string TargetKind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string? TargetName { get; set; }
    public string? TargetServerId { get; set; }
    public string? TargetRelated { get; set; }

    /// <summary>SHA-256 of the caller's arguments in normalized form (keys sorted at every level, no whitespace).</summary>
    public string InputHash { get; set; } = string.Empty;

    /// <summary>The policy version the decision was made under (<see cref="PolicySnapshotEntity.VersionHash"/>).</summary>
    public string PolicyVersion { get; set; } = string.Empty;

    /// <summary>The preset that governed the call: the active one, a trigger's pinned one, or a fail-closed marker.</summary>
    public string Preset { get; set; } = string.Empty;

    /// <summary>The rule ids that produced the verdict (JSON list).</summary>
    public string MatchedRules { get; set; } = "[]";

    /// <summary>Allow, Confirm or Deny.</summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>The policy's reason, without payload values and redacted.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// An immutable policy version (R1-12): the policy exactly as it was evaluated, keyed by the hash of its content. A
/// changed policy is a new row; an existing row is never rewritten, so every decision can be replayed against the
/// policy it was made under.
/// </summary>
public class PolicySnapshotEntity
{
    /// <summary>SHA-256 of <see cref="PolicyJson"/>.</summary>
    public string VersionHash { get; set; } = string.Empty;

    /// <summary>The preset the policy first appeared under (the same policy under another name is the same version).</summary>
    public string Preset { get; set; } = string.Empty;

    /// <summary>The policy in canonical JSON form.</summary>
    public string PolicyJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
