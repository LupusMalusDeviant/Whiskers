using System.Diagnostics;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Observability.Correlation;

namespace Whiskers.Services.Mutations;

/// <summary>
/// The one path to an infrastructure mutation (ADR-0005). Entry points describe the mutation; the gateway authorizes
/// it against the capability catalog, asks the policy, records the intent, runs the delegate inside a
/// <see cref="MutationScope"/> under the capability's deadline, verifies the result and records the outcome.
/// </summary>
public interface IMutationGateway
{
    /// <summary>
    /// Runs the whole pipeline for one attempt. A request carrying an <see cref="MutationRequest.EventKey"/> is
    /// rejected with <see cref="ArgumentException"/> before anything is decided or run: a key is honoured only for a
    /// refusal (<see cref="RefuseAsync"/>, INV-45).
    /// </summary>
    Task<MutationOutcome<T>> ExecuteAsync<T>(
        MutationRequest request,
        Func<MutationContext, CancellationToken, Task<T>> execute,
        CancellationToken ct = default);

    /// <summary>
    /// Steps 1–5 without executing (R1-12): authorizes, resolves the target, asks the policy and records the decision
    /// (INV-22); a refusal is audited as in <see cref="ExecuteAsync{T}"/>. An agent session uses it before it pauses
    /// for an approval or reports a block, so the approval names a recorded decision and a block is on record. Null
    /// from a gateway that cannot decide on its own (test doubles). A request carrying an
    /// <see cref="MutationRequest.EventKey"/> is rejected with <see cref="ArgumentException"/> (INV-45).
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

/// <summary>A verifier cannot read this particular call back (R1-37) — it named no subject, or its effect is not
/// observable for this input. The outcome is <see cref="VerificationStatus.NotVerifiable"/> with this reason: recorded,
/// never a success claim.</summary>
public sealed class MutationNotVerifiableException(string reason) : InvalidOperationException(reason);

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
    // A restore is staged (F3): no operation is admitted, and the ones running are counted so the restore waits for
    // them before its safety backup.
    private readonly Whiskers.Services.Maintenance.IMaintenanceStateService? _maintenance;
    // Contract 0023: the shutdown drain a top-level run takes an entry of (point 6), the abandoned runs and their target
    // guard (4b), and the grace period G after the deadline.
    private readonly IShutdownDrain? _drain;
    private readonly IAbandonedRuns? _abandoned;
    private readonly TimeSpan _grace;

    /// <summary>Contract 0023 (4b): the two capabilities the target guard never refuses — the brake must always be
    /// reachable, also while a <c>guardrails.save</c> is abandoned.</summary>
    internal static readonly IReadOnlySet<string> KillSwitchToggles =
        new HashSet<string>(StringComparer.Ordinal) { "guardrails.kill_switch_on", "guardrails.kill_switch_off" };

    /// <summary>Contract 0023 (point 6): the reason of a top-level mutation asked for once Whiskers has begun to stop.</summary>
    public const string ShuttingDownRefusal = "Whiskers is stopping; the change was not made. Retry after the restart.";

    /// <summary>Contract 0023 (4b): the reason of a mutation whose target an abandoned run still holds.</summary>
    public const string TargetBusyRefusal =
        "An earlier run on this target ran past its deadline and is still running; nothing ran. Fix the host, or " +
        "restart Whiskers to clear it.";

    /// <summary>The reason of every mutation refused while a restore is staged. Not written to the audit trail: the
    /// database it would land in is replaced at the restart, after the pre-restore safety backup; it is logged.</summary>
    public const string MaintenanceRefusal =
        "A restore is in progress and Whiskers is about to restart; the change was not made. Retry after the restart.";
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
        IAuditRecovery? recovery = null,
        Whiskers.Services.Maintenance.IMaintenanceStateService? maintenance = null,
        IShutdownDrain? drain = null,
        IAbandonedRuns? abandoned = null,
        Microsoft.Extensions.Options.IOptions<MutationGatewayOptions>? options = null)
    {
        _maintenance = maintenance;
        _drain = drain;
        _abandoned = abandoned;
        _grace = options?.Value.AbandonGrace ?? new MutationGatewayOptions().AbandonGrace;
        if (_grace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "The grace period cannot be negative.");
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
        // The parent link comes from the enclosing scope only; a caller's own is never kept (contract 0023, 4b).
        request = request with { OperationId = operationId, ParentOperationId = MutationScope.Current?.OperationId };
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
            catch (Exception ex)
            {
                // A cancellation too (INV-45): the refusal is recorded with the target as named, never dropped.
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
        RejectEventKey(request);
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
        var progress = new PrepareProgress();
        try
        {
            var prepared = await PrepareCoreAsync(request, admission, progress, ct);
            if (prepared.Refusal is not null) admission.Dispose();
            return prepared with { Admission = admission };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            admission.Dispose();
            // INV-45: the attempt began when the caller asked for the capability, so a cancellation before the decision
            // (the caller went away, a run's deadline passed during target resolution) is still one event: a refusal,
            // recorded regardless of the cancelled token. Every refusal on the way returned instead of throwing, so this
            // is the only event. The caller still sees the cancellation.
            var seen = progress.Request ?? request with
            {
                OperationId = string.IsNullOrWhiteSpace(request.OperationId) ? Guid.NewGuid().ToString("N") : request.OperationId,
            };
            await RefuseAsync<object?>(seen.OperationId!, seen, progress.Capability ?? _catalog.Find(request.CapabilityId),
                MutationStatus.Denied, seen.DecisionId is null ? CancelledBeforeDecision : CancelledBeforeRun,
                CancellationToken.None);
            throw;
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }

    /// <summary>The reason of an attempt cancelled before it was decided (INV-45).</summary>
    internal const string CancelledBeforeDecision = "The call was cancelled before its decision; nothing ran.";

    /// <summary>The reason of an attempt cancelled after its decision but before its intent (INV-45).</summary>
    internal const string CancelledBeforeRun = "The call was cancelled before it ran; nothing ran.";

    private async Task<Prepared> PrepareCoreAsync(MutationRequest request, AdmissionLease admission, PrepareProgress progress,
        CancellationToken ct)
    {
        // A call without an operation id of its own gets a fresh one — or, retrying an approval, the one that raised it.
        var callerOperationId = string.IsNullOrWhiteSpace(request.OperationId) ? null : request.OperationId;
        var operationId = callerOperationId ?? Guid.NewGuid().ToString("N");
        // R1-18: a mutation nested in another one's execution keeps the link to it; its own id stays its own. Contract
        // 0023 (4b): the link comes from the enclosing open scope only — a ParentOperationId the caller supplied is
        // cleared, so no caller can claim to be nested in a run it is not part of.
        request = request with { OperationId = operationId, ParentOperationId = MutationScope.Current?.OperationId };
        // INV-23: a mutation nested in a pinned run's mutation (a scheduled task an AI trigger runs) is decided
        // against the run's preset too; the global kill switch still reaches it (EffectivePolicy).
        if (request.PinnedPolicy is null && MutationScope.Current?.PinnedPolicy is { } inherited)
            request = request with { PinnedPolicy = inherited };
        // R1-13: the trigger the mutation is made for, directly or because it is nested in a trigger's mutation.
        if ((TriggerAuthority.TriggerOf(request.Principal) ?? MutationScope.Current?.TriggerId) is { } triggerId)
            request = request with { TriggerId = triggerId };
        progress.Request = request;

        // 1–2: the capability must exist, be switched on, and the principal must hold its level. Unknown means Deny.
        var capability = _catalog.Find(request.CapabilityId);
        progress.Capability = capability;
        if (capability is null)
            return Prepared.Refused(await RefuseAsync<object?>(operationId, request, null, MutationStatus.Denied,
                $"Unknown capability '{request.CapabilityId}'.", ct));
        // A principal without authority (its owner could not be read) is refused outright, whatever the capability's
        // level or a setting makes of it — not normalised to read (INV-45 review round 13).
        if (McpPermissionLevels.IsNone(request.Principal.PermissionLevel))
            return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                $"'{capability.Id}' was refused: {request.Principal.DisplayName} holds no authority.", ct));

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
        ResolvedTarget? resolvedObject = null;
        var namedTarget = request.Target;
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
            // Contract 0012 (D7): a caller that read the target before (a webhook delivery choosing its lock key) must
            // get the same object; one that resolves differently now is refused before policy.
            if (request.ExpectedTarget is { } expected && !SameObject(expected, resolution.Target))
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request with { Target = resolution.Target },
                    capability, MutationStatus.Denied,
                    $"{expected.Name ?? expected.Id} changed after it was read; nothing ran.", ct)
                    with
                    {
                        ReasonCode = MutationReasonCodes.TargetChanged,
                        ReasonArgs = [expected.Name ?? expected.Id],
                    });
            request = request with { Target = resolution.Target };
            resolvedObject = resolution.Object;
            progress.Request = request;
        }

        // Contract 0023 (4b): while an abandoned run still holds an overlapping target, nothing else acts on it — for
        // every principal, under the emergency stop too, whatever its operation id. Only the run's own nested calls (in
        // its open scope chain, never a ParentOperationId a caller supplied) and the two kill-switch toggles pass.
        if (_abandoned is not null && !KillSwitchToggles.Contains(capability.Id))
        {
            AbandonedRun? busy;
            try
            {
                busy = _abandoned.Overlapping(request.Target, MutationScope.OpenOperationIds());
            }
            catch (Exception ex)
            {
                // Not knowing is refused, like any other step that cannot answer.
                _logger.LogWarning(ex, "Abandoned runs could not be checked for {Capability} (operation {OperationId})",
                    capability.Id, operationId);
                busy = new AbandonedRun("?", "?", request.Target, _time.GetUtcNow(), _time.GetUtcNow());
            }
            if (busy is not null)
            {
                _logger.LogWarning("{Capability} refused: operation {Abandoned} ({AbandonedCapability}) still runs past its " +
                                   "deadline on an overlapping target (operation {OperationId})",
                    capability.Id, busy.OperationId, busy.CapabilityId, operationId);
                return Prepared.Refused(await RefuseAsync<object?>(operationId, request, capability, MutationStatus.Denied,
                    TargetBusyRefusal, ct) with { ReasonCode = MutationReasonCodes.TargetBusy });
            }
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
                progress.Request = request;
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
            progress.Request = request;
        }

        // R1-53: the request this operation came from, next to the ids that carry it on (decision, operation) — one
        // line that links the client's X-Request-Id to the operation id of the decision, approval, run and audit row.
        _logger.LogInformation(
            "Operation {OperationId} ({Capability}, {Surface}) decided {Verdict}: decision {DecisionId}, request {HttpRequestId}",
            operationId, capability.Id, request.Surface, decision.Verdict, request.DecisionId ?? "none",
            RequestCorrelation.CurrentOrNone);

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

        return new Prepared(null, request, capability, decision, callerOperationId, decision.Policy?.Preset)
        {
            Resolved = resolvedObject,
            NamedTarget = namedTarget,
        };
    }

    /// <summary>Contract 0012 (D7): whether <paramref name="resolved"/> is the object <paramref name="expected"/> named —
    /// same kind and stable id, and the same server when the expectation names one.</summary>
    internal static bool SameObject(MutationTarget expected, MutationTarget resolved) =>
        string.Equals(expected.Kind, resolved.Kind, StringComparison.Ordinal)
        && string.Equals(expected.Id, resolved.Id, StringComparison.Ordinal)
        && (string.IsNullOrEmpty(expected.ServerId)
            || string.Equals(expected.ServerId, resolved.ServerId, StringComparison.Ordinal));

    public async Task<MutationOutcome<T>> ExecuteAsync<T>(
        MutationRequest request,
        Func<MutationContext, CancellationToken, Task<T>> execute,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Principal);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        RejectEventKey(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CapabilityId);
        ArgumentNullException.ThrowIfNull(execute);

        var enclosing = MutationScope.Current;
        // A restore is staged: nothing is decided, run or written — its audit events would land in the database the
        // restore replaces, after the safety backup. A nested operation runs inside an admitted one and is counted by it.
        IDisposable? admitted = null;
        if (_maintenance is not null && enclosing is null)
        {
            admitted = _maintenance.TryEnter($"operation {request.CapabilityId}");
            if (admitted is null)
            {
                var refusedId = string.IsNullOrWhiteSpace(request.OperationId) ? Guid.NewGuid().ToString("N") : request.OperationId;
                _logger.LogWarning("{Capability} refused: a restore is in progress (operation {OperationId})",
                    request.CapabilityId, refusedId);
                return new MutationOutcome<T>(refusedId, MutationStatus.Denied, default, MaintenanceRefusal,
                    VerificationStatus.NotRun);
            }
        }
        var held = new Holdings(admitted);
        try
        {
            // Contract 0023 (point 6): a top-level run takes a shutdown-drain entry here, before anything is decided,
            // claimed or used; a nested call counts under the outer run's entry. Once stopping has begun the call is
            // refused, on record (the database is not being replaced), and no approval is used up.
            if (_drain is not null && enclosing is null)
            {
                var entry = _drain.TryEnter($"operation {request.CapabilityId}");
                if (entry is null)
                {
                    _logger.LogWarning("{Capability} refused: Whiskers is stopping", request.CapabilityId);
                    var refused = await RefuseAsync(request, ShuttingDownRefusal, CancellationToken.None);
                    return Cast<T>(refused) with { ReasonCode = MutationReasonCodes.ShuttingDown };
                }
                held.Add(entry);
            }
            return await ExecuteAdmittedAsync(request, execute, enclosing, held, ct);
        }
        finally
        {
            // Released here unless an abandoned run (or one with an abandoned nested run) took them along to its real
            // end (contract 0023, 4a and 4d).
            if (!held.HandedOff) held.Dispose();
        }
    }

    /// <summary>The pipeline after the maintenance admission and the drain entry (<paramref name="held"/>).</summary>
    private async Task<MutationOutcome<T>> ExecuteAdmittedAsync<T>(MutationRequest request,
        Func<MutationContext, CancellationToken, Task<T>> execute, MutationScope? enclosing, Holdings held,
        CancellationToken ct)
    {
        var prepared = await PrepareAsync(request, ct);
        if (prepared.Refusal is { } refusal) return Cast<T>(refusal);
        request = prepared.Request!;
        var capability = prepared.Capability!;
        var decision = prepared.Decision!;
        var operationId = request.OperationId!;
        // R1-53: every log line of this operation from here on — the gateway's and those of the services the execution
        // calls — carries its operation id as a structured scope (written by the redacting logger factory, R1-25).
        using var operationScope = _logger.BeginScope("Operation {OperationId} ({Capability})", operationId, capability.Id);

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
            catch (OperationCanceledException)
            {
                // INV-45: decided, cancelled before it ran: one refusal on record, and the caller sees the cancellation.
                // The gate may have raised the approval before the cancellation reached it: the event names it.
                await RefuseAsync<T>(operationId, request, capability, MutationStatus.Denied, CancelledBeforeRun, ct,
                    RaisedApprovalOf(request, capability, decision, operationId));
                throw;
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

        // INV-45: a caller that went away after the decision gets one refusal, not an intent for a run it no longer
        // waits for. A cancellation that races the intent write below does not break that write (it gets no caller
        // token); it is seen right after it and ends the intent as that same refusal — either way one event, and the
        // caller sees its cancellation.
        if (ct.IsCancellationRequested)
        {
            await RefuseAsync<T>(operationId, request, capability, MutationStatus.Denied, CancelledBeforeRun, ct,
                approval?.ApprovalId);
            ct.ThrowIfCancellationRequested();
        }

        // 5: no intent record, no mutation (PC-D5).
        long auditId;
        try
        {
            // Not the caller's token (the race above), but bounded: a write that runs out is the intent's refusal, kept
            // in the outbox (review round 4, NIT-4).
            using var intentTime = new CancellationTokenSource(RefusalWriteTimeout);
            auditId = await _audit.BeginAsync(operationId, request, capability, approval, intentTime.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit intent for {Capability} could not be written; operation {OperationId} refused",
                capability.Id, operationId);
            LogNotRun(operationId, capability.Id, MutationStatus.Denied, approval?.ApprovalId, request.DecisionId);
            return new MutationOutcome<T>(operationId, MutationStatus.Denied, default,
                "The audit record could not be written; the mutation was refused.", VerificationStatus.NotRun, ex)
                { DecisionId = request.DecisionId };
        }
        if (ct.IsCancellationRequested)
        {
            // Cancelled while the intent was written: nothing ran, nothing was claimed or used — the intent ends as the
            // refusal (INV-45, the intent race).
            await CompleteAuditAsync(new PendingAuditOutcome(auditId, operationId, MutationStatus.Denied,
                VerificationStatus.NotRun, CancelledBeforeRun, TimeSpan.Zero), capability);
            LogNotRun(operationId, capability.Id, MutationStatus.Denied, approval?.ApprovalId, request.DecisionId);
            ct.ThrowIfCancellationRequested();
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
            LogNotRun(operationId, capability.Id, MutationStatus.Denied, approval?.ApprovalId, request.DecisionId);
            return new MutationOutcome<T>(operationId, MutationStatus.Denied, default, duplicate!.Value.Reason,
                VerificationStatus.NotRun)
            {
                DecisionId = request.DecisionId,
                ReasonCode = duplicate.Value.Code,
                ReasonArgs = [operationId],
            };
        }

        // Contract 0012 (D9): the object is read again right before it is acted on — after the intent and the claim,
        // before the approval is used. Vanished or changed (or a read that fails): the intent ends as the refusal, the
        // claim is released, the approval stays usable, nothing runs — one event (INV-45).
        // Contract 0022 (point 4): a trusted script's hash is read again here too, although host.exec resolves a server.
        if (prepared.Resolved is { } decidedObject
            && (capability.Resolution == TargetResolutionKind.Object || decidedObject.Record.TrustedScript is not null)
            && _targets is not null)
        {
            TargetResolution confirmation;
            try
            {
                confirmation = await _targets.ConfirmAsync(request with { Target = prepared.NamedTarget ?? request.Target },
                    capability, decidedObject, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Target of {Capability} could not be confirmed (operation {OperationId})",
                    capability.Id, operationId);
                confirmation = new TargetResolution(null, "The target could not be confirmed (see the log); nothing ran.",
                    MutationReasonCodes.TargetNotResolved, ["see the log"]);
            }
            catch (OperationCanceledException)
            {
                await ReleaseAsync(operationId, claim, capability);
                await CompleteAuditAsync(new PendingAuditOutcome(auditId, operationId, MutationStatus.Denied,
                    VerificationStatus.NotRun, CancelledBeforeRun, TimeSpan.Zero), capability);
                LogNotRun(operationId, capability.Id, MutationStatus.Denied, approval?.ApprovalId, request.DecisionId);
                throw;
            }
            if (confirmation.Target is null)
            {
                await ReleaseAsync(operationId, claim, capability);
                var reason = confirmation.Error ?? "The target changed after the decision; nothing ran.";
                await CompleteAuditAsync(new PendingAuditOutcome(auditId, operationId, MutationStatus.Denied,
                    VerificationStatus.NotRun, reason, TimeSpan.Zero), capability);
                LogNotRun(operationId, capability.Id, MutationStatus.Denied, approval?.ApprovalId, request.DecisionId);
                return new MutationOutcome<T>(operationId, MutationStatus.Denied, default, reason, VerificationStatus.NotRun,
                    ApprovalId: approval?.ApprovalId)
                {
                    MatchedRules = decision.MatchedRules,
                    DecisionId = request.DecisionId,
                    ReasonCode = confirmation.Code ?? MutationReasonCodes.TargetChanged,
                    ReasonArgs = confirmation.Args ?? [decidedObject.Name ?? decidedObject.Id],
                };
            }
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
            LogNotRun(operationId, capability.Id, MutationStatus.Denied, approvalId, request.DecisionId);
            return new MutationOutcome<T>(operationId, MutationStatus.Denied, default, reason, VerificationStatus.NotRun,
                ApprovalId: approvalId) { MatchedRules = decision.MatchedRules, DecisionId = request.DecisionId };
        }

        // 6–7: execute inside the scope and under the deadline, then verify. Contract 0023: the caller waits at most the
        // deadline plus the grace period G; an action still running then is abandoned and goes on in the background,
        // keeping what protects its target until it really ends.
        var context = new MutationContext(operationId, request, capability) { Target = prepared.Resolved };
        var started = _time.GetTimestamp();
        var startedAt = _time.GetUtcNow();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        held.CompleteOnRelease(completion);
        // 4d: the enclosing run keeps what it holds until this one has really ended too.
        enclosing?.AddNested(operationId, completion.Task);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        held.Add(deadline);
        deadline.CancelAfter(capability.Deadline);
        var run = new ActionRun();
        held.Add(run);
        var action = RunActionAsync(run, execute, context, decision, approval, deadline.Token);

        if (!action.IsCompleted)
        {
            using var limit = new CancellationTokenSource(capability.Deadline + _grace, _time);
            try
            {
                await action.WaitAsync(limit.Token);
            }
            catch
            {
                // The wait ran out, or the action failed: both are read below.
            }
            // Exactly one ending (point 1): the abandon wins only while the action has not marked its own end.
            if (!action.IsCompleted && run.TryAbandon())
                return await AbandonAsync(run, action, held, completion.Task, claim, auditId, request, capability, approval,
                    started, startedAt);
        }

        MutationOutcome<T> outcome;
        T? value = default;
        Exception? thrown = null;
        try
        {
            value = await action;
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        // What the tool reported belongs to the event also when it then threw.
        var results = run.Results;
        var reported = run.FailureReason;
        var facts = run.ReadBackFacts;

        // 4d: a run nested in this one that is still running gets at most G more; the outcome never claims an effect
        // while one of them is still at work.
        var pendingNested = await NestedAfterGraceAsync(run);
        // Closed before verification as always — unless a nested run still needs its scope chain.
        if (pendingNested.Count == 0) run.CloseScope();

        if (thrown is not null)
        {
            outcome = thrown switch
            {
                // R1-38 (INV-40): a call stopped while it ran never reads as "nothing happened" — it may have changed
                // something before the cancellation reached it, so the reason says the effect is unknown.
                OperationCanceledException when deadline.IsCancellationRequested && !ct.IsCancellationRequested =>
                    new MutationOutcome<T>(operationId, MutationStatus.Failed, default,
                        $"'{capability.Id}' exceeded its deadline of {capability.Deadline} and was cancelled; " +
                        "its effect is unknown — read the current state before retrying.", VerificationStatus.NotRun, thrown,
                        approval?.ApprovalId),
                OperationCanceledException when ct.IsCancellationRequested =>
                    new MutationOutcome<T>(operationId, MutationStatus.Failed, default,
                        $"'{capability.Id}' was cancelled while it ran; its effect is unknown — read the current state " +
                        "before retrying.", VerificationStatus.NotRun, thrown, approval?.ApprovalId),
                _ => new MutationOutcome<T>(operationId, MutationStatus.Failed, default, thrown.Message,
                    VerificationStatus.NotRun, thrown, approval?.ApprovalId),
            };
        }
        else if (pendingNested.Count > 0)
        {
            // 4d: Failed with NestedAbandoned, the value withheld (as for R1-36), an error the UI runner and the MCP filter
            // treat before a reported failure — never a half state read as the effect.
            var reason = $"'{capability.Id}': {NestedAbandonedReason}";
            outcome = new MutationOutcome<T>(operationId, MutationStatus.Failed, default, reason, VerificationStatus.NotRun,
                new MutationAbandonedException(reason, capability.Deadline, pendingNested[0].OperationId),
                approval?.ApprovalId)
            {
                ReasonCode = MutationReasonCodes.NestedAbandoned,
            };
        }
        else if (reported is not null)
        {
            // The delegate returned but reported a failure (MutationScope.Fail): Failed, with its value kept so the
            // caller still gets the tool's own answer.
            outcome = new MutationOutcome<T>(operationId, MutationStatus.Failed, value, reported,
                VerificationStatus.NotRun, ApprovalId: approval?.ApprovalId);
        }
        else
        {
            // D6: an action that returned after its deadline (within G) is verified with a token of its own, bounded by
            // G — never with the deadline's, which is already cancelled.
            using var verifyTime = deadline.IsCancellationRequested ? new CancellationTokenSource(_grace, _time) : null;
            var (verification, problem, unknown) = await VerifyAsync(context with { ReadBackFacts = facts }, value,
                verifyTime?.Token ?? deadline.Token);
            var verificationNote = verification == VerificationStatus.NotVerifiable ? problem : null;
            // R1-36 (INV-43/44): the call completed, but its effect is not there — never a success. The status says
            // Failed, the verification says the call itself ran; the value is withheld so no caller can read it as the
            // effect's result.
            outcome = verification == VerificationStatus.Failed
                ? new MutationOutcome<T>(operationId, MutationStatus.Failed, default,
                    $"'{capability.Id}' ran, but its effect could not be verified: {problem}", verification,
                    new MutationVerificationFailedException(
                        $"'{capability.Id}' ran, but its effect could not be verified: {problem}", unknown),
                    approval?.ApprovalId)
                : new MutationOutcome<T>(operationId, MutationStatus.Executed, value, null, verification,
                    ApprovalId: approval?.ApprovalId) { VerificationNote = verificationNote };
        }

        if (pendingNested.Count == 0)
            await FinishJournalAsync(operationId, claim, capability, outcome.Status, outcome.Verification);

        // 8: the outcome is recorded even when the caller has gone away (CancellationToken.None). The mutation has
        // already happened at this point, so a failed write must not hide its result from the caller; the intent
        // row stays as evidence that it ran, and the outcome is kept and written again (R1-19).
        // An executed call that could not be verified carries why (R1-37) in the reason of its outcome row.
        await CompleteAuditAsync(new PendingAuditOutcome(auditId, operationId, outcome.Status, outcome.Verification,
            outcome.Reason ?? (outcome.VerificationNote is { } note ? $"Not verifiable: {note}" : null),
            _time.GetElapsedTime(started), results), capability);
        // R1-53: how it ended, under the ids an operator follows it by. The reason stays in the audit row.
        _logger.Log(outcome.Status == MutationStatus.Executed ? LogLevel.Information : LogLevel.Warning,
            "Operation {OperationId} ({Capability}) {Status}: verification {Verification}, approval {ApprovalId}, audit {AuditId}, request {HttpRequestId}",
            operationId, capability.Id, outcome.Status, outcome.Verification, outcome.ApprovalId ?? "none", auditId,
            RequestCorrelation.CurrentOrNone);

        if (pendingNested.Count > 0)
        {
            // 4d: the release moves to the background and waits for every nested run — the scope (the inner action keeps
            // its scope chain), the journal claim, the maintenance admission and the drain entry. The journal ends as
            // Failed, as the outcome says.
            held.HandedOff = true;
            var finalStatus = outcome.Status;
            var finalVerification = outcome.Verification;
            // Off the caller's flow: the release writes the gateway's own journal and opens no decision.
            using var suppressed = ExecutionContext.SuppressFlow();
            _ = Task.Run(async () =>
            {
                try
                {
                    await WaitForNestedAsync(run);
                    await FinishJournalAsync(operationId, claim, capability, finalStatus, finalVerification);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Release of operation {OperationId} ({Capability}) after its nested runs failed",
                        operationId, capability.Id);
                }
                finally
                {
                    held.Dispose();
                }
            });
        }
        return outcome with { DecisionId = request.DecisionId, Completion = completion.Task };
    }

    /// <summary>Contract 0023 (4d): the reason of a run whose nested run is still running past its deadline.</summary>
    internal const string NestedAbandonedReason =
        "a nested run is still running past its deadline; its effect is unknown — read the current state before retrying.";

    /// <summary>Opens the run's scope on the action's own async flow (it never leaks into the gateway's) and runs the
    /// action in it. The action marks its own end, so exactly one of "ended" and "abandoned" happens (point 1).</summary>
    private static async Task<T> RunActionAsync<T>(ActionRun run, Func<MutationContext, CancellationToken, Task<T>> execute,
        MutationContext context, MutationPolicyDecision decision, ApprovalGateResult? approval, CancellationToken token)
    {
        try
        {
            var scope = MutationScope.Begin(context.Capability.Id, context.OperationId, context.Request.Principal,
                context.Request);
            run.Scope = scope;
            // Contract 0006: which rule set decided, for the re-check after a compose run.
            scope.SetDecision(decision.AgentRuleSet, decision.ExposureConfirmClasses);
            scope.DecidedLevel = context.Capability.RequiredLevel;
            // The person whose approval was used for this run (approvers are always people, by email).
            if (approval is { Approved: true, ApprovedBy: { } approver } && approver.Contains('@'))
                scope.ApprovedBy = PrincipalIds.User(approver);
            // Contract 0022, amendment 2 (B2): which rules of the decision that approval covered.
            if (approval is { Approved: true })
                scope.ApprovedRules = new HashSet<string>(decision.MatchedRules, StringComparer.Ordinal);
            return await execute(context, token);
        }
        finally
        {
            run.Ended();
        }
    }

    /// <summary>
    /// Contract 0023 (points 2–4): answers the caller without the action. The outcome row is written now; the action
    /// goes on in the background with its scope, journal claim, maintenance admission, drain entry and deadline, listed
    /// in <see cref="IAbandonedRuns"/> (which guards its target), and its late end is audited when it ends.
    /// </summary>
    private async Task<MutationOutcome<T>> AbandonAsync<T>(ActionRun run, Task<T> action, Holdings held, Task completion,
        string? claim, long auditId, MutationRequest request, Capability capability, ApprovalGateResult? approval,
        long started, DateTimeOffset startedAt)
    {
        var operationId = request.OperationId!;
        held.HandedOff = true;
        try
        {
            held.Add(_abandoned?.Add(new AbandonedRun(capability.Id, operationId, request.Target, _time.GetUtcNow(),
                startedAt)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Abandoned operation {OperationId} ({Capability}) could not be listed", operationId,
                capability.Id);
        }

        var reason = $"'{capability.Id}' ran past its deadline of {capability.Deadline} and did not stop; it is still " +
                     "running and its effect is unknown — read the current state before retrying.";
        var outcome = new MutationOutcome<T>(operationId, MutationStatus.Failed, default, reason, VerificationStatus.NotRun,
            new MutationAbandonedException(reason, capability.Deadline), approval?.ApprovalId)
        {
            Abandoned = true,
            ReasonCode = MutationReasonCodes.Abandoned,
            ReasonArgs = [capability.Id, capability.Deadline.ToString()],
            DecisionId = request.DecisionId,
            Completion = completion,
        };
        try
        {
            await CompleteAuditAsync(new PendingAuditOutcome(auditId, operationId, outcome.Status, outcome.Verification,
                reason, _time.GetElapsedTime(started), run.Results), capability);
            _logger.LogWarning(
                "Operation {OperationId} ({Capability}) abandoned: it ran past its deadline of {Deadline} and did not stop; " +
                "it goes on in the background, its target held (audit {AuditId}, request {HttpRequestId})",
                operationId, capability.Id, capability.Deadline, auditId, RequestCorrelation.CurrentOrNone);
        }
        finally
        {
            // Off the caller's flow: the late end writes the gateway's own records and opens no decision.
            using (ExecutionContext.SuppressFlow())
                _ = Task.Run(() => LateEndAsync(run, action, held, claim, auditId, operationId, capability, started));
        }
        return outcome;
    }

    /// <summary>Contract 0023 (point 3): waits for the abandoned action's real end (or the end of the shutdown drain),
    /// then for its nested runs, writes its late end as a further version of its audit row, finishes its journal claim
    /// as <c>Failed</c> and releases what it held. Never throws.</summary>
    private async Task LateEndAsync(ActionRun run, Task action, Holdings held, string? claim, long auditId,
        string operationId, Capability capability, long started)
    {
        try
        {
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var abort = _drain is null ? default : _drain.Abort.Register(() => stopped.TrySetResult());
            var cutOff = await Task.WhenAny(action, stopped.Task) != action;
            if (!cutOff)
                cutOff = await Task.WhenAny(WaitForNestedAsync(run), stopped.Task) == stopped.Task;
            var how = cutOff ? "cut off at shutdown"
                : action.Status == TaskStatus.RanToCompletion ? "returned"
                : $"threw {(action.IsCanceled ? nameof(OperationCanceledException) : action.Exception?.InnerException?.GetType().Name ?? "an exception")}";
            var results = run.Results.ToList();
            if (run.FailureReason is { } reported)
                results.Add(new MutationDomainResult(capability.Id, "", "", "", reported, null, false));
            var duration = _time.GetElapsedTime(started);
            try
            {
                await _audit.CompleteLateEndAsync(auditId, operationId, how, duration, results, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Late end of {Capability} (operation {OperationId}) could not be written", capability.Id,
                    operationId);
                _recovery?.LateEndNotWritten(new PendingLateEnd(auditId, operationId, how, duration, results));
            }
            // Cut off by the stop: the claim stays Running, and the next start reports the operation as interrupted.
            if (!cutOff)
                await FinishJournalAsync(operationId, claim, capability, MutationStatus.Failed, VerificationStatus.NotRun);
            _logger.LogInformation(
                "Abandoned operation {OperationId} ({Capability}) ended: {How} after {Duration}", operationId, capability.Id,
                how, duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Late end of operation {OperationId} ({Capability}) failed", operationId, capability.Id);
        }
        finally
        {
            held.Dispose();
        }
    }

    /// <summary>Contract 0023 (4d): the nested runs still running once G has passed after the action returned.</summary>
    private async Task<IReadOnlyList<(string OperationId, Task Completion)>> NestedAfterGraceAsync(ActionRun run)
    {
        var pending = run.PendingNested();
        if (pending.Count == 0) return pending;
        using var grace = new CancellationTokenSource(_grace, _time);
        try
        {
            await Task.WhenAll(pending.Select(p => p.Completion)).WaitAsync(grace.Token);
        }
        catch (OperationCanceledException) when (grace.IsCancellationRequested)
        {
            // Still running: answered as NestedAbandoned.
        }
        return run.PendingNested();
    }

    /// <summary>Waits until every run nested in <paramref name="run"/> has ended, also those started meanwhile.</summary>
    private static async Task WaitForNestedAsync(ActionRun run)
    {
        while (run.PendingNested() is { Count: > 0 } pending)
            await Task.WhenAll(pending.Select(p => p.Completion));
    }

    /// <summary>The claim ends with the outcome. A failed write leaves it Running: after a restart a retry is refused as
    /// "outcome unknown", never run again.</summary>
    private async Task FinishJournalAsync(string operationId, string? claim, Capability capability, MutationStatus status,
        VerificationStatus verification)
    {
        if (_journal is null || claim is null) return;
        try
        {
            await _journal.FinishAsync(operationId, claim, status, verification, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operation journal for {Capability} (operation {OperationId}) could not be finished",
                capability.Id, operationId);
        }
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
        {
            // R1-37: the missing read-back is recorded with its reason, never as a success.
            var contract = VerificationContracts.Find(context.Capability.Id);
            return (VerificationStatus.NotVerifiable, contract switch
            {
                { Kind: VerificationKind.NotVerifiable } => contract.Reason,
                { Kind: VerificationKind.ReadBack } => "its read-back is declared but no verifier is registered.",
                _ => "no verification is declared for this capability.",
            }, false);
        }
        try
        {
            var status = await verifier.VerifyAsync(context, result, ct);
            return status switch
            {
                VerificationStatus.Failed => (status, "the read-back did not show it.", false),
                VerificationStatus.NotVerifiable => (status, "the verifier could not read this call back.", false),
                // A verifier has no other honest answer: NotRun from it would hide that nothing was checked.
                VerificationStatus.NotRun => (VerificationStatus.NotVerifiable, "the verifier did not check this call.", false),
                _ => (status, null, false),
            };
        }
        catch (MutationNotVerifiableException notVerifiable)
        {
            return (VerificationStatus.NotVerifiable, notVerifiable.Message, false);
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
            // INV-45: the refusal is recorded even when the caller has gone away, like an outcome (step 8): not on the
            // caller's token, but within its own bounded time — one that runs out goes to the outbox below.
            using var bounded = new CancellationTokenSource(RefusalWriteTimeout);
            await _audit.RecordDeniedAsync(operationId, request, capability, status, reason, approvalId, bounded.Token);
        }
        catch (Exception ex)
        {
            // The mutation is refused either way; losing the refusal record must not turn it into an error. The sink
            // keeps the event in its outbox and AuditRecovery inserts it later (R1-19).
            _logger.LogError(ex, "Audit record for refused {Capability} (operation {OperationId}) could not be written",
                request.CapabilityId, operationId);
        }
        LogNotRun(operationId, request.CapabilityId, status, approvalId, request.DecisionId);
        return new MutationOutcome<T>(operationId, status, default, reason, VerificationStatus.NotRun, ApprovalId: approvalId)
        {
            DecisionId = request.DecisionId,
        };
    }

    /// <summary>R1-53: a refusal or a wait for approval, linked to its request — every path that ends an operation
    /// without running it. The reason stays in the audit row; every value passes the redacting logger factory.</summary>
    private void LogNotRun(string operationId, string capabilityId, MutationStatus status, string? approvalId,
        string? decisionId) =>
        _logger.LogInformation(
            "Operation {OperationId} ({Capability}) not run: {Status}, approval {ApprovalId}, decision {DecisionId}, request {HttpRequestId}",
            operationId, capabilityId, status, approvalId ?? "none", decisionId ?? "none",
            RequestCorrelation.CurrentOrNone);

    /// <summary>Nobody is there to confirm: an automation run, or anything made for an AI trigger (R1-13).</summary>
    private static bool Unattended(MutationRequest request) => Mutations.Unattended.Is(request);

    /// <summary>Result of steps 1–5: a refusal (already audited), or the decided request.</summary>
    private sealed record Prepared(MutationOutcome<object?>? Refusal, MutationRequest? Request, Capability? Capability,
        MutationPolicyDecision? Decision, string? CallerOperationId, string? Preset)
    {
        /// <summary>The admission lock this call holds until the approval gate answered; disposing releases it.</summary>
        public AdmissionLease? Admission { get; init; }

        /// <summary>Contract 0012: the typed object the target resolved to (null without a resolver or from a double).</summary>
        public ResolvedTarget? Resolved { get; init; }

        /// <summary>The target as the caller named it, before resolution: the confirmation resolves it the same way again.</summary>
        public MutationTarget? NamedTarget { get; init; }

        public static Prepared Refused(MutationOutcome<object?> refusal, string? preset = null) =>
            new(refusal, null, null, null, null, preset);
    }

    /// <summary>The approval the gate raised (or holds open) for this operation, if any — for the event of a call
    /// cancelled while the gate answered. A failed lookup names none; the refusal stands.</summary>
    private string? RaisedApprovalOf(MutationRequest request, Capability capability, MutationPolicyDecision decision,
        string operationId)
    {
        try
        {
            return ApprovalBinding.Of(request, capability, decision.Policy?.VersionHash) is { } binding
                ? _approvals?.OpenApprovalOf(binding, operationId)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open approval of operation {OperationId} could not be looked up", operationId);
            return null;
        }
    }

    /// <summary>How long a refusal's audit write may take (INV-45): long enough for a queue of audit writes, bounded
    /// so a stop never waits on it — a write that runs out of time is kept in the audit outbox (R1-19).</summary>
    internal static readonly TimeSpan RefusalWriteTimeout = TimeSpan.FromSeconds(20);

    /// <summary>INV-45 (review round 7, NIT-1): an event key names the one refusal several paths may write; it is
    /// honoured only on <see cref="RefuseAsync"/>. A request that is decided or executed carries none — its denials and
    /// approvals are attempts of their own and must never merge into one row.</summary>
    private static void RejectEventKey(MutationRequest request)
    {
        if (request.EventKey is not null)
            throw new ArgumentException("An event key is honoured only for a refusal (RefuseAsync).", nameof(request));
    }

    /// <summary>How far <see cref="PrepareCoreAsync"/> got: the request as decided so far and its capability, for the
    /// refusal a cancellation on the way leaves on record (INV-45).</summary>
    private sealed class PrepareProgress
    {
        public MutationRequest? Request { get; set; }
        public Capability? Capability { get; set; }
    }

    /// <summary>
    /// Contract 0023 (4a): what a run holds until it really ends — the maintenance admission, the drain entry, the
    /// deadline, its scope and its listing as abandoned — released once, in reverse order, and then its
    /// <see cref="MutationOutcome{T}.Completion"/> completed. <see cref="HandedOff"/>: the release moved to the
    /// background (an abandoned run, or one with an abandoned nested run).
    /// </summary>
    private sealed class Holdings(IDisposable? first) : IDisposable
    {
        private readonly List<IDisposable> _items = first is null ? [] : [first];
        private TaskCompletionSource? _completion;
        private int _disposed;

        public bool HandedOff { get; set; }

        public void Add(IDisposable? item)
        {
            if (item is null) return;
            lock (_items) _items.Add(item);
        }

        public void CompleteOnRelease(TaskCompletionSource completion) => _completion = completion;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            IDisposable[] items;
            lock (_items) items = [.. _items];
            for (var i = items.Length - 1; i >= 0; i--)
            {
                try
                {
                    items[i].Dispose();
                }
                catch
                {
                    // One release that fails never keeps the others (or the completion) back.
                }
            }
            _completion?.TrySetResult();
        }
    }

    /// <summary>Contract 0023: the action of one run — its scope (opened on the action's own flow) and which of its two
    /// endings happened: its own end, or the gateway's abandon.</summary>
    private sealed class ActionRun : IDisposable
    {
        private const int Running = 0, EndedItself = 1, AbandonedByGateway = 2;
        private int _state;

        public MutationScope? Scope { get; set; }

        /// <summary>The action marks its end; it no longer counts once the gateway has abandoned it.</summary>
        public void Ended() => Interlocked.CompareExchange(ref _state, EndedItself, Running);

        /// <summary>The gateway abandons the action: true only while the action has not marked its own end.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, AbandonedByGateway, Running) == Running;

        public IReadOnlyList<MutationDomainResult> Results => Scope?.Results ?? [];
        public string? FailureReason => Scope?.FailureReason;
        public IReadOnlyDictionary<string, string> ReadBackFacts =>
            Scope?.ReadBackFacts ?? new Dictionary<string, string>();
        public IReadOnlyList<(string OperationId, Task Completion)> PendingNested() => Scope?.PendingNested() ?? [];

        public void CloseScope() => Scope?.Dispose();
        public void Dispose() => CloseScope();
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
