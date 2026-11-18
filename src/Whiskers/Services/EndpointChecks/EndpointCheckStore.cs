using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.EndpointChecks;

/// <summary>R1-57 (contract 0004): the stored checks and their results. Every write of a definition is a gated
/// operation; recording a run and pruning are Whiskers' own bookkeeping.</summary>
public interface IEndpointCheckStore
{
    IReadOnlyList<EndpointCheckDefinition> List(string? serverId = null);
    EndpointCheckDefinition? Get(string id);

    /// <summary>Creates or changes a check. <paramref name="editorId"/> (<c>user:&lt;email&gt;</c>, the signed-in Admin)
    /// becomes its owner, and a suspension ends — saving is a takeover. Throws <see cref="ArgumentException"/> with
    /// every reason when the draft does not validate; nothing is written then.</summary>
    [MutatingOperation("endpointcheck.save")]
    Task<EndpointCheckDefinition> SaveAsync(EndpointCheckDraft draft, string editorId, CancellationToken ct);

    /// <summary>Deletes a check and its results.</summary>
    [MutatingOperation("endpointcheck.delete")]
    Task DeleteAsync([MutationTarget] string id, CancellationToken ct);

    /// <summary>Switches a check off because its owner lost the authority to run it — only while it still has
    /// <paramref name="expectedOwnerId"/> and is on. False when an Admin took it over meanwhile, or it is gone.</summary>
    Task<bool> SuspendAsync(string id, string expectedOwnerId, string reason, CancellationToken ct = default);

    /// <summary>Records one run and the check's last outcome; nothing when the check no longer exists or nothing ran
    /// (<see cref="EndpointCheckRun.NothingRan"/>). Only a monitor run moves the schedule (<c>LastRunUtc</c>); a
    /// "Run now" result (<see cref="EndpointCheckRun.OnDemand"/>) does not.</summary>
    Task RecordAsync(EndpointCheckRun run, CancellationToken ct = default);

    /// <summary>The latest results of a check, newest first.</summary>
    IReadOnlyList<EndpointCheckResultEntity> Results(string checkId, int take = 50);

    /// <summary>The newest result of every check that has one.</summary>
    IReadOnlyDictionary<string, EndpointCheckResultEntity> LastResults();

    /// <summary>Deletes results older than <paramref name="cutoffUtc"/>, except each check's newest one, and every
    /// result whose check no longer exists.</summary>
    Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken ct = default);

    /// <summary>Contract 0020: <c>OwnerId := to</c> only while it is <paramref name="fromOwnerId"/>; <c>Enabled := false</c>;
    /// <c>SuspendedReason :=</c> the handover text. Called only from <c>AutomationHandover</c>.</summary>
    [MutatingOperation("automation.handover")]
    Task<HandoverWrite> HandOverAsync(string checkId, string fromOwnerId, string toOwnerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot hand a check over.");

    /// <summary>Contract 0020 (D11 c): off with the hold text only while the owner is <paramref name="ownerId"/>; the
    /// owner stays. Inside <c>account.create</c> only.</summary>
    [MutatingOperation("automation.hold")]
    Task<HandoverWrite> HoldAsync(string checkId, string ownerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot hold a check.");

    /// <summary>Contract 0020 (D7): <c>Enabled := true</c>, <c>SuspendedReason := null</c>, only while the owner is
    /// <paramref name="expectedOwnerId"/>; the owner is kept (a resume never re-owns). False when the check is gone, has
    /// another owner, or is on.</summary>
    [MutatingOperation("endpointcheck.save")]
    Task<bool> ResumeAsync(string id, string expectedOwnerId, CancellationToken ct) =>
        throw new NotSupportedException($"{GetType().Name} cannot resume a check.");
}

/// <inheritdoc cref="IEndpointCheckStore"/>
public sealed class EndpointCheckStore : IEndpointCheckStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IServerConfigService _servers;
    private readonly TimeProvider _time;

    // Contract 0020: the first-run ledger of handed-over checks.
    private readonly IHandoverLedger? _handovers;

    public EndpointCheckStore(IServiceScopeFactory scopes, IServerConfigService servers, TimeProvider? time = null,
        IHandoverLedger? handovers = null)
    {
        _handovers = handovers;
        _scopes = scopes;
        _servers = servers;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<EndpointCheckDefinition> List(string? serverId = null)
    {
        using var scope = _scopes.CreateScope();
        var db = Db(scope);
        var query = db.EndpointChecks.AsNoTracking();
        if (!string.IsNullOrEmpty(serverId)) query = query.Where(c => c.ServerId == serverId);
        return query.ToList().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(EndpointCheckDefinition.From).ToList();
    }

    public EndpointCheckDefinition? Get(string id)
    {
        using var scope = _scopes.CreateScope();
        var entity = Db(scope).EndpointChecks.AsNoTracking().FirstOrDefault(c => c.Id == id);
        return entity is null ? null : EndpointCheckDefinition.From(entity);
    }

    public async Task<EndpointCheckDefinition> SaveAsync(EndpointCheckDraft draft, string editorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (PrincipalIds.EmailOf(editorId) is null)
            throw new UnauthorizedAccessException("An endpoint check needs a signed-in Admin to own it.");
        var errors = EndpointCheckValidator.Validate(draft, _servers.GetServer(draft.ServerId));
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var exceptions = (draft.Exceptions ?? []).Select(e => Notifications.OutboundException.Parse(e).ToEntry()).ToList();

        using var scope = _scopes.CreateScope();
        var db = Db(scope);
        var now = _time.GetUtcNow().UtcDateTime;
        EndpointCheckEntity entity;
        if (string.IsNullOrEmpty(draft.Id))
        {
            entity = new EndpointCheckEntity { CreatedUtc = now };
            db.EndpointChecks.Add(entity);
        }
        else
        {
            entity = await db.EndpointChecks.FirstOrDefaultAsync(c => c.Id == draft.Id, ct)
                     ?? throw new KeyNotFoundException("The endpoint check no longer exists.");
            // A check stays on its server: moving it would change which host runs its commands under an id the audit
            // trail already knows. Delete and create instead.
            if (!string.Equals(entity.ServerId, draft.ServerId, StringComparison.Ordinal))
                throw new ArgumentException("A check cannot move to another server; create a new one there.");
        }

        entity.ServerId = draft.ServerId;
        entity.Name = draft.Name.Trim();
        entity.Kind = draft.Kind;
        entity.Target = draft.Target.Trim();
        entity.Port = draft.Port;
        entity.ExpectedStatus = draft.Kind == EndpointCheckKind.Http ? draft.ExpectedStatus : null;
        entity.TlsWarnDays = draft.Kind == EndpointCheckKind.TlsExpiry ? draft.TlsWarnDays : null;
        entity.IntervalSeconds = draft.IntervalSeconds;
        entity.TimeoutSeconds = draft.TimeoutSeconds;
        entity.ExceptionsJson = EndpointCheckDefinition.WriteExceptions(exceptions);
        var previousOwner = string.IsNullOrEmpty(draft.Id) ? null : entity.OwnerId;
        entity.Enabled = draft.Enabled;
        entity.SuspendedReason = null;
        entity.OwnerId = editorId;
        entity.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        // Contract 0020: a save is a takeover (it re-owns); a handover's note ends with it unless the owner stays.
        if (previousOwner is not null && previousOwner != editorId)
            HandoverNotes.ForgetQuietly(_handovers, ItemOf(entity.Id));
        return EndpointCheckDefinition.From(entity);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = Db(scope);
        await db.EndpointCheckResults.Where(r => r.CheckId == id).ExecuteDeleteAsync(ct);
        await db.EndpointChecks.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        HandoverNotes.ForgetQuietly(_handovers, ItemOf(id));   // contract 0020
    }

    public async Task<bool> SuspendAsync(string id, string expectedOwnerId, string reason, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var now = _time.GetUtcNow().UtcDateTime;
        return await Db(scope).EndpointChecks
            .Where(c => c.Id == id && c.Enabled && c.OwnerId == expectedOwnerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Enabled, false)
                // A bulk update passes the save interceptor by: redacted here (INV-51, contract 0017 C1).
                .SetProperty(c => c.SuspendedReason, Whiskers.Utils.SinkRedaction.Text(reason))
                .SetProperty(c => c.UpdatedUtc, now), ct) > 0;
    }

    public async Task RecordAsync(EndpointCheckRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        // Review n1: "Run now" of a check that is gone or off ran nothing; there is nothing to record.
        if (run.NothingRan) return;
        using var scope = _scopes.CreateScope();
        var db = Db(scope);
        // Review m8: a check deleted while it ran leaves no result behind (there is no foreign key to clean it up, and
        // the newest result of a check is kept by the pruning).
        if (!await db.EndpointChecks.AnyAsync(c => c.Id == run.CheckId, ct)) return;
        db.EndpointCheckResults.Add(new EndpointCheckResultEntity
        {
            CheckId = run.CheckId,
            AtUtc = run.AtUtc,
            OperationId = run.OperationId,
            Outcome = run.Outcome,
            Detail = EndpointCheckRunner.Bounded(run.Detail),
            LatencyMs = run.LatencyMs is { } latency ? (int)Math.Min(latency, int.MaxValue) : null,
            HttpStatus = run.HttpStatus,
            TlsNotAfterUtc = run.TlsNotAfterUtc,
            AnsweredAddress = run.AnsweredAddress,
            AddressesTried = run.AddressesTried,
            AddressesNotTried = run.AddressesNotTried,
        });
        await db.SaveChangesAsync(ct);
        // Review n2: LastRunUtc is the monitor's schedule. A "Run now" result does not move it — otherwise repeated
        // clicks would keep the check from ever being due, the alert would get no readings, and its gap rule would close
        // an open endpoint_check_failed as unconfirmed.
        if (run.OnDemand)
            await db.EndpointChecks.Where(c => c.Id == run.CheckId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastOutcome, run.Outcome), ct);
        else
            await db.EndpointChecks.Where(c => c.Id == run.CheckId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastRunUtc, run.AtUtc).SetProperty(c => c.LastOutcome, run.Outcome), ct);
    }

    public IReadOnlyList<EndpointCheckResultEntity> Results(string checkId, int take = 50)
    {
        using var scope = _scopes.CreateScope();
        return Db(scope).EndpointCheckResults.AsNoTracking().Where(r => r.CheckId == checkId)
            .OrderByDescending(r => r.AtUtc).ThenByDescending(r => r.Id).Take(Math.Clamp(take, 1, 500)).ToList();
    }

    public IReadOnlyDictionary<string, EndpointCheckResultEntity> LastResults()
    {
        using var scope = _scopes.CreateScope();
        var db = Db(scope);
        var lastIds = db.EndpointCheckResults.AsNoTracking().GroupBy(r => r.CheckId).Select(g => g.Max(r => r.Id)).ToList();
        return db.EndpointCheckResults.AsNoTracking().Where(r => lastIds.Contains(r.Id)).ToList()
            .ToDictionary(r => r.CheckId, StringComparer.Ordinal);
    }

    public async Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = Db(scope);
        // Results whose check is gone go whatever their age (review m8); of the others each check's newest is kept.
        var orphans = await db.EndpointCheckResults.Where(r => !db.EndpointChecks.Any(c => c.Id == r.CheckId))
            .ExecuteDeleteAsync(ct);
        var keep = await db.EndpointCheckResults.GroupBy(r => r.CheckId).Select(g => g.Max(r => r.Id)).ToListAsync(ct);
        return orphans + await db.EndpointCheckResults.Where(r => r.AtUtc < cutoffUtc && !keep.Contains(r.Id)).ExecuteDeleteAsync(ct);
    }


    public Task<HandoverWrite> HandOverAsync(string checkId, string fromOwnerId, string toOwnerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toOwnerId);
        return SwitchOffAsync(checkId, fromOwnerId, toOwnerId, HandoverReason, ct);
    }

    public Task<HandoverWrite> HoldAsync(string checkId, string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return SwitchOffAsync(checkId, ownerId, ownerId, HoldReason, ct);
    }

    /// <summary>Contract 0020: the suspension text of a handover and of an <c>account.create</c> hold.</summary>
    public const string HandoverReason = "handed over: off until an Admin resumes it";
    public const string HoldReason = "held when a local account was created for its owner's address: off until an Admin resumes it";

    /// <summary>Contract 0020: owner <paramref name="from"/> → <paramref name="to"/> (the same for a hold), off, with
    /// the reason; one conditional update on the state read, so "was on" is what it replaced.</summary>
    private async Task<HandoverWrite> SwitchOffAsync(string checkId, string from, string to, string reason, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = Db(scope);
        var now = _time.GetUtcNow().UtcDateTime;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = await db.EndpointChecks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == checkId, ct);
            if (before is null) return new(HandoverItemStatus.SkippedGone, checkId, false);
            if (before.OwnerId != from) return new(HandoverItemStatus.SkippedOwnerChanged, before.Name, before.Enabled);
            var wasOn = before.Enabled;
            var changed = await db.EndpointChecks
                .Where(c => c.Id == checkId && c.OwnerId == from && c.Enabled == wasOn)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.OwnerId, to)
                    .SetProperty(c => c.Enabled, false)
                    // A bulk update passes the save interceptor by: redacted here (INV-51).
                    .SetProperty(c => c.SuspendedReason, Whiskers.Utils.SinkRedaction.Text(reason))
                    .SetProperty(c => c.UpdatedUtc, now), ct);
            if (changed == 1) return new(HandoverItemStatus.HandedOver, before.Name, wasOn);
        }
        throw new InvalidOperationException("The check kept changing while it was handed over; nothing was written.");
    }

    public async Task<bool> ResumeAsync(string id, string expectedOwnerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerId);
        using var scope = _scopes.CreateScope();
        var now = _time.GetUtcNow().UtcDateTime;
        // The owner is kept: a resume never re-owns (D7).
        var resumed = await Db(scope).EndpointChecks
            .Where(c => c.Id == id && c.OwnerId == expectedOwnerId && !c.Enabled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Enabled, true)
                .SetProperty(c => c.SuspendedReason, (string?)null)
                .SetProperty(c => c.UpdatedUtc, now), ct) == 1;
        if (resumed) HandoverNotes.RecordResumeInScope(_handovers, ItemOf(id));
        return resumed;
    }

    private static AutomationItemRef ItemOf(string checkId) => new(AutomationKind.EndpointCheck, checkId);

    private static MetricsDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
}
