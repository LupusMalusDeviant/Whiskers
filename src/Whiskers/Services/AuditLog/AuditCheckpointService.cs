namespace Whiskers.Services.AuditLog;

/// <summary>Signs the end of the audit chain every minute and at shutdown (R1-23, ADR-0006): events written after
/// the newest checkpoint are the only ones a cut-off end can lose without notice. A plain hosted service, not a
/// BackgroundService: it makes no Docker calls, so it needs no fleet lane.</summary>
public sealed class AuditCheckpointService : IHostedService, IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IAuditChain _chain;
    private readonly ILogger<AuditCheckpointService> _logger;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private int _running;

    public AuditCheckpointService(IAuditChain chain, ILogger<AuditCheckpointService> logger, TimeProvider? time = null)
    {
        _chain = chain;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = _time.CreateTimer(_ => _ = TickAsync(), null, Interval, Interval);
        return Task.CompletedTask;
    }

    private async Task TickAsync()
    {
        // One at a time: a slow database never stacks checkpoints up.
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try { await _chain.CheckpointAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "AUDIT CHECKPOINT NOT WRITTEN"); }
        finally { Volatile.Write(ref _running, 0); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_timer is not null) await _timer.DisposeAsync();
        _timer = null;
        try { await _chain.CheckpointAsync(cancellationToken); }
        catch (Exception ex) { _logger.LogError(ex, "AUDIT CHECKPOINT NOT WRITTEN at shutdown"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null) await _timer.DisposeAsync();
    }
}
