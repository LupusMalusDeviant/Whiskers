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
    MutationStatus? Refused);

/// <summary>Step 7 of the pipeline: checks that a capability actually took effect (INV-43).</summary>
public interface IMutationVerifier
{
    string CapabilityId { get; }

    Task<VerificationStatus> VerifyAsync(MutationContext context, object? result, CancellationToken ct);
}

/// <summary>
/// Since WP2 step 2.1 every MCP tool call and every agent tool call that maps to a capability runs through here
/// (<c>McpGuardrailFilter</c>, <c>AgentToolInvoker</c>). UI handlers, jobs, webhooks and terminals follow in steps
/// 2.2–2.5. A <c>Confirm</c> verdict goes to <see cref="IApprovalGate"/>: the mutation runs only with an approval a
/// human gave for exactly this call; otherwise the outcome is <see cref="MutationStatus.PendingApproval"/>. Approvals
/// stay in memory until WP3 persists them.
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

    /// <param name="overrides">Tool switches and stricter stored levels (INV-15); none = catalog levels only.</param>
    /// <param name="targets">Target resolution (INV-57); none = targets are taken as given.</param>
    /// <param name="approvals">Where a <c>Confirm</c> verdict is decided; none = every <c>Confirm</c> waits.</param>
    /// <param name="decisions">Where every policy decision is recorded before it is acted on (INV-22). The app always
    /// registers one; a gateway built without it (unit tests of other steps) records nothing.</param>
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
        IPolicyDecisionStore? decisions = null)
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
        if (prepared.Refusal is { } refusal)
            return new MutationDecisionResult(refusal.OperationId, MutationVerdict.Deny, refusal.Reason ?? "Refused.",
                refusal.MatchedRules, refusal.DecisionId, prepared.Preset, refusal.Status);
        var decision = prepared.Decision!;
        return new MutationDecisionResult(prepared.Request!.OperationId!, decision.Verdict, decision.Reason,
            decision.MatchedRules, prepared.Request.DecisionId, decision.Policy?.Preset, null);
    }

    /// <summary>Steps 1–5: capability, tool settings, effective requirement, level, target, policy and the decision
    /// record. A refusal on the way is audited and returned as <see cref="Prepared.Refusal"/>; a policy Deny too.</summary>
    private async Task<Prepared> PrepareAsync(MutationRequest request, CancellationToken ct)
    {
        // Only an operation id the caller chose can name an approval the caller raised itself.
        var callerOperationId = string.IsNullOrWhiteSpace(request.OperationId) ? null : request.OperationId;
        var operationId = callerOperationId ?? Guid.NewGuid().ToString("N");
        request = request with { OperationId = operationId };
        // INV-23: a mutation nested in a pinned run's mutation (a scheduled task an AI trigger runs) is decided
        // against the run's preset too; the global kill switch still reaches it (EffectivePolicy).
        if (request.PinnedPolicy is null && MutationScope.Current?.PinnedPolicy is { } inherited)
            request = request with { PinnedPolicy = inherited };

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
        var callerOperationId = prepared.CallerOperationId;

        // 4: Confirm runs only with a human approval for exactly this call.
        ApprovalGateResult? approval = null;
        // An unattended run (scheduler, AutoUpdate, AutoPatch) has nobody who could confirm it, and the next occurrence
        // of the same task is a new run, not a retry: it must never reach the approval gate, which would raise an
        // approval and later match an identical occurrence against the grant. Terminal refusal, audited.
        if (decision.Verdict == MutationVerdict.Confirm && request.Surface == MutationSurface.Automation)
            return await RefuseAsync<T>(operationId, request, capability, MutationStatus.Denied,
                $"Unattended run refused: it needs a confirmation nobody can give ({decision.Reason}).", ct)
                with { MatchedRules = decision.MatchedRules };
        if (decision.Verdict == MutationVerdict.Confirm)
        {
            if (_approvals is null)
                return await RefuseAsync<T>(operationId, request, capability, MutationStatus.PendingApproval, decision.Reason, ct)
                    with { MatchedRules = decision.MatchedRules };
            try
            {
                approval = await _approvals.CheckAsync(ApprovalSubjectOf(request, capability, callerOperationId, decision.Policy?.Preset),
                    decision.Reason, decision.MatchedRules, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Approval check for {Capability} failed (operation {OperationId})", capability.Id, operationId);
                return await RefuseAsync<T>(operationId, request, capability, MutationStatus.Denied,
                    "The approval could not be checked; the mutation was refused.", ct);
            }
            if (!approval.Approved)
                return await RefuseAsync<T>(operationId, request, capability, MutationStatus.PendingApproval,
                    approval.Message, ct, approval.ApprovalId) with { MatchedRules = decision.MatchedRules };
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

        // The approval is used only now, after the intent is on record: a refusal before this point leaves it valid.
        // Losing the race against a concurrent identical call (or the approval lapsing meanwhile) turns this attempt's
        // intent row into a refusal.
        if (approval?.ApprovalId is { } approvalId && !_approvals!.TryConsume(approvalId))
        {
            var reason = $"Approval {approvalId} is no longer usable (used by another call, revoked or expired); the mutation was refused.";
            try
            {
                await _audit.CompleteAsync(auditId, operationId, MutationStatus.Denied, VerificationStatus.NotRun, reason,
                    TimeSpan.Zero, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit outcome for refused {Capability} (operation {OperationId}) could not be written",
                    capability.Id, operationId);
            }
            return new MutationOutcome<T>(operationId, MutationStatus.Denied, default, reason, VerificationStatus.NotRun,
                ApprovalId: approvalId) { MatchedRules = decision.MatchedRules, DecisionId = request.DecisionId };
        }

        // 6–7: execute inside the scope and under the deadline, then verify.
        var context = new MutationContext(operationId, request, capability);
        var started = _time.GetTimestamp();
        MutationOutcome<T> outcome;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(capability.Deadline);
            try
            {
                T value;
                string? reported;
                using (var scope = MutationScope.Begin(capability.Id, operationId, context.Request.Principal, context.Request))
                {
                    value = await execute(context, deadline.Token);
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
                    var verification = await VerifyAsync(context, value, deadline.Token);
                    outcome = new MutationOutcome<T>(operationId, MutationStatus.Executed, value,
                        verification == VerificationStatus.Failed ? "Executed, but verification failed." : null, verification,
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

        // 8: the outcome is recorded even when the caller has gone away (CancellationToken.None). The mutation has
        // already happened at this point, so a failed write must not hide its result from the caller; the intent
        // row stays as evidence that it ran.
        try
        {
            await _audit.CompleteAsync(auditId, operationId, outcome.Status, outcome.Verification, outcome.Reason,
                _time.GetElapsedTime(started), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit outcome for {Capability} (operation {OperationId}, status {Status}) could not be written",
                capability.Id, operationId, outcome.Status);
        }
        return outcome with { DecisionId = request.DecisionId };
    }

    private async Task<VerificationStatus> VerifyAsync(MutationContext context, object? result, CancellationToken ct)
    {
        if (!_verifiers.TryGetValue(context.Capability.Id, out var verifier))
            return VerificationStatus.NotVerifiable;
        try
        {
            return await verifier.VerifyAsync(context, result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verifier for {Capability} failed (operation {OperationId})",
                context.Capability.Id, context.OperationId);
            return VerificationStatus.Failed;
        }
    }

    /// <summary>The approval subject carries the MCP tool name when there is one: that is the name on the approval card
    /// and in the agent's own approval.</summary>
    private static ApprovalSubject ApprovalSubjectOf(MutationRequest request, Capability capability, string? callerOperationId,
        string? decisionPreset)
    {
        var p = request.Principal;
        var actorKey = $"{p.Kind}:{p.Id}:{p.CredentialId}:{p.OnBehalfOf?.Id}";
        var actorType = request.Surface switch
        {
            MutationSurface.Mcp => "mcp-direct",
            MutationSurface.Ui => "web",
            _ => request.Surface.ToString().ToLowerInvariant(),
        };
        return new ApprovalSubject(capability.McpTool ?? capability.Id, capability.RequiredLevel, actorKey,
            p.DisplayName, actorType, request.Arguments, callerOperationId, request.SessionId)
        {
            DecisionId = request.DecisionId,
            Preset = request.DecisionId is null ? null : decisionPreset,
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
            await _audit.RecordDeniedAsync(operationId, request, capability, status, reason, ct);
        }
        catch (Exception ex)
        {
            // The mutation is refused either way; losing the refusal record must not turn it into an error.
            _logger.LogError(ex, "Audit record for refused {Capability} (operation {OperationId}) could not be written",
                request.CapabilityId, operationId);
        }
        return new MutationOutcome<T>(operationId, status, default, reason, VerificationStatus.NotRun, ApprovalId: approvalId)
        {
            DecisionId = request.DecisionId,
        };
    }

    /// <summary>Result of steps 1–5: a refusal (already audited), or the decided request.</summary>
    private sealed record Prepared(MutationOutcome<object?>? Refusal, MutationRequest? Request, Capability? Capability,
        MutationPolicyDecision? Decision, string? CallerOperationId, string? Preset)
    {
        public static Prepared Refused(MutationOutcome<object?> refusal, string? preset = null) =>
            new(refusal, null, null, null, null, preset);
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
