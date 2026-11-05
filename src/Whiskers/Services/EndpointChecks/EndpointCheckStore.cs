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
    Task DeleteAsync(string id, CancellationToken ct);

    /// <summary>Switches a check off because its owner lost the authority to run it — only while it still has
    /// <paramref name="expectedOwnerId"/> and is on. False when an Admin took it over meanwhile, or it is gone.</summary>
    Task<bool> SuspendAsync(string id, string expectedOwnerId, string reason, CancellationToken ct = default);

    /// <summary>Records one run and the check's last outcome; nothing when the check no longer exists.</summary>
    Task RecordAsync(EndpointCheckRun run, CancellationToken ct = default);

    /// <summary>The latest results of a check, newest first.</summary>
    IReadOnlyList<EndpointCheckResultEntity> Results(string checkId, int take = 50);

    /// <summary>The newest result of every check that has one.</summary>
    IReadOnlyDictionary<string, EndpointCheckResultEntity> LastResults();

    /// <summary>Deletes results older than <paramref name="cutoffUtc"/>, except each check's newest one, and every
    /// result whose check no longer exists.</summary>
    Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken ct = default);
}

/// <inheritdoc cref="IEndpointCheckStore"/>
public sealed class EndpointCheckStore : IEndpointCheckStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IServerConfigService _servers;
    private readonly TimeProvider _time;

    public EndpointCheckStore(IServiceScopeFactory scopes, IServerConfigService servers, TimeProvider? time = null)
    {
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
        entity.Enabled = draft.Enabled;
        entity.SuspendedReason = null;
        entity.OwnerId = editorId;
        entity.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        return EndpointCheckDefinition.From(entity);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = Db(scope);
        await db.EndpointCheckResults.Where(r => r.CheckId == id).ExecuteDeleteAsync(ct);
        await db.EndpointChecks.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> SuspendAsync(string id, string expectedOwnerId, string reason, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var now = _time.GetUtcNow().UtcDateTime;
        return await Db(scope).EndpointChecks
            .Where(c => c.Id == id && c.Enabled && c.OwnerId == expectedOwnerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Enabled, false)
                .SetProperty(c => c.SuspendedReason, reason)
                .SetProperty(c => c.UpdatedUtc, now), ct) > 0;
    }

    public async Task RecordAsync(EndpointCheckRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
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

    private static MetricsDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
}
