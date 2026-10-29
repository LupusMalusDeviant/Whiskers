using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Mutations;

/// <summary>What the gateway asks to run under an operation id (R1-18).</summary>
public sealed record OperationClaimRequest(
    string OperationId,
    string? ParentId,
    string CapabilityId,
    string PrincipalId,
    string InputHash,
    string? DecisionId,
    string? ApprovalId,
    long? AuditId);

/// <summary>Whether the operation may run now.</summary>
public enum OperationClaimResult
{
    /// <summary>The id is this call's now: run it.</summary>
    Claimed,
    /// <summary>The same operation is running right now (a concurrent retry).</summary>
    AlreadyRunning,
    /// <summary>The operation ran already (executed or failed): it does not run again.</summary>
    AlreadyRun,
    /// <summary>It was running when Whiskers stopped; whether it took effect is unknown. It is not run again blindly.</summary>
    OutcomeUnknown,
    /// <summary>The id was used for something else (another principal, capability or arguments).</summary>
    Mismatch,
}

/// <summary>The answer to a claim. <paramref name="Token"/> is set only when <see cref="OperationClaimResult.Claimed"/>:
/// it names this one claim, and only it can finish or give the claim back — a retried release of an earlier claim
/// never touches a later one.</summary>
public sealed record OperationClaim(OperationClaimResult Result, string? Token = null);

/// <summary>
/// The execution claims of mutations (R1-18, INV-39): the gateway claims an operation id right before the mutation
/// runs and records how it ended. A second attempt under the same id — a concurrent retry, a retry after it ran, a
/// retry after a restart interrupted it — does not run. A claim given back before the mutation ran (refused after
/// the claim) lets the same id be used again.
/// </summary>
public interface IOperationJournal
{
    Task<OperationClaim> ClaimAsync(OperationClaimRequest claim, CancellationToken ct = default);

    /// <summary>Why <paramref name="operationId"/> could not run now, without claiming it; <c>null</c> when it is free
    /// (unknown, or given back). The gateway asks before an approval is raised or used, so a duplicate is refused as
    /// one instead of asking for another approval. The claim right before the run still decides.</summary>
    Task<OperationClaimResult?> InspectAsync(string operationId, string inputHash, CancellationToken ct = default);

    /// <summary>The claimed operation ended: <c>Executed</c> or <c>Failed</c>, with its verification. Only the claim
    /// named by <paramref name="token"/> is finished.</summary>
    Task FinishAsync(string operationId, string token, MutationStatus status, VerificationStatus verification,
        CancellationToken ct = default);

    /// <summary>The claimed operation did not run (refused after the claim): the id is free again. Only the claim
    /// named by <paramref name="token"/> is given back.</summary>
    Task ReleaseAsync(string operationId, string token, CancellationToken ct = default);
}

/// <summary>The journal in the Whiskers database (table <c>Operations</c>).</summary>
public sealed class DbOperationJournal : IOperationJournal
{
    internal const string Running = "Running", Executed = "Executed", Failed = "Failed", Released = "Released";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    /// <summary>This process: a <c>Running</c> claim of another one was cut off by a restart.</summary>
    internal string ProcessInstance { get; } = Guid.NewGuid().ToString("N");

    public DbOperationJournal(IServiceScopeFactory scopes, TimeProvider? time = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>What a retry under the same id must ask for again: the principal, the capability and the arguments
    /// (keys sorted, raw JSON; the gateway's own <c>__…</c> keys left out).</summary>
    public static string InputHashOf(MutationRequest request) =>
        ApprovalFingerprint.Of(request.Principal.Id, request.CapabilityId,
            request.Arguments.Where(a => !a.Key.StartsWith("__", StringComparison.Ordinal))
                .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));

    public async Task<OperationClaim> ClaimAsync(OperationClaimRequest claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        // This claim's token: the process, and the claim itself — so a retried insert whose commit answer was lost
        // recognises its own row.
        var token = $"{ProcessInstance}/{Guid.NewGuid():N}";
        var now = _time.GetUtcNow().UtcDateTime;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        try
        {
            db.Operations.Add(new OperationRecordEntity
            {
                Id = claim.OperationId,
                ParentId = claim.ParentId,
                CapabilityId = claim.CapabilityId,
                PrincipalId = claim.PrincipalId,
                InputHash = claim.InputHash,
                State = Running,
                Instance = token,
                DecisionId = claim.DecisionId,
                ApprovalId = claim.ApprovalId,
                AuditId = claim.AuditId,
                ClaimedAt = now,
            });
            await db.SaveChangesAsync(ct);
            return new OperationClaim(OperationClaimResult.Claimed, token);
        }
        catch (DbUpdateException)
        {
            // The id exists (or the write failed — then the read below fails too and the call is refused).
            db.ChangeTracker.Clear();
        }

        var existing = await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == claim.OperationId, ct)
                       ?? throw new InvalidOperationException("The operation could not be claimed.");
        if (existing.Instance == token) return new OperationClaim(OperationClaimResult.Claimed, token);
        if (Classify(existing, claim.InputHash) is { } refused) return new OperationClaim(refused);
        // Given back before it ran: the id is free again, for exactly one of any concurrent claimers.
        var taken = await db.Operations
            .Where(o => o.Id == claim.OperationId && o.State == Released && o.InputHash == claim.InputHash)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.State, Running)
                .SetProperty(o => o.Instance, token)
                .SetProperty(o => o.ParentId, claim.ParentId)
                .SetProperty(o => o.DecisionId, claim.DecisionId)
                .SetProperty(o => o.ApprovalId, claim.ApprovalId)
                .SetProperty(o => o.AuditId, claim.AuditId)
                .SetProperty(o => o.ClaimedAt, now), ct);
        return taken == 1
            ? new OperationClaim(OperationClaimResult.Claimed, token)
            : new OperationClaim(OperationClaimResult.AlreadyRunning);
    }

    public async Task<OperationClaimResult?> InspectAsync(string operationId, string inputHash, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var existing = await db.Operations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == operationId, ct);
        return existing is null ? null : Classify(existing, inputHash);
    }

    /// <summary>Why an existing row keeps the id from running; <c>null</c> when it was given back.</summary>
    private OperationClaimResult? Classify(OperationRecordEntity existing, string inputHash)
    {
        if (existing.InputHash != inputHash) return OperationClaimResult.Mismatch;
        switch (existing.State)
        {
            case Released:
                return null;
            case Running:
                return existing.Instance.StartsWith(ProcessInstance + "/", StringComparison.Ordinal)
                    ? OperationClaimResult.AlreadyRunning
                    : OperationClaimResult.OutcomeUnknown;
            default:
                return OperationClaimResult.AlreadyRun;
        }
    }

    public async Task FinishAsync(string operationId, string token, MutationStatus status, VerificationStatus verification,
        CancellationToken ct = default)
    {
        var state = status == MutationStatus.Executed ? Executed : Failed;
        var now = _time.GetUtcNow().UtcDateTime;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        await db.Operations
            .Where(o => o.Id == operationId && o.Instance == token && o.State == Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.State, state)
                .SetProperty(o => o.Verification, verification.ToString())
                .SetProperty(o => o.FinishedAt, now), ct);
    }

    public async Task ReleaseAsync(string operationId, string token, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        await db.Operations
            // Conditional on this claim: a release retried after its first commit (a lost answer, a provider retry)
            // finds the row taken by a later claim and leaves it alone.
            .Where(o => o.Id == operationId && o.Instance == token && o.State == Running)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.State, Released), ct);
    }
}
