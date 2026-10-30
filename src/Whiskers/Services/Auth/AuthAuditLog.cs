using System.Security.Claims;
using System.Threading.Channels;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Auth;

/// <summary>Writes sign-in events in the background: a call only queues the row, so a slow or locked audit database
/// never delays a sign-in decision (R1-22). The queue is bounded; a row it cannot take, or one the database loses,
/// is logged and marks the audit trail incomplete (<see cref="IAuditRecovery"/>).</summary>
public sealed class AuthAuditLog : IHostedService, IAuthAuditLog
{
    /// <summary>The audit target type of every sign-in event.</summary>
    public const string Target = "auth";

    public const string LoginAction = "auth.login";
    public const string LoginFailedAction = "auth.login_failed";
    public const string LogoutAction = "auth.logout";

    /// <summary>Rows waiting to be written; beyond this a row is dropped (and reported), never waited for.</summary>
    public const int Capacity = 1024;

    /// <summary>A queued row keeps the time it happened: a stalled write must not move it behind later events.</summary>
    private sealed record Row(string Actor, string Action, string Method, string Details, bool Success)
    {
        public DateTime OccurredUtc { get; } = DateTime.UtcNow;
    }

    private readonly IAuditLogService _audit;
    private readonly ILogger<AuthAuditLog> _logger;
    private readonly IAuditRecovery? _recovery;
    private readonly CancellationTokenSource _abort = new();
    private Task? _writer;
    private bool _stopped;   // set before the queue is completed: a later row is dropped because of the shutdown
    private readonly Channel<Row> _queue = Channel.CreateBounded<Row>(
        // Wait mode: TryWrite then answers false when the queue is full (DropWrite would report the dropped row as
        // written), so a lost row is always logged and reported.
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public AuthAuditLog(IAuditLogService audit, ILogger<AuthAuditLog> logger, IAuditRecovery? recovery = null)
    {
        _audit = audit;
        _logger = logger;
        _recovery = recovery;
    }

    public Task SignedInAsync(HttpContext context, string method, string email) =>
        Enqueue(new Row(email, LoginAction, method, Details(context, null), true));

    public Task SignInRefusedAsync(HttpContext context, string method, string? account, string reason) =>
        Enqueue(new Row(string.IsNullOrWhiteSpace(account) ? "anonymous" : account, LoginFailedAction, method,
            Details(context, reason), false));

    public Task SignedOutAsync(HttpContext context)
    {
        var email = context.User.FindFirstValue(ClaimTypes.Email);
        // Nothing to record for a visitor who was not signed in.
        if (string.IsNullOrEmpty(email)) return Task.CompletedTask;
        return Enqueue(new Row(email, LogoutAction, context.User.Identity?.AuthenticationType ?? "", Details(context, null), true));
    }

    private Task Enqueue(Row row)
    {
        if (!_queue.Writer.TryWrite(row))
        {
            _logger.LogError("AUDIT WRITE FAILED: sign-in audit queue {State}, dropped [web] {Actor} → {Action} ({Method}, success={Success}, {Details})",
                Volatile.Read(ref _stopped) ? "stopped" : "full", row.Actor, row.Action, row.Method, row.Success, row.Details);
            _recovery?.LegacyWriteFailed();
        }
        return Task.CompletedTask;
    }

    /// <summary>How long a shutdown waits for queued rows to be written.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    // A plain hosted service, not a BackgroundService: it makes no Docker calls, so it needs no fleet lane.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _writer = Task.Run(() => WriteAsync(_abort.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task WriteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Reads until StopAsync completes the queue, so what is queued is still written on a clean shutdown.
            await foreach (var row in _queue.Reader.ReadAllAsync(CancellationToken.None))
                // TryLogAtAsync never throws: a lost row is logged and reported to the audit health check there. A write
                // that hangs is abandoned once the shutdown's drain time is over.
                await _audit.TryLogAtAsync(row.OccurredUtc, row.Actor, "web", row.Action, Target, row.Method, row.Method,
                    row.Details, success: row.Success).WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogError("AUDIT WRITE FAILED: sign-in audit rows still queued at shutdown were not written.");
            _recovery?.LegacyWriteFailed();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopped, true);
        _queue.Writer.TryComplete();
        if (_writer is not { } running) return;
        try { await running.WaitAsync(DrainTimeout, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        await _abort.CancelAsync().ConfigureAwait(false);
        await running.ConfigureAwait(false);
    }

    /// <summary>The client address (after the forwarded-headers middleware) and, for a refusal, the reason.</summary>
    private static string Details(HttpContext context, string? reason)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return reason is null ? $"ip={ip}" : $"reason={reason}; ip={ip}";
    }
}
