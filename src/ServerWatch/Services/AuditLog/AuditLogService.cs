using Microsoft.EntityFrameworkCore;
using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.AuditLog;

public class AuditLogService : IAuditLogService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(IServiceScopeFactory scopeFactory, ILogger<AuditLogService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LogAsync(string actor, string actorType, string action,
                                string targetType, string targetId, string targetName,
                                string? details = null, string? serverId = null, bool success = true)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

            db.AuditLog.Add(new AuditLogEntity
            {
                Timestamp = DateTime.UtcNow,
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit log entry");
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
