using Microsoft.EntityFrameworkCore;
using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Observability;

/// <summary>Records and queries every agent/MCP tool call for governance ("Agent History").
/// Writes through a scoped <see cref="MetricsDbContext"/>; safe to call from singletons.</summary>
public interface IMcpCallLogStore
{
    Task RecordAsync(McpToolCallEntity entry);

    Task<List<McpToolCallEntity>> GetRecentAsync(
        int count = 100, int offset = 0,
        string? actor = null, string? tool = null, string? verdict = null,
        bool writesOnly = false, DateTime? since = null);

    Task<int> CountAsync(
        string? actor = null, string? tool = null, string? verdict = null,
        bool writesOnly = false, DateTime? since = null);
}

public sealed class McpCallLogStore : IMcpCallLogStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<McpCallLogStore>? _logger;

    public McpCallLogStore(IServiceScopeFactory scopeFactory, ILogger<McpCallLogStore>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordAsync(McpToolCallEntity entry)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.McpToolCalls.Add(entry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to record MCP tool-call log entry");
        }
    }

    public async Task<List<McpToolCallEntity>> GetRecentAsync(
        int count = 100, int offset = 0,
        string? actor = null, string? tool = null, string? verdict = null,
        bool writesOnly = false, DateTime? since = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await Filter(db.McpToolCalls, actor, tool, verdict, writesOnly, since)
            .OrderByDescending(e => e.Timestamp)
            .Skip(offset)
            .Take(count)
            .ToListAsync();
    }

    public async Task<int> CountAsync(
        string? actor = null, string? tool = null, string? verdict = null,
        bool writesOnly = false, DateTime? since = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await Filter(db.McpToolCalls, actor, tool, verdict, writesOnly, since).CountAsync();
    }

    private static IQueryable<McpToolCallEntity> Filter(
        IQueryable<McpToolCallEntity> q,
        string? actor, string? tool, string? verdict, bool writesOnly, DateTime? since)
    {
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(e => e.Actor.Contains(actor));
        if (!string.IsNullOrWhiteSpace(tool)) q = q.Where(e => e.ToolName.Contains(tool));
        if (!string.IsNullOrWhiteSpace(verdict)) q = q.Where(e => e.Verdict == verdict);
        if (writesOnly) q = q.Where(e => e.Level != "read");
        if (since is { } s) q = q.Where(e => e.Timestamp >= s);
        return q;
    }
}
