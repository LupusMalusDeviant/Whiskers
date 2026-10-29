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

    /// <summary>Entries of <see cref="Related"/> that are decided on but never recorded as they are (R1-16): a
    /// mount whose source came from an <c>.env</c> value, or every mount a caller listed.</summary>
    public IReadOnlySet<string> Withheld { get; init; } = System.Collections.Frozen.FrozenSet<string>.Empty;

    /// <summary>For a network connect / disconnect: the resolved id of the container named in the call.</summary>
    public string? RelatedContainerId { get; init; }

    /// <summary>The other resources the operation also touches (R1-13), one entry per resource with its aliases (a
    /// container's id and name) as the resolver found them in the inventory. Never the target itself: its own
    /// aliases are <see cref="Aliases"/>. Set by the resolver for every <see cref="Resolved"/> target.</summary>
    public IReadOnlyList<IReadOnlyList<string>> RelatedResources { get; init; } = Array.Empty<IReadOnlyList<string>>();

    /// <summary>Further names of the target itself from the inventory (R1-13): a workload's pod name, a Git app's
    /// stored name and repository. Never a name the caller supplied.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

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
    string? SessionId = null)
{
    /// <summary>The preset a run is pinned to (INV-23, an AI trigger's run). Null: the live active preset governs.</summary>
    public Whiskers.Models.Agent.PinnedPolicy? PinnedPolicy { get; init; }

    /// <summary>Set by the gateway once the policy decision is recorded (INV-22); the audit names it.</summary>
    public string? DecisionId { get; init; }

    /// <summary>The AI trigger the mutation is made for (R1-13): the trigger itself, an agent on its behalf, or a
    /// mutation nested in one of theirs. Set by the gateway; the audit keeps <c>trigger:&lt;id&gt;</c> with it.</summary>
    public string? TriggerId { get; init; }

    /// <summary>Where an agent mutation came from (R1-15), set by Whiskers from the agent session, never from the
    /// caller: an approval the gateway raises for it is interactive only for the web chat. Null: not interactive.</summary>
    public Whiskers.Models.Agent.AgentOrigin? AgentOrigin { get; init; }
}

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

    /// <summary>The recorded policy decision (INV-22), when the call got as far as the policy step.</summary>
    public string? DecisionId { get; init; }
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

    /// <summary>The policy decision could not be recorded (INV-22, R1-12).</summary>
    public const string DecisionNotRecorded = "DecisionNotRecorded";

    /// <summary>An AI trigger may not do this: {0} is why — no grant for the capability or target, the trigger is off,
    /// or its owner lost the authority (PC-D6, PC-D7, R1-13).</summary>
    public const string TriggerNotAllowed = "TriggerNotAllowed";

    /// <summary>An unattended run needs a confirmation nobody can give: {0} is the rule's reason (INV-04, PC-D7).</summary>
    public const string UnattendedConfirm = "UnattendedConfirm";

    /// <summary>The action touches Whiskers' control plane or a protected resource: an Admin must approve it; {0} is
    /// the approval's id (INV-59, R1-16).</summary>
    public const string ProtectedNeedsAdminApproval = "ProtectedNeedsAdminApproval";

    public static readonly IReadOnlyList<string> All =
        [UnderlyingCapabilityDisabled, RequirementUnresolved, TargetUnknown, TargetAmbiguous, TargetNotResolved,
            DecisionNotRecorded, TriggerNotAllowed, UnattendedConfirm, ProtectedNeedsAdminApproval];

    /// <summary>The resource key of <paramref name="code"/>.</summary>
    public static string KeyOf(string code) => "Mutation_Refused_" + code;
}

/// <summary>Handed to the execution delegate: the decided request (with its resolved target) and its capability.</summary>
public sealed record MutationContext(string OperationId, MutationRequest Request, Capability Capability);
