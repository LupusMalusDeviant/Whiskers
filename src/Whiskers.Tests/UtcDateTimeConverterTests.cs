using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

// stableDB step 2: every DateTime must read back as DateTimeKind.Utc (Npgsql rejects Unspecified/Local
// for timestamptz). Verified here against SQLite; the same convention applies to Postgres.
public sealed class UtcDateTimeConverterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"utc-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public UtcDateTimeConverterTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task DateTime_is_normalized_to_utc_and_reads_back_as_utc_kind()
    {
        // Written as Local kind on purpose — the converter must normalize it to UTC.
        var local = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Local);

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.ServerMetrics.Add(new ServerMetricEntity { ServerId = "s1", ServerName = "srv", Timestamp = local });
            await db.SaveChangesAsync();
        }

        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var row = await db.ServerMetrics.SingleAsync();
            Assert.Equal(DateTimeKind.Utc, row.Timestamp.Kind);
            Assert.Equal(local.ToUniversalTime(), row.Timestamp);
        }
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }
}
