using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Whiskers.Services.Auth;

namespace Whiskers.Hubs;

/// <summary>
/// R1-26 (INV-09): the open <see cref="ContainerHub"/> connections, re-checked every <see cref="SweepInterval"/>. The
/// server pushes to every connection (container list, CVE and image-update events) without the client calling
/// anything, so the per-call <see cref="SessionHubFilter"/> alone would let a revoked, silent connection keep receiving
/// them. A connection whose session no longer holds — or whose check throws — is closed and the revocation audited
/// (surface <c>hub</c>). A plain hosted service on a <see cref="TimeProvider"/> timer: it makes no Docker calls.
/// </summary>
public sealed class HubSessionSweep : IHostedService, IDisposable
{
    /// <summary>How often open connections are re-checked: well inside INV-09's 60 s.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, HubCallerContext> _open = new(StringComparer.Ordinal);
    private readonly ISessionValidator _sessions;
    private readonly TimeProvider _time;
    private readonly IAuthAuditLog? _audit;
    private readonly ILogger<HubSessionSweep>? _logger;
    private ITimer? _timer;
    private int _sweeping;

    public HubSessionSweep(ISessionValidator sessions, TimeProvider? time = null, IAuthAuditLog? audit = null,
        ILogger<HubSessionSweep>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _time = time ?? TimeProvider.System;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>The number of connections being watched.</summary>
    public int Count => _open.Count;

    public void Track(HubCallerContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _open[connection.ConnectionId] = connection;
    }

    public void Forget(string connectionId) => _open.TryRemove(connectionId, out _);

    /// <summary>Ends <paramref name="connection"/> for <paramref name="reason"/>: stops watching it, closes it, audits it.</summary>
    public async Task EndAsync(HubCallerContext connection, string reason)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Forget(connection.ConnectionId);
        _logger?.LogInformation("A hub connection's session was ended ({Reason})", reason);
        connection.Abort();
        if (_audit is not null)
            await _audit.SessionRevokedAsync(null, connection.User?.FindFirstValue(ClaimTypes.Email), "hub", reason);
    }

    /// <summary>Re-checks every open connection now; called by the timer, public for tests.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            foreach (var connection in _open.Values.ToList())
            {
                SessionCheck check;
                try
                {
                    check = await _sessions.CheckAsync(connection.User, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger?.LogError("A hub connection's session could not be re-checked ({Error}); it is closed",
                        ex.GetType().Name);
                    check = SessionCheck.Revoked(SessionRevocationReasons.StoreUnavailable);
                }
                if (!check.Valid)
                    await EndAsync(connection, check.Reason ?? SessionRevocationReasons.NotSignedIn);
            }
        }
        finally { Interlocked.Exchange(ref _sweeping, 0); }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = _time.CreateTimer(_ => _ = SweepSafelyAsync(), null, SweepInterval, SweepInterval);
        return Task.CompletedTask;
    }

    private async Task SweepSafelyAsync()
    {
        try { await SweepAsync(); }
        catch (Exception ex) { _logger?.LogError("The hub session sweep failed ({Error})", ex.GetType().Name); }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}
