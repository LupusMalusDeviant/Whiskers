using System.Diagnostics;
using Whiskers.Models;

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
}

/// <summary>Step 7 of the pipeline: checks that a capability actually took effect (INV-43).</summary>
public interface IMutationVerifier
{
    string CapabilityId { get; }

    Task<VerificationStatus> VerifyAsync(MutationContext context, object? result, CancellationToken ct);
}

/// <summary>
/// WP2 step 2.0 implementation. Not yet called by any entry point; steps 2.1–2.5 move the surfaces onto it.
/// Approvals are not wired yet: a <c>Confirm</c> verdict returns <see cref="MutationStatus.PendingApproval"/> without
/// creating one. Persisted, hash-bound approvals arrive with WP3 behind the same outcome.
/// </summary>
public sealed class MutationGateway : IMutationGateway
{
    private readonly ICapabilityCatalog _catalog;
    private readonly IMutationPolicy _policy;
    private readonly IMutationAuditSink _audit;
    private readonly IReadOnlyDictionary<string, IMutationVerifier> _verifiers;
    private readonly ILogger<MutationGateway> _logger;
    private readonly TimeProvider _time;

    public MutationGateway(
        ICapabilityCatalog catalog,
        IMutationPolicy policy,
        IMutationAuditSink audit,
        IEnumerable<IMutationVerifier> verifiers,
        ILogger<MutationGateway> logger,
        TimeProvider? time = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _verifiers = (verifiers ?? throw new ArgumentNullException(nameof(verifiers)))
            .ToDictionary(v => v.CapabilityId, StringComparer.Ordinal);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
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

        var operationId = string.IsNullOrWhiteSpace(request.OperationId) ? Guid.NewGuid().ToString("N") : request.OperationId;
        request = request with { OperationId = operationId };

        // 1–2: the capability must exist and the principal must hold its level. Unknown means Deny.
        var capability = _catalog.Find(request.CapabilityId);
        if (capability is null)
            return await RefuseAsync<T>(operationId, request, null, MutationStatus.Denied,
                $"Unknown capability '{request.CapabilityId}'.", ct);

        if (!McpPermissionLevels.HasAccess(McpPermissionLevels.Normalize(request.Principal.PermissionLevel), capability.RequiredLevel))
            return await RefuseAsync<T>(operationId, request, capability, MutationStatus.Denied,
                $"'{capability.Id}' requires '{capability.RequiredLevel}'; {request.Principal.DisplayName} has " +
                $"'{McpPermissionLevels.Normalize(request.Principal.PermissionLevel)}'.", ct);

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

        if (decision.Verdict == MutationVerdict.Deny)
            return await RefuseAsync<T>(operationId, request, capability, MutationStatus.Denied, decision.Reason, ct);
        // 4: approvals are wired in WP3; until then a Confirm verdict never executes.
        if (decision.Verdict == MutationVerdict.Confirm)
            return await RefuseAsync<T>(operationId, request, capability, MutationStatus.PendingApproval, decision.Reason, ct);

        // 5: no intent record, no mutation (PC-D5).
        long auditId;
        try
        {
            auditId = await _audit.BeginAsync(operationId, request, capability, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit intent for {Capability} could not be written; operation {OperationId} refused",
                capability.Id, operationId);
            return new MutationOutcome<T>(operationId, MutationStatus.Denied, default,
                "The audit record could not be written; the mutation was refused.", VerificationStatus.NotRun, ex);
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
                using (MutationScope.Begin(capability.Id, operationId))
                    value = await execute(context, deadline.Token);

                var verification = await VerifyAsync(context, value, deadline.Token);
                outcome = new MutationOutcome<T>(operationId, MutationStatus.Executed, value,
                    verification == VerificationStatus.Failed ? "Executed, but verification failed." : null, verification);
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                outcome = new MutationOutcome<T>(operationId, MutationStatus.Failed, default,
                    $"'{capability.Id}' exceeded its deadline of {capability.Deadline}.", VerificationStatus.NotRun, ex);
            }
            catch (Exception ex)
            {
                outcome = new MutationOutcome<T>(operationId, MutationStatus.Failed, default, ex.Message,
                    VerificationStatus.NotRun, ex);
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
        return outcome;
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

    private async Task<MutationOutcome<T>> RefuseAsync<T>(string operationId, MutationRequest request,
        Capability? capability, MutationStatus status, string reason, CancellationToken ct)
    {
        Debug.Assert(status is MutationStatus.Denied or MutationStatus.PendingApproval);
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
        return new MutationOutcome<T>(operationId, status, default, reason, VerificationStatus.NotRun);
    }
}
