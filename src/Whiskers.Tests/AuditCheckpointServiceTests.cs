using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.AuditLog;
using Whiskers.Tests.ComponentTests;

namespace Whiskers.Tests;

/// <summary>R1-23: the end of the audit chain is signed at shutdown too, and a checkpoint that cannot be written never
/// takes the host down.</summary>
public sealed class AuditCheckpointServiceTests
{
    [Fact]
    public async Task Stopping_signs_the_end_of_the_chain()
    {
        var chain = AuditPagesAccessTests.Recording<IAuditChain>.Create();
        var service = new AuditCheckpointService((IAuditChain)(object)chain, NullLogger<AuditCheckpointService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.DisposeAsync();

        Assert.Equal([nameof(IAuditChain.CheckpointAsync)], chain.Calls);
    }

    [Fact]
    public async Task A_failing_checkpoint_at_shutdown_is_logged_not_thrown()
    {
        var service = new AuditCheckpointService(new Failing(), NullLogger<AuditCheckpointService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);   // does not throw
        await service.DisposeAsync();
    }

    [Fact]
    public async Task The_timer_signs_every_interval()
    {
        var time = new ManualTime();
        var chain = AuditPagesAccessTests.Recording<IAuditChain>.Create();
        var service = new AuditCheckpointService((IAuditChain)(object)chain, NullLogger<AuditCheckpointService>.Instance, time);
        await service.StartAsync(CancellationToken.None);

        time.Fire();
        time.Fire();

        Assert.Equal(2, chain.Calls.Count);
        Assert.Equal(AuditCheckpointService.Interval, time.Period);
        await service.DisposeAsync();
    }

    private sealed class Failing : IAuditChain
    {
        public Task<T> WriteAsync<T>(Whiskers.Services.Persistence.MetricsDbContext db,
            Func<CancellationToken, Task<(T Result, IReadOnlyCollection<long> AuditIds)>> write, CancellationToken ct = default) =>
            throw new InvalidOperationException();
        public Task SealExistingAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task CheckpointAsync(CancellationToken ct = default) => throw new InvalidOperationException("database is locked");
        public Task<AuditChainReport> VerifyAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<int> PruneAsync(DateTime cutoffUtc, int retentionDays, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<AuditChainReport> ExportAsync(Stream output, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    /// <summary>A clock whose single timer fires when the test says so.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan Period { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            Period = period;
            return new NoTimer();
        }

        public void Fire() => _callback!(_state);

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
