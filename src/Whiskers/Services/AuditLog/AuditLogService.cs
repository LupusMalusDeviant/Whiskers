using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.AuditLog;

public class AuditLogService : IAuditLogService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditLogService> _logger;
    private readonly Whiskers.Services.Mutations.IAuditRecovery? _recovery;

    /// <param name="recovery">Told when a row is lost, so the <c>audit</c> health check shows the trail is incomplete
    /// (R1-19).</param>
    public AuditLogService(IServiceScopeFactory scopeFactory, ILogger<AuditLogService> logger,
        Whiskers.Services.Mutations.IAuditRecovery? recovery = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _recovery = recovery;
    }

    public Task LogAsync(string actor, string actorType, string action,
                         string targetType, string targetId, string targetName,
                         string? details = null, string? serverId = null, bool success = true) =>
        TryLogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

    public Task<bool> TryLogAsync(string actor, string actorType, string action,
                                  string targetType, string targetId, string targetName,
                                  string? details = null, string? serverId = null, bool success = true) =>
        TryLogAtAsync(DateTime.UtcNow, actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

    public async Task<bool> TryLogAtAsync(DateTime occurredUtc, string actor, string actorType, string action,
                                          string targetType, string targetId, string targetName,
                                          string? details = null, string? serverId = null, bool success = true)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

            db.AuditLog.Add(new AuditLogEntity
            {
                Timestamp = occurredUtc,
                Actor = actor,
                ActorType = actorType,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                TargetName = targetName,
                Details = details,
                ServerId = serverId,
                Success = success
            });

            await db.SaveChangesAsync();

            _logger.LogInformation("Audit: [{ActorType}] {Actor} → {Action} on {TargetType}/{TargetName} (success={Success})",
                actorType, actor, action, targetType, targetName, success);
            return true;
        }
        catch (Exception ex)
        {
            // Fail-safe: the audit backend is down (DB locked, disk full) but the destructive action already
            // happened — the full entry must survive in the app log instead of vanishing. Callers never put
            // raw secrets into `details` (key names / redacted commands only), so echoing it here is safe.
            _logger.LogError(ex,
                "AUDIT WRITE FAILED: [{ActorType}] {Actor} → {Action} on {TargetType}/{TargetName} " +
                "(target={TargetId}, server={ServerId}, success={Success}, details={Details})",
                actorType, actor, action, targetType, targetName, targetId, serverId, success, details);
            _recovery?.LegacyWriteFailed();
            return false;
        }
    }

    public async Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0,
                                                            string? actionFilter = null,
                                                            string? targetTypeFilter = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        IQueryable<AuditLogEntity> query = db.AuditLog;

        if (!string.IsNullOrEmpty(actionFilter))
            query = query.Where(e => e.Action.StartsWith(actionFilter));

        if (!string.IsNullOrEmpty(targetTypeFilter))
            query = query.Where(e => e.TargetType == targetTypeFilter);

        return await query
            .OrderByDescending(e => e.Timestamp)
            .Skip(offset)
            .Take(count)
            .ToListAsync();
    }

    public async Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        IQueryable<AuditLogEntity> query = db.AuditLog;

        if (!string.IsNullOrEmpty(actionFilter))
            query = query.Where(e => e.Action.StartsWith(actionFilter));

        if (!string.IsNullOrEmpty(targetTypeFilter))
            query = query.Where(e => e.TargetType == targetTypeFilter);

        return await query.CountAsync();
    }
}
