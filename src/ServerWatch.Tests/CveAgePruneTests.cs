using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerWatch.Models.Cve;
using ServerWatch.Services.Cve;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Tests;

public sealed class CveAgePruneTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cve-age-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public CveAgePruneTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    private void Seed(params CveFirstSeenEntity[] rows)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.CveFirstSeen.AddRange(rows);
        db.SaveChanges();
    }

    private List<string> AllKeys()
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>()
            .CveFirstSeen.Select(e => e.IdentityKey).ToList();
    }

    private CveAgeStore Store() => new(_sp.GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task DeletesStaleAndOld_KeepsLiveOrRecent()
    {
        var now = DateTime.UtcNow;
        Seed(
            new CveFirstSeenEntity { IdentityKey = "old-stale", CveId = "CVE-1", FirstSeenUtc = now.AddDays(-40) }, // delete: old + gone
            new CveFirstSeenEntity { IdentityKey = "old-live",  CveId = "CVE-2", FirstSeenUtc = now.AddDays(-40) }, // keep: still present
            new CveFirstSeenEntity { IdentityKey = "new-stale", CveId = "CVE-3", FirstSeenUtc = now.AddDays(-5) });  // keep: too recent

        await Store().PruneStaleAsync(new HashSet<string> { "old-live" }, now.AddDays(-30));

        var keys = AllKeys();
        Assert.DoesNotContain("old-stale", keys); // only the old + no-longer-present row is gone
        Assert.Contains("old-live", keys);
        Assert.Contains("new-stale", keys);
    }

    [Fact]
    public async Task AllLive_IsNoOp()
    {
        var now = DateTime.UtcNow;
        Seed(new CveFirstSeenEntity { IdentityKey = "k", CveId = "CVE-1", FirstSeenUtc = now.AddDays(-40) });

        await Store().PruneStaleAsync(new HashSet<string> { "k" }, now.AddDays(-30)); // "k" is live → keep

        Assert.Contains("k", AllKeys());
    }

    public void Dispose()
    {
        _sp.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
    }
}
