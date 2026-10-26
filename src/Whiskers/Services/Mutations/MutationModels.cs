using System.Text.Json;

namespace Whiskers.Services.Mutations;

/// <summary>How much damage a capability can do. Drives defaults (approval, verification) in later WPs.</summary>
public enum CapabilityRisk
{
    /// <summary>Undone by the opposite action (start ↔ stop).</summary>
    Reversible,
    /// <summary>Interrupts service but loses no data (restart, reboot, reload).</summary>
    Disruptive,
    /// <summary>Loses data or cannot be undone (remove, delete snapshot, disable backups).</summary>
    Destructive,
    /// <summary>Equivalent to root on a host (shell, compose deploy, arbitrary SQL as superuser).</summary>
    RootEquivalent,
}

/// <summary>One mutating action in the capability catalog (ADR-0005, INV-13).</summary>
/// <param name="Id"><c>&lt;area&gt;.&lt;verb&gt;</c>, for example <c>container.start</c>.</param>
/// <param name="RequiredLevel"><c>read</c> / <c>write</c> / <c>admin</c> (see <c>McpPermissionLevels</c>).</param>
/// <param name="TargetKind">What the capability acts on: <c>container</c>, <c>host</c>, <c>cloud-server</c>, …</param>
/// <param name="McpTool">The MCP tool that exposes it, if any. Its level is the tool's level.</param>
/// <param name="Deadline">Upper bound for one execution (INV-40).</param>
public sealed record Capability(
    string Id,
    string RequiredLevel,
    string TargetKind,
    CapabilityRisk Risk,
    string? McpTool,
    TimeSpan Deadline);

/// <summary>Who acts (INV-01). Kept small in WP2; WP6 adds stable subject ids and session identity.</summary>
public enum MutationPrincipalKind { Human, ApiKey, Agent, Automation, Webhook, System }

/// <param name="Id">Stable identifier: email, <c>key:&lt;id&gt;</c>, <c>trigger:&lt;id&gt;</c>, <c>task:&lt;id&gt;</c>.</param>
/// <param name="DisplayName">What the audit log and the UI show.</param>
/// <param name="PermissionLevel">The principal's effective level (<c>read</c> / <c>write</c> / <c>admin</c>).</param>
/// <param name="CredentialId">The MCP key id when a key authenticated the call.</param>
/// <param name="OnBehalfOf">The principal an agent or automation acts for; its rights are the ceiling.</param>
public sealed record MutationPrincipal(
    MutationPrincipalKind Kind,
    string Id,
    string DisplayName,
    string PermissionLevel,
    string? CredentialId = null,
    MutationPrincipal? OnBehalfOf = null);

/// <summary>The entry point a mutation came in through.</summary>
public enum MutationSurface { Mcp, Agent, Ui, Automation, Webhook, Terminal }

/// <summary>The resolved target of a mutation (INV-57). Resolution itself moves into the gateway in WP2 step 2.1.</summary>
public sealed record MutationTarget(string Kind, string Id, string? Name = null, string? ServerId = null);

/// <summary>Everything the gateway needs to decide on and record one mutation.</summary>
/// <param name="OperationId">Idempotency and correlation key (INV-39); the gateway generates one when null.</param>
/// <param name="SessionId">Agent session or UI circuit the call belongs to, if any.</param>
public sealed record MutationRequest(
    string CapabilityId,
    MutationPrincipal Principal,
    MutationSurface Surface,
    MutationTarget Target,
    IReadOnlyDictionary<string, JsonElement> Arguments,
    string? OperationId = null,
    string? SessionId = null);

public enum MutationStatus
{
    /// <summary>The delegate ran and returned.</summary>
    Executed,
    /// <summary>Refused before execution (unknown capability, level, policy, audit unavailable).</summary>
    Denied,
    /// <summary>Policy requires a human approval that has not been given yet.</summary>
    PendingApproval,
    /// <summary>The delegate ran and threw, or exceeded its deadline.</summary>
    Failed,
}

public enum VerificationStatus { NotRun, Verified, Failed, NotVerifiable }

/// <summary>Result of one gateway call. <see cref="Value"/> is set only for <see cref="MutationStatus.Executed"/>.</summary>
public sealed record MutationOutcome<T>(
    string OperationId,
    MutationStatus Status,
    T? Value,
    string? Reason,
    VerificationStatus Verification,
    Exception? Error = null)
{
    public bool Succeeded => Status == MutationStatus.Executed;
}

/// <summary>Handed to the execution delegate: the decided request and its capability.</summary>
public sealed record MutationContext(string OperationId, MutationRequest Request, Capability Capability);
