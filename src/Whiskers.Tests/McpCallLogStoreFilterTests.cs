using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Observability;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>The actor/tool filters of the MCP call log ignore case on every provider. They used
/// <c>Contains</c>, which translates to case-sensitive <c>instr()</c>/<c>strpos()</c>, so searching the log for
/// "Admin@…" missed every call made as "admin@…".</summary>
public sealed class McpCallLogStoreFilterTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"mcplog-{Guid.NewGuid():N}.db");

    private McpCallLogStore NewStore()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_db}"), ServiceLifetime.Transient);
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
        return new McpCallLogStore(sp.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task Actor_and_tool_filters_ignore_case()
    {
        var store = NewStore();
        await store.RecordAsync(new McpToolCallEntity { Timestamp = DateTime.UtcNow, Actor = "admin@example.org", ActorType = "key", ToolName = "list_servers" });
        await store.RecordAsync(new McpToolCallEntity { Timestamp = DateTime.UtcNow, Actor = "agent", ActorType = "agent", ToolName = "restart_container", Level = "write" });

        Assert.Equal(1, await store.CountAsync(actor: "Admin@Example"));
        Assert.Equal(1, await store.CountAsync(tool: "RESTART"));
        Assert.Equal(0, await store.CountAsync(actor: "nobody"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
    }
}
