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

/// <summary>Who acts (INV-01). Ids follow <see cref="PrincipalIds"/>.</summary>
public enum MutationPrincipalKind { Human, ApiKey, Agent, Automation, Webhook, System }

/// <param name="Id">Stable identifier (<see cref="PrincipalIds"/>): <c>user:&lt;email&gt;</c>, <c>key:&lt;id&gt;</c>,
/// <c>trigger:&lt;id&gt;</c>, <c>agent:&lt;session id&gt;</c>, <c>task:&lt;id&gt;</c>. Never derived from a display name.</param>
/// <param name="DisplayName">What the audit log and the UI show.</param>
/// <param name="PermissionLevel">The principal's effective level (<c>read</c> / <c>write</c> / <c>admin</c>).</param>
/// <param name="CredentialId">The MCP key id when a key authenticated the call.</param>
/// <param name="OnBehalfOf">The principal an agent or automation acts for; its rights are the ceiling.</param>
/// <param name="AllowedTools">The MCP key's tool list, if it has one; the policy treats it as a ceiling.</param>
/// <param name="SessionId">The agent session the principal acts in, if any.</param>
/// <param name="DelegatedBy">For an automation: the principal id of the call that set the delegation up (e.g.
/// <c>agent:&lt;session&gt;</c> when an agent created a task for its user). Provenance only, never authority.</param>
public sealed record MutationPrincipal(
    MutationPrincipalKind Kind,
    string Id,
    string DisplayName,
    string PermissionLevel,
    string? CredentialId = null,
    MutationPrincipal? OnBehalfOf = null,
    IReadOnlyList<string>? AllowedTools = null,
    string? SessionId = null,
    string? DelegatedBy = null);

/// <summary>The entry point a mutation came in through.</summary>
public enum MutationSurface { Mcp, Agent, Ui, Automation, Webhook, Terminal }

/// <summary>The target of a mutation (INV-57). Entry points pass what the caller named; the gateway resolves it
/// (<see cref="IMutationTargetResolver"/>) before policy runs and records the resolved form.</summary>
public sealed record MutationTarget(string Kind, string Id, string? Name = null, string? ServerId = null)
{
    /// <summary>What the operation also touches (R1-06): the containers attached to a network, the container a
    /// network is connected to, a workload's pod. Policy matches protected resources on these as well.</summary>
    public IReadOnlyList<string> Related { get; init; } = Array.Empty<string>();

    /// <summary>For a network connect / disconnect: the resolved id of the container named in the call.</summary>
    public string? RelatedContainerId { get; init; }

    /// <summary>The resolver found the target in the inventory and <see cref="Id"/> is canonical. False for a
    /// target that is created by the operation or whose kind is resolved by its own service.</summary>
    public bool Resolved { get; init; }
}

/// <summary>Everything the gateway needs to decide on and record one mutation.</summary>
/// <param name="OperationId">Idempotency and correlation key (INV-39); the gateway generates one when null.</param>
/// <param name="SessionId">Agent session or UI circuit the call belongs to, if any. Together with
/// <paramref name="OperationId"/> it identifies an approval the caller raised for this operation itself.</param>
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
    Exception? Error = null,
    string? ApprovalId = null)
{
    public bool Succeeded => Status == MutationStatus.Executed;

    /// <summary>The policy rules that produced a refusal or an approval request, if the policy step decided it.</summary>
    public IReadOnlyList<string> MatchedRules { get; init; } = [];

    /// <summary>A stable code for a refusal the UI shows in the user's language (<see cref="MutationReasonCodes"/>);
    /// <see cref="Reason"/> stays the English text for logs and the audit.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>The values the localized text of <see cref="ReasonCode"/> fills in, in order.</summary>
    public IReadOnlyList<string> ReasonArgs { get; init; } = [];
}

/// <summary>Refusal codes with a localized text (<c>Mutation_Refused_&lt;code&gt;</c> in the shared resources).</summary>
public static class MutationReasonCodes
{
    /// <summary>{0} needs {1}, which is switched off in Settings → MCP.</summary>
    public const string UnderlyingCapabilityDisabled = "UnderlyingCapabilityDisabled";

    /// <summary>The operation's requirement could not be determined.</summary>
    public const string RequirementUnresolved = "RequirementUnresolved";

    /// <summary>{0} does not exist (R1-06).</summary>
    public const string TargetUnknown = "TargetUnknown";

    /// <summary>{0} fits more than one target (R1-06).</summary>
    public const string TargetAmbiguous = "TargetAmbiguous";

    /// <summary>The target could not be resolved; {0} is the detail (R1-06).</summary>
    public const string TargetNotResolved = "TargetNotResolved";

    public static readonly IReadOnlyList<string> All =
        [UnderlyingCapabilityDisabled, RequirementUnresolved, TargetUnknown, TargetAmbiguous, TargetNotResolved];

    /// <summary>The resource key of <paramref name="code"/>.</summary>
    public static string KeyOf(string code) => "Mutation_Refused_" + code;
}

/// <summary>Handed to the execution delegate: the decided request (with its resolved target) and its capability.</summary>
public sealed record MutationContext(string OperationId, MutationRequest Request, Capability Capability);
