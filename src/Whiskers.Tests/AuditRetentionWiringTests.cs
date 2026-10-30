using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Metrics;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>R1-23: the metrics loop runs audit retention once a day, with the configured (floored) period, through the
/// chain; and an export's scratch files left by a crash are swept.</summary>
public sealed class AuditRetentionWiringTests
{
    [Fact]
    public async Task Retention_runs_once_a_day_with_the_configured_period()
    {
        var chain = new PruneRecorder();
        var services = new ServiceCollection()
            .AddSingleton<IAuditChain>(chain)
            .AddSingleton<IOptionsMonitor<AuditSettings>>(new Fixed(new AuditSettings { RetentionDays = 40 }))
            .BuildServiceProvider();
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

        var last = await MetricsCollectorService.PruneAuditIfDueAsync(services, null!, now, DateTime.MinValue, default);
        Assert.Equal(now, last);
        Assert.Equal((now.AddDays(-40), 40), Assert.Single(chain.Calls));

        // Within the day: nothing; a day later: again.
        Assert.Equal(last, await MetricsCollectorService.PruneAuditIfDueAsync(services, null!, now.AddHours(23), last, default));
        Assert.Single(chain.Calls);
        await MetricsCollectorService.PruneAuditIfDueAsync(services, null!, now.AddDays(1).AddMinutes(1), last, default);
        Assert.Equal(2, chain.Calls.Count);
    }

    [Fact]
    public async Task A_period_below_the_floor_is_raised_to_it()
    {
        var chain = new PruneRecorder();
        var services = new ServiceCollection()
            .AddSingleton<IAuditChain>(chain)
            .AddSingleton<IOptionsMonitor<AuditSettings>>(new Fixed(new AuditSettings { RetentionDays = 3 }))
            .BuildServiceProvider();

        await MetricsCollectorService.PruneAuditIfDueAsync(services, null!, DateTime.UtcNow, DateTime.MinValue, default);

        Assert.Equal(AuditSettings.MinRetentionDays, Assert.Single(chain.Calls).Days);
    }

    [Fact]
    public void Stale_export_scratch_files_are_swept_and_fresh_ones_kept()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"audit-export-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var stale = Path.Combine(dir, AuditExport.TempPrefix + "old.tmp");
            var fresh = Path.Combine(dir, AuditExport.TempPrefix + "new.tmp");
            var other = Path.Combine(dir, "backup-part.tmp");
            foreach (var file in new[] { stale, fresh, other }) File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));

            AuditExport.SweepStale(dir, DateTime.UtcNow);

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(other));   // not an export's
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Fixed(AuditSettings value) : IOptionsMonitor<AuditSettings>
    {
        public AuditSettings CurrentValue => value;
        public AuditSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<AuditSettings, string?> listener) => null;
    }

    private sealed class PruneRecorder : IAuditChain
    {
        public List<(DateTime Cutoff, int Days)> Calls { get; } = [];

        public Task<int> PruneAsync(DateTime cutoffUtc, int retentionDays, CancellationToken ct = default)
        {
            Calls.Add((cutoffUtc, retentionDays));
            return Task.FromResult(0);
        }

        public Task<T> WriteAsync<T>(MetricsDbContext db,
            Func<CancellationToken, Task<(T Result, IReadOnlyCollection<long> AuditIds)>> write, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<T> UpdateAsync<T>(MetricsDbContext db, IReadOnlyCollection<long> rowIds,
            Func<CancellationToken, Task<(T Result, IReadOnlyCollection<long> AuditIds)>> write, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SealExistingAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task CheckpointAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuditChainReport> VerifyAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuditChainReport> ExportAsync(Stream output, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
