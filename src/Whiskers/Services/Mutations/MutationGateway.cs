using System.Diagnostics;
using Whiskers.Models;
using Whiskers.Services.Approvals;

namespace Whiskers.Services.Mutations;

/// <summary>
/// The one path to an infrastructure mutation (ADR-0005). Entry points describe the mutation; the gateway authorizes
/// it against the capability catalog, asks the policy, records the intent, runs the delegate inside a
/// <see cref="MutationScope"/> under the capability's deadline, verifies the result and records the outcome.
/// </summary>
public interface IMutationGateway
{
    Task<MutationOutcome<T>> ExecuteAsync<T>(
        MutationRequest request,
        Func<MutationContext, CancellationToken, Task<T>> execute,
        CancellationToken ct = default);

    /// <summary>
    /// Steps 1–5 without executing (R1-12): authorizes, resolves the target, asks the policy and records the decision
    /// (INV-22); a refusal is audited as in <see cref="ExecuteAsync{T}"/>. An agent session uses it before it pauses
    /// for an approval or reports a block, so the approval names a recorded decision and a block is on record. Null
    /// from a gateway that cannot decide on its own (test doubles).
    /// </summary>
    Task<MutationDecisionResult?> DecideAsync(MutationRequest request, CancellationToken ct = default) =>
        Task.FromResult<MutationDecisionResult?>(null);

    /// <summary>
    /// Records a refusal the surface made before asking the gateway (R1-21) — a page whose role check stops the user
    /// in front of its confirmation dialog — as the gateway's own Denied event for that capability, so the attempt is
    /// on record exactly once. Nothing runs.
    /// </summary>
    Task<MutationOutcome<object?>> RefuseAsync(MutationRequest request, string reason, CancellationToken ct = default) =>
        Task.FromResult(new MutationOutcome<object?>(request.OperationId ?? "", MutationStatus.Denied, null, reason,
            VerificationStatus.NotRun));
}

/// <summary>What <see cref="IMutationGateway.DecideAsync"/> decided. <see cref="Refused"/> is set when the call was
/// refused (by the policy or an earlier step); <see cref="DecisionId"/> when the policy step recorded a decision.</summary>
public sealed record MutationDecisionResult(
    string OperationId,
    MutationVerdict Verdict,
    string Reason,
    IReadOnlyList<string> MatchedRules,
    string? DecisionId,
    string? Preset,
    MutationStatus? Refused)
{
    /// <summary>What an approval for this call must be bound to (R1-14, <see cref="ApprovalBinding"/>); set for a
    /// call the policy decided.</summary>
    public string? Binding { get; init; }

    /// <summary>Why a refused call was refused, as a <see cref="MutationReasonCodes"/> code; <c>null</c> otherwise.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>The target as the gateway resolved it from the inventory (R1-15): what an approval notification may
    /// name, unlike a value read from the arguments.</summary>
    public MutationTarget? Target { get; init; }
}

/// <summary>Step 7 of the pipeline: checks that a capability actually took effect (INV-43). A verifier reads the
/// effect back; it throws <see cref="MutationVerificationFailedException"/> with what it found when the effect is not
/// there. Any other exception also counts as failed: a read-back that cannot be made proves nothing.</summary>
public interface IMutationVerifier
{
    string CapabilityId { get; }

    Task<VerificationStatus> VerifyAsync(MutationContext context, object? result, CancellationToken ct);
}

/// <summary>A call completed, but its effect is not there (R1-36). The outcome is <see cref="MutationStatus.Failed"/>
/// with <see cref="VerificationStatus.Failed"/>; this exception is its <see cref="MutationOutcome{T}.Error"/>.
/// <see cref="StateUnknown"/> says the read-back could not be made at all (the effect is unproven, not disproven): a
/// caller about to act on the failure — undo it — must not treat that as a bad state.</summary>
public sealed class MutationVerificationFailedException(string message, bool stateUnknown = false)
    : InvalidOperationException(message)
{
    public bool StateUnknown { get; } = stateUnknown;
}

/// <summary>
/// Since WP2 step 2.1 every MCP tool call and every agent tool call that maps to a capability runs through here
/// (<c>McpGuardrailFilter</c>, <c>AgentToolInvoker</c>). UI handlers, jobs, webhooks and terminals follow in steps
/// 2.2–2.5. A <c>Confirm</c> verdict goes to <see cref="IApprovalGate"/>: the mutation runs only with an approval a
/// human gave for exactly this call (its <see cref="ApprovalBinding"/>, R1-14); otherwise the outcome is
/// <see cref="MutationStatus.PendingApproval"/>. Approvals and their single use are persisted.
/// </summary>
public sealed class MutationGateway : IMutationGateway
{
    private readonly ICapabilityCatalog _catalog;
    private readonly IMutationPolicy _policy;
    private readonly IMutationAuditSink _audit;
    private readonly IReadOnlyDictionary<string, IMutationVerifier> _verifiers;
    private readonly ILogger<MutationGateway> _logger;
    private readonly TimeProvider _time;
    private readonly ICapabilityOverrides? _overrides;
    private readonly IMutationTargetResolver? _targets;
    private readonly IApprovalGate? _approvals;
    private readonly IEffectiveRequirementResolver? _requirements;
    private readonly IPolicyDecisionStore? _decisions;
    private readonly ITriggerAuthority? _triggers;
    private readonly IOperationJournal? _journal;
    private readonly IAuditRecovery? _recovery;
    // Admission locks by approval binding (striped, so they never grow): choosing the operation id of an
    // approve-then-retry and the approval gate's answer happen under one of them (R1-18).
    private readonly SemaphoreSlim[] _admission = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <param name="overrides">Tool switches and stricter stored levels (INV-15); none = catalog levels only.</param>
    /// <param name="targets">Target resolution (INV-57); none = targets are taken as given.</param>
    /// <param name="approvals">Where a <c>Confirm</c> verdict is decided; none = every <c>Confirm</c> waits.</param>
    /// <param name="decisions">Where every policy decision is recorded before it is acted on (INV-22). The app always
    /// registers one; a gateway built without it (unit tests of other steps) records nothing.</param>
    /// <param name="triggers">What an AI trigger may change (R1-13). Without it every mutation a trigger asks for is
    /// refused; the Agent module, which runs triggers, registers it.</param>
    /// <param name="journal">Execution claims by operation id (R1-18, INV-39). The app always registers it; a gateway
    /// built without it (unit tests of other steps) does not suppress duplicates.</param>
    /// <param name="recovery">Keeps an outcome or refusal whose audit write failed and writes it again (R1-19); the app
    /// always registers it.</param>
    public MutationGateway(
        ICapabilityCatalog catalog,
        IMutationPolicy policy,
        IMutationAuditSink audit,
        IEnumerable<IMutationVerifier> verifiers,
        ILogger<MutationGateway> logger,
        TimeProvider? time = null,
        ICapabilityOverrides? overrides = null,
        IMutationTargetResolver? targets = null,
        IApprovalGate? approvals = null,
        IEffectiveRequirementResolver? requirements = null,
        IPolicyDecisionStore? decisions = null,
        ITriggerAuthority? triggers = null,
        IOperationJournal? journal = null,
        IAuditRecovery? recovery = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _verifiers = (verifiers ?? throw new ArgumentNullException(nameof(verifiers)))
            .ToDictionary(v => v.CapabilityId, StringComparer.Ordinal);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _overrides = overrides;
        _targets = targets;
        _approvals = approvals;
        _requirements = requirements;
        _decisions = decisions;
        _triggers = triggers;
        _journal = journal;
        _recovery = recovery;
    }

    /// <inheritdoc />
    public async Task<MutationOutcome<object?>> RefuseAsync(MutationRequest request, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var operationId = string.IsNullOrWhiteSpace(request.OperationId) ? Guid.NewGuid().ToString("N") : request.OperationId;
        request = request with { OperationId = operationId };
        if (MutationScope.Current is { } enclosing)
            request = request with { ParentOperationId = enclosing.OperationId };
        var capability = _catalog.Find(request.CapabilityId);
        // The refusal names the same canonical target as the action's other outcomes would (INV-57); when the target
        // cannot be resolved the refusal stands with the target as named.
        if (_targets is not null && capability is not null)
        {
            try
            {
                if ((await _targets.ResolveAsync(request, capability, ct)).Target is { } resolved)
                    request = request with { Target = resolved };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The type only: a resolver's message echoes the raw target from the arguments.
                _logger.LogWarning("Target of refused {Capability} could not be resolved ({Error}, operation {OperationId})",
                    capability.Id, ex.GetType().Name, operationId);
            }
        }
        return await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied, reason, ct);
    }

    /// <inheritdoc />
    public async Task<MutationDecisionResult?> DecideAsync(MutationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Principal);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CapabilityId);

        var prepared = await PrepareAsync(request, ct);
        prepared.Admission?.Dispose();
        if (prepared.Refusal is { } refusal)
            return new MutationDecisionResult(refusal.OperationId, MutationVerdict.Deny, refusal.Reason ?? "Refused.",
                refusal.MatchedRules, refusal.DecisionId, prepared.Preset, refusal.Status) { ReasonCode = refusal.ReasonCode };
        var decision = prepared.Decision!;
        return new MutationDecisionResult(prepared.Request!.OperationId!, decision.Verdict, decision.Reason,
            decision.MatchedRules, prepared.Request.DecisionId, decision.Policy?.Preset, null)
        {
            Binding = ApprovalBinding.Of(prepared.Request, prepared.Capability!, decision.Policy?.VersionHash),
            Target = prepared.Request.Target,
        };
    }

    /// <summary>Steps 1–5: capability, tool settings, effective requirement, level, target, policy and the decision
    /// record. A refusal on the way is audited and returned as <see cref="Prepared.Refusal"/>; a policy Deny too.</summary>
    private async Task<Prepared> PrepareAsync(MutationRequest request, CancellationToken ct)
    {
        // Held from choosing the operation id until the approval gate has answered (released by the caller): two
        // identical calls without an id of their own then never pick different ids for one approval (R1-18).
        var admission = new AdmissionLease();
        try
        {
            var prepared = await PrepareCoreAsync(request, admission, ct);
            if (prepared.Refusal is not null) admission.Dispose();
            return prepared with { Admission = admission };
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }

    private async Task<Prepared> PrepareCoreAsync(MutationRequest request, AdmissionLease admission, CancellationToken ct)
    {
        // A call without an operation id of its own gets a fresh one — or, retrying an approval, the one that raised it.
        var callerOperationId = string.IsNullOrWhiteSpace(request.OperationId) ? null : request.OperationId;
        var operationId = callerOperationId ?? Guid.NewGuid().ToString("N");
        request = request with { OperationId = operationId };
        // R1-18: a mutation nested in another one's execution keeps the link to it; its own id stays its own.
        if (MutationScope.Current is { } enclosing)
            request = request with { ParentOperationId = enclosing.OperationId };
        // INV-23: a mutation nested in a pinned run's mutation (a scheduled task an AI trigger runs) is decided
        // against the run's preset too; the global kill switch still reaches it (EffectivePolicy).
        if (request.PinnedPolicy is null && MutationScope.Current?.PinnedPolicy is { } inherited)
            request = request with { PinnedPolicy = inherited };
        // R1-13: the trigger the mutation is made for, directly or because it is nested in a trigger's mutation.
        if ((TriggerAuthority.TriggerOf(request.Principal) ?? MutationScope.Current?.TriggerId) is { } triggerId)
            request = request with { TriggerId = triggerId };

        // 1–2: the capability must exist, be switched on, and the principal must hold its level. Unknown means Deny.
        var capability = _catalog.Find(request.CapabilityId);
        if (capability is null)
            return Prepared.Refused(await RefuseAsync<object?>(operationId, request, null, MutationStatus.Denied,
                $"Unknown capability '{request.CapabilityId}'.", ct));

        if (_overrides is not null)
        {
            CapabilityAvailability availability;
            try
            {
                availability = _overrides.Resolve(capability);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Capability settings for {Capability} could not be read (operation {OperationId})",
                    capability.Id, operationId);
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                    "The capability settings could not be read; the mutation was refused.", ct));
            }
            if (!availability.Enabled)
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                    $"'{capability.McpTool ?? capability.Id}' is disabled in Settings → MCP.", ct));
            capability = capability with { RequiredLevel = availability.RequiredLevel };
        }

        // R1-05: what the arguments make of the operation (a scheduled CustomCommand is host.exec). Only ever stricter,
        // and set before the caller check and the approval, so neither is held to the outer tool's level.
        if (_requirements is not null)
        {
            try
            {
                var effective = await _requirements.ResolveAsync(capability, request.Arguments, ct);
                capability = capability with
                {
                    RequiredLevel = SchedulerRequirementResolver.Stricter(capability.RequiredLevel, effective),
                };
            }
            catch (CapabilityDisabledException disabled)
            {
                var outer = capability.McpTool ?? capability.Id;
                var underlying = disabled.Capability.McpTool ?? disabled.Capability.Id;
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                    $"'{outer}' needs '{underlying}', which is disabled in Settings → MCP.", ct)
                    with { ReasonCode = MutationReasonCodes.UnderlyingCapabilityDisabled, ReasonArgs = [outer, underlying] });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The requirement of {Capability} could not be resolved (operation {OperationId})",
                    capability.Id, operationId);
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                    "The operation's requirement could not be determined; the mutation was refused.", ct)
                    with { ReasonCode = MutationReasonCodes.RequirementUnresolved });
            }
        }

        if (!McpPermissionLevels.HasAccess(McpPermissionLevels.Normalize(request.Principal.PermissionLevel), capability.RequiredLevel))
            return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                $"'{capability.Id}' requires '{capability.RequiredLevel}'; {request.Principal.DisplayName} has " +
                $"'{McpPermissionLevels.Normalize(request.Principal.PermissionLevel)}'.", ct));

        // The target must resolve before policy sees it (INV-57); an error here is a Deny.
        if (_targets is not null)
        {
            TargetResolution resolution;
            try
            {
                resolution = await _targets.ResolveAsync(request, capability, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Target of {Capability} could not be resolved (operation {OperationId})",
                    capability.Id, operationId);
                // The detail (possibly a runtime's endpoint or path) stays in the log, not in the refusal or audit.
                resolution = new TargetResolution(null, "The target could not be resolved (see the log).",
                    MutationReasonCodes.TargetNotResolved, ["see the log"]);
            }
            if (resolution.Target is null)
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                    resolution.Error ?? "The target could not be resolved.", ct)
                    with
                    {
                        ReasonCode = resolution.Code ?? MutationReasonCodes.TargetNotResolved,
                        ReasonArgs = resolution.Args ?? [resolution.Error ?? ""],
                    });
            request = request with { Target = resolution.Target };
        }

        // PC-D6 / PC-D7 (R1-13): an AI trigger writes only what an Admin granted it (capability and every resource it
        // touches) and only while the owner of that grant still holds the authority, re-read for this mutation. That
        // holds for a mutation nested in the trigger's as well (a scheduled task it runs): the nested principal's own
        // authority is checked too, but it cannot carry the trigger past its grant. No answer is a Deny.
        if (request.TriggerId is { } grantTrigger)
        {
            string? refusal;
            try
            {
                refusal = _triggers is null
                    ? "AI trigger grants cannot be checked (the Agent module is off)"
                    : await _triggers.RefusalAsync(grantTrigger, capability, request.Target, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "AI trigger grant for {Capability} could not be checked (operation {OperationId})",
                    capability.Id, operationId);
                refusal = "the AI trigger's grant could not be checked";
            }
            if (refusal is not null)
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                    $"Refused for the AI trigger: {refusal}", ct)
                    with { ReasonCode = MutationReasonCodes.TriggerNotAllowed, ReasonArgs = [refusal] });
        }

        // 3: policy. Any exception here is a Deny, never an Allow.
        MutationPolicyDecision decision;
        try
        {
            decision = _policy.Evaluate(request, capability);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Policy evaluation failed for {Capability} (operation {OperationId})", capability.Id, operationId);
            decision = new MutationPolicyDecision(MutationVerdict.Deny, "Policy evaluation failed; the mutation was refused.", []);
        }

        // R1-18 (INV-39): an approve-then-retry without an operation id of the caller's own continues the operation
        // that raised the approval, so its decision, approval, run, verification and audit carry one id. Only an open
        // approval (pending, or approved and unused) bound to exactly this call names it.
        if (callerOperationId is null && decision.Verdict == MutationVerdict.Confirm && _approvals is not null
            && !Unattended(request)
            && ApprovalBinding.Of(request, capability, decision.Policy?.VersionHash) is { } binding)
        {
            await admission.EnterAsync(_admission[(uint)StringComparer.Ordinal.GetHashCode(binding) % (uint)_admission.Length], ct);
            string? raisedBy = null;
            try
            {
                raisedBy = _approvals.OpenOperationOf(binding);
            }
            catch (Exception ex)
            {
                // The call keeps its own id; the approval gate decides as before.
                _logger.LogWarning(ex, "Open approval for {Capability} could not be looked up (operation {OperationId})",
                    capability.Id, operationId);
            }
            if (!string.IsNullOrWhiteSpace(raisedBy))
            {
                operationId = raisedBy;
                request = request with { OperationId = operationId };
            }
        }

        // INV-22: the decision is on record before anything acts on it — a refusal, an approval request or the
        // mutation. No record, no mutation: a failed write is a Deny.
        if (_decisions is not null)
        {
            var decisionId = Guid.NewGuid().ToString("N");
            try
            {
                await _decisions.RecordAsync(new PolicyDecisionRecord(decisionId, _time.GetUtcNow().UtcDateTime,
                    operationId, request, capability, decision,
                    decision.Policy ?? PolicySnapshot.Of(NoPolicyPreset, Whiskers.Configuration.GuardrailPolicy.FailClosed())), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Policy decision for {Capability} could not be recorded; operation {OperationId} refused",
                    capability.Id, operationId);
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                    "The policy decision could not be recorded; the mutation was refused.", ct)
                    with { ReasonCode = MutationReasonCodes.DecisionNotRecorded });
            }
            request = request with { DecisionId = decisionId };
        }

        if (decision.Verdict == MutationVerdict.Deny)
            return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                $"Blocked by guardrails: {decision.Reason}", ct) with { MatchedRules = decision.MatchedRules },
                decision.Policy?.Preset);

        // An unattended run (scheduler, AutoUpdate, AutoPatch, an AI trigger and whatever it sets off) has nobody who
        // could confirm it, and the next occurrence of the same item is a new run, not a retry: it must never reach the
        // approval gate, which would raise an approval and later match an identical occurrence against the grant.
        // Terminal refusal, audited, also when only asked for the decision (an agent session's check).
        if (decision.Verdict == MutationVerdict.Confirm && Unattended(request))
            return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                $"Unattended run refused: it needs a confirmation nobody can give ({decision.Reason}).", ct)
                with
                {
                    MatchedRules = decision.MatchedRules,
                    ReasonCode = MutationReasonCodes.UnattendedConfirm,
                    ReasonArgs = [decision.Reason],
                }, decision.Policy?.Preset);

        // R1-18 (INV-39): an operation that ran, runs, was cut off or names other inputs is refused as such before the
        // approval gate — for the agent's decision too — so it never raises (or uses) an approval. The claim right
        // before the run still decides.
        if (decision.Verdict == MutationVerdict.Confirm && _journal is not null)
        {
            (string Reason, string Code)? known;
            try
            {
                known = await _journal.InspectAsync(operationId, DbOperationJournal.InputHashOf(request), ct) is { } state
                    ? RefusalOf(state, operationId)
                    : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Operation {OperationId} ({Capability}) could not be looked up; refused", operationId, capability.Id);
                known = ("The operation could not be registered; nothing ran.", MutationReasonCodes.OperationNotRegistered);
            }
            if (known is { } knownRefusal)
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                    knownRefusal.Reason, ct)
                    with { MatchedRules = decision.MatchedRules, ReasonCode = knownRefusal.Code, ReasonArgs = [operationId] },
                    decision.Policy?.Preset);
        }

        return new Prepared(null, request, capability, decision, callerOperationId, decision.Policy?.Preset);
    }

    public async Task<MutationOutcome<T>> ExecuteAsync<T>(
        MutationRequest request,
        Func<MutationContext, CancellationToken, Task<T>> execute,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Principal);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CapabilityId);
        ArgumentNullException.ThrowIfNull(execute);

        var prepared = await PrepareAsync(request, ct);
        if (prepared.Refusal is { } refusal) return Cast<T>(refusal);
        request = prepared.Request!;
        var capability = prepared.Capability!;
        var decision = prepared.Decision!;
        var operationId = request.OperationId!;

        // 4: Confirm runs only with a human approval for exactly this call.
        ApprovalGateResult? approval = null;
        // An unattended Confirm never gets here: PrepareAsync refuses it.
        using (prepared.Admission)
        if (decision.Verdict == MutationVerdict.Confirm)
        {
            if (_approvals is null)
                return await RefuseAsync<T>(operationId, request, capability, MutationStatus.PendingApproval, decision.Reason, ct)
                    with { MatchedRules = decision.MatchedRules };
            try
            {
                approval = await _approvals.CheckAsync(ApprovalSubjectOf(request, capability, operationId, decision.Policy, decision.MatchedRules),
                    decision.Reason, decision.MatchedRules, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Approval check for {Capability} failed (operation {OperationId})", capability.Id, operationId);
                return await RefuseAsync<T>(operationId, request, capability, MutationStatus.Denied,
                    "The approval could not be checked; the mutation was refused.", ct);
            }
            if (!approval.Approved)
            {
                var pending = await RefuseAsync<T>(operationId, request, capability, MutationStatus.PendingApproval,
                    approval.Message, ct, approval.ApprovalId) with { MatchedRules = decision.MatchedRules };
                // INV-59 (R1-16): say in the person's language that an Admin must approve it on the Approvals page.
                return decision.MatchedRules.Contains(Whiskers.Services.Guardrails.ProtectedResourceRule.RuleId)
                    ? pending with
                    {
                        ReasonCode = MutationReasonCodes.ProtectedNeedsAdminApproval,
                        ReasonArgs = [approval.ApprovalId ?? "?"],
                    }
                    : pending;
            }
        }

        // 5: no intent record, no mutation (PC-D5).
        long auditId;
        try
        {
            auditId = await _audit.BeginAsync(operationId, request, capability, approval, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit intent for {Capability} could not be written; operation {OperationId} refused",
                capability.Id, operationId);
            return new MutationOutcome<T>(operationId, MutationStatus.Denied, default,
                "The audit record could not be written; the mutation was refused.", VerificationStatus.NotRun, ex)
                { DecisionId = request.DecisionId };
        }

        // R1-18 (INV-39): the operation id is claimed before anything runs, so the same id never runs twice — not as a
        // concurrent retry, not after it ran, not after a restart cut it off (its outcome is then unknown and stays
        // so). Claimed before the approval is used: a duplicate does not use up an approval it cannot run on.
        string? claim = null;
        (string Reason, string Code)? duplicate = null;
        if (_journal is not null)
            (claim, duplicate) = await ClaimAsync(operationId, request, capability, approval, auditId);
        if (duplicate is not null)
        {
            await CompleteAuditAsync(new PendingAuditOutcome(auditId, operationId, MutationStatus.Denied,
                VerificationStatus.NotRun, duplicate!.Value.Reason, TimeSpan.Zero), capability);
            return new MutationOutcome<T>(operationId, MutationStatus.Denied, default, duplicate!.Value.Reason,
                VerificationStatus.NotRun)
            {
                DecisionId = request.DecisionId,
                ReasonCode = duplicate.Value.Code,
                ReasonArgs = [operationId],
            };
        }

        // The approval is used only now, after the intent is on record: a refusal before this point leaves it valid.
        // Losing the race against a concurrent identical call (or the approval lapsing meanwhile) turns this attempt's
        // intent row into a refusal.
        if (approval?.ApprovalId is { } approvalId && !await TryConsumeAsync(approvalId, operationId))
        {
            // Nothing ran: the operation id is free again.
            await ReleaseAsync(operationId, claim, capability);
            var reason = $"Approval {approvalId} is no longer usable (used by another call, revoked or expired); the mutation was refused.";
            await CompleteAuditAsync(new PendingAuditOutcome(auditId, operationId, MutationStatus.Denied,
                VerificationStatus.NotRun, reason, TimeSpan.Zero), capability);
            return new MutationOutcome<T>(operationId, MutationStatus.Denied, default, reason, VerificationStatus.NotRun,
                ApprovalId: approvalId) { MatchedRules = decision.MatchedRules, DecisionId = request.DecisionId };
        }

        // 6–7: execute inside the scope and under the deadline, then verify.
        var context = new MutationContext(operationId, request, capability);
        var started = _time.GetTimestamp();
        MutationOutcome<T> outcome;
        IReadOnlyList<MutationDomainResult> results = [];
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(capability.Deadline);
            try
            {
                T value;
                string? reported;
                using (var scope = MutationScope.Begin(capability.Id, operationId, context.Request.Principal, context.Request))
                {
                    try
                    {
                        value = await execute(context, deadline.Token);
                    }
                    finally
                    {
                        // What the tool reported belongs to the event also when it then threw.
                        results = scope.Results;
                    }
                    reported = scope.FailureReason;
                }

                if (reported is not null)
                {
                    // The delegate returned but reported a failure (MutationScope.Fail): Failed, with its value kept so
                    // the caller still gets the tool's own answer.
                    outcome = new MutationOutcome<T>(operationId, MutationStatus.Failed, value, reported,
                        VerificationStatus.NotRun, ApprovalId: approval?.ApprovalId);
                }
                else
                {
                    var (verification, problem, unknown) = await VerifyAsync(context, value, deadline.Token);
                    // R1-36 (INV-43/44): the call completed, but its effect is not there — never a success. The status
                    // says Failed, the verification says the call itself ran; the value is withheld so no caller can
                    // read it as the effect's result.
                    outcome = verification == VerificationStatus.Failed
                        ? new MutationOutcome<T>(operationId, MutationStatus.Failed, default,
                            $"'{capability.Id}' ran, but its effect could not be verified: {problem}", verification,
                            new MutationVerificationFailedException(
                                $"'{capability.Id}' ran, but its effect could not be verified: {problem}", unknown),
                            approval?.ApprovalId)
                        : new MutationOutcome<T>(operationId, MutationStatus.Executed, value, null, verification,
                            ApprovalId: approval?.ApprovalId);
                }
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                outcome = new MutationOutcome<T>(operationId, MutationStatus.Failed, default,
                    $"'{capability.Id}' exceeded its deadline of {capability.Deadline}.", VerificationStatus.NotRun, ex,
                    approval?.ApprovalId);
            }
            catch (Exception ex)
            {
                outcome = new MutationOutcome<T>(operationId, MutationStatus.Failed, default, ex.Message,
                    VerificationStatus.NotRun, ex, approval?.ApprovalId);
            }
        }

        // The claim ends with the outcome. A failed write leaves it Running: after a restart a retry is refused as
        // "outcome unknown", never run again.
        if (_journal is not null && claim is not null)
        {
            try
            {
                await _journal.FinishAsync(operationId, claim, outcome.Status, outcome.Verification, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Operation journal for {Capability} (operation {OperationId}) could not be finished",
                    capability.Id, operationId);
            }
        }

        // 8: the outcome is recorded even when the caller has gone away (CancellationToken.None). The mutation has
        // already happened at this point, so a failed write must not hide its result from the caller; the intent
        // row stays as evidence that it ran, and the outcome is kept and written again (R1-19).
        await CompleteAuditAsync(new PendingAuditOutcome(auditId, operationId, outcome.Status, outcome.Verification,
            outcome.Reason, _time.GetElapsedTime(started), results), capability);
        return outcome with { DecisionId = request.DecisionId };
    }

    /// <summary>Writes the outcome to its intent row. A failed write is handed to <see cref="IAuditRecovery"/>: the
    /// event is incomplete — visibly, in the <c>audit</c> health check — until it is written again.</summary>
    private async Task CompleteAuditAsync(PendingAuditOutcome outcome, Capability capability)
    {
        try
        {
            await _audit.CompleteAsync(outcome.AuditId, outcome.OperationId, outcome.Status, outcome.Verification,
                outcome.Reason, outcome.Duration, outcome.Results, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit outcome for {Capability} (operation {OperationId}, status {Status}) could not be written",
                capability.Id, outcome.OperationId, outcome.Status);
            _recovery?.OutcomeNotWritten(outcome);
        }
    }

    private async Task<(VerificationStatus Status, string? Problem, bool Unknown)> VerifyAsync(MutationContext context,
        object? result, CancellationToken ct)
    {
        if (!_verifiers.TryGetValue(context.Capability.Id, out var verifier))
            return (VerificationStatus.NotVerifiable, null, false);
        try
        {
            var status = await verifier.VerifyAsync(context, result, ct);
            return (status, status == VerificationStatus.Failed ? "the read-back did not show it." : null, false);
        }
        catch (MutationVerificationFailedException failed)
        {
            return (VerificationStatus.Failed, failed.Message, failed.StateUnknown);
        }
        catch (Exception ex)
        {
            // A read-back that cannot be made proves nothing: failed, not verified (INV-43).
            _logger.LogWarning(ex, "Verifier for {Capability} failed (operation {OperationId})",
                context.Capability.Id, context.OperationId);
            return (VerificationStatus.Failed, $"the read-back failed ({ex.GetType().Name}).", true);
        }
    }

    /// <summary>The approval subject carries the MCP tool name when there is one: that is the name on the approval card
    /// and in the agent's own approval.</summary>
    /// <summary>Uses the approval; a failure to record the use is a refusal, never a run.</summary>
    /// <summary>Claims <paramref name="operationId"/> for this call: the claim's token when it may run, else why not. A
    /// journal that cannot be written refuses the call (nothing ran).</summary>
    private async Task<(string? Token, (string Reason, string Code)? Refusal)> ClaimAsync(string operationId,
        MutationRequest request, Capability capability, ApprovalGateResult? approval, long auditId)
    {
        OperationClaim claim;
        try
        {
            claim = await _journal!.ClaimAsync(new OperationClaimRequest(operationId, request.ParentOperationId,
                capability.Id, request.Principal.Id, DbOperationJournal.InputHashOf(request), request.DecisionId,
                approval?.ApprovalId, auditId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operation {OperationId} ({Capability}) could not be claimed; refused", operationId, capability.Id);
            return (null, ("The operation could not be registered; nothing ran.", MutationReasonCodes.OperationNotRegistered));
        }
        return claim is { Result: OperationClaimResult.Claimed, Token: { } token }
            ? (token, null)
            : (null, RefusalOf(claim.Result, operationId));
    }

    /// <summary>Why an operation id that is taken does not run: the refusal text and its reason code.</summary>
    private static (string Reason, string Code) RefusalOf(OperationClaimResult result, string operationId) =>
        result switch
        {
            OperationClaimResult.AlreadyRunning => ($"Operation {operationId} is already running; it does not run twice.",
                MutationReasonCodes.OperationRunning),
            OperationClaimResult.AlreadyRun => ($"Operation {operationId} ran already; it does not run again.",
                MutationReasonCodes.OperationAlreadyRun),
            OperationClaimResult.OutcomeUnknown => ($"Operation {operationId} was running when Whiskers stopped; whether it " +
                "took effect is unknown. Check the target, then start it as a new operation.", MutationReasonCodes.OperationOutcomeUnknown),
            _ => ($"Operation id {operationId} was used for another call; it was refused.", MutationReasonCodes.OperationIdReused),
        };

    /// <summary>Gives the claim back after a refusal that came after it: nothing ran, the id may be used again.</summary>
    private async Task ReleaseAsync(string operationId, string? claim, Capability capability)
    {
        if (_journal is null || claim is null) return;
        try
        {
            await _journal.ReleaseAsync(operationId, claim, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Stays Running: a retry is refused (fail closed), never run twice.
            _logger.LogError(ex, "Operation {OperationId} ({Capability}) could not be released", operationId, capability.Id);
        }
    }

    private async Task<bool> TryConsumeAsync(string approvalId, string operationId)
    {
        try
        {
            return await _approvals!.TryConsumeAsync(approvalId, operationId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approval {ApprovalId} could not be used for operation {OperationId}", approvalId, operationId);
            return false;
        }
    }

    private static ApprovalSubject ApprovalSubjectOf(MutationRequest request, Capability capability, string? operationId,
        PolicySnapshot? policy, IReadOnlyList<string>? matchedRules = null)
    {
        var decisionPreset = policy?.Preset;
        var p = request.Principal;
        var actorKey = $"{p.Kind}:{p.Id}:{p.CredentialId}:{p.OnBehalfOf?.Id}";
        var actorType = request.Surface switch
        {
            MutationSurface.Mcp => "mcp-direct",
            MutationSurface.Ui => "web",
            // The same kinds the agent's own approvals use; only the web chat is a human who is present (PC-D4).
            MutationSurface.Agent => request.AgentOrigin switch
            {
                Whiskers.Models.Agent.AgentOrigin.WebUi => "agent-web",
                Whiskers.Models.Agent.AgentOrigin.McpTool => "agent-mcp",
                Whiskers.Models.Agent.AgentOrigin.Trigger => "trigger",
                _ => "agent",
            },
            _ => request.Surface.ToString().ToLowerInvariant(),
        };
        // INV-59 (R1-16): touching a protected resource is the interactive Admin's exception — only an Admin approves it.
        var level = matchedRules?.Contains(Whiskers.Services.Guardrails.ProtectedResourceRule.RuleId) == true
            ? McpPermissionLevels.Admin
            : capability.RequiredLevel;
        return new ApprovalSubject(capability.McpTool ?? capability.Id, level, actorKey,
            p.DisplayName, actorType, request.Arguments, operationId, request.SessionId)
        {
            DecisionId = request.DecisionId,
            Preset = request.DecisionId is null ? null : decisionPreset,
            // R1-14: who, with which credential, what, at which level, with which arguments, on which resolved target,
            // under which policy version.
            Binding = ApprovalBinding.Of(request, capability, policy?.VersionHash),
            // R1-15: the caller and everyone it acts for; none of them decides a non-interactive request.
            Requesters = ApprovalEligibility.RequestersOf(p),
            WithholdPayloads = request.Surface == MutationSurface.Ui,
            // The card shows only a target found in the inventory; the notification only its kind and inventory id.
            TargetServer = ApprovalDisplay.NotifiableTarget(request.Target).Server,
            TargetWorkload = ApprovalDisplay.NotifiableTarget(request.Target).Resource,
            NotifiedTarget = ApprovalDisplay.NotifiedTarget(capability.TargetKind, request.Target),
        };
    }

    private async Task<MutationOutcome<T>> RefuseAsync<T>(string operationId, MutationRequest request,
        Capability? capability, MutationStatus status, string reason, CancellationToken ct, string? approvalId = null)
    {
        Debug.Assert(status is MutationStatus.Denied or MutationStatus.PendingApproval);
        // A rule can quote the argument it matched; a payload (a command, SQL, a file body) can carry credentials.
        // Callers log, notify and store the reason, so it leaves the gateway without payload values.
        reason = AuditLogMutationSink.WithoutPayloadValues(reason, request.Arguments) ?? reason;
        try
        {
            await _audit.RecordDeniedAsync(operationId, request, capability, status, reason, approvalId, ct);
        }
        catch (Exception ex)
        {
            // The mutation is refused either way; losing the refusal record must not turn it into an error. The sink
            // keeps the event in its outbox and AuditRecovery inserts it later (R1-19).
            _logger.LogError(ex, "Audit record for refused {Capability} (operation {OperationId}) could not be written",
                request.CapabilityId, operationId);
        }
        return new MutationOutcome<T>(operationId, status, default, reason, VerificationStatus.NotRun, ApprovalId: approvalId)
        {
            DecisionId = request.DecisionId,
        };
    }

    /// <summary>Nobody is there to confirm: an automation run, or anything made for an AI trigger (R1-13).</summary>
    private static bool Unattended(MutationRequest request) =>
        request.Surface == MutationSurface.Automation || request.TriggerId is not null;

    /// <summary>Result of steps 1–5: a refusal (already audited), or the decided request.</summary>
    private sealed record Prepared(MutationOutcome<object?>? Refusal, MutationRequest? Request, Capability? Capability,
        MutationPolicyDecision? Decision, string? CallerOperationId, string? Preset)
    {
        /// <summary>The admission lock this call holds until the approval gate answered; disposing releases it.</summary>
        public AdmissionLease? Admission { get; init; }

        public static Prepared Refused(MutationOutcome<object?> refusal, string? preset = null) =>
            new(refusal, null, null, null, null, preset);
    }

    /// <summary>One of the gateway's admission locks, held at most once and released at most once.</summary>
    private sealed class AdmissionLease : IDisposable
    {
        private SemaphoreSlim? _held;

        public async Task EnterAsync(SemaphoreSlim stripe, CancellationToken ct)
        {
            await stripe.WaitAsync(ct);
            _held = stripe;
        }

        public void Dispose() => Interlocked.Exchange(ref _held, null)?.Release();
    }

    private static MutationOutcome<T> Cast<T>(MutationOutcome<object?> o) =>
        new(o.OperationId, o.Status, default, o.Reason, o.Verification, o.Error, o.ApprovalId)
        {
            MatchedRules = o.MatchedRules,
            ReasonCode = o.ReasonCode,
            ReasonArgs = o.ReasonArgs,
            DecisionId = o.DecisionId,
        };

    /// <summary>The preset a decision records when no policy could be consulted (no engine, evaluation failed).</summary>
    internal const string NoPolicyPreset = "(no policy)";
}
