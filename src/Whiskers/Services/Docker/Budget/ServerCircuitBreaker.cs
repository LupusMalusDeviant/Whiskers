using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Docker.Budget;

/// <summary>
/// Per-server circuit breaker (Plan-0001 WP4). Closed → Open after a run of transport failures → HalfOpen
/// after a cooldown, one probe → Closed on success.
///
/// <para>Resolves <see cref="INotificationService"/> lazily from the root provider rather than taking it in
/// the constructor: the notification composite sits far above the Docker layer, and a direct dependency here
/// would tie the two together at startup. Same reason <c>AiTriggerDispatcher</c> does it.</para>
/// </summary>
public sealed class ServerCircuitBreaker : IServerCircuitBreaker
{
    private sealed class Circuit
    {
        public readonly object Gate = new();
        public ServerCircuitState State = ServerCircuitState.Closed;
        public int ConsecutiveFailures;
        public DateTime? OpenedAt;
        public string? LastReason;
        public bool ProbeInFlight;
    }

    private readonly ConcurrentDictionary<string, Circuit> _circuits = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<ServerBudgetSettings> _settings;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ServerCircuitBreaker> _logger;

    public ServerCircuitBreaker(
        IOptionsMonitor<ServerBudgetSettings> settings,
        IServiceProvider serviceProvider,
        ILogger<ServerCircuitBreaker> logger)
    {
        _settings = settings;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public void ThrowIfOpen(string serverId)
    {
        var c = _circuits.GetOrAdd(serverId, _ => new Circuit());
        var cooldown = TimeSpan.FromSeconds(Math.Max(1, _settings.CurrentValue.CircuitCooldownSeconds));

        lock (c.Gate)
        {
            if (c.State == ServerCircuitState.Closed) return;

            if (c.State == ServerCircuitState.Open)
            {
                if (c.OpenedAt is { } opened && DateTime.UtcNow - opened >= cooldown)
                {
                    // Cooldown elapsed: promote to half-open and let THIS caller be the probe.
                    c.State = ServerCircuitState.HalfOpen;
                    c.ProbeInFlight = true;
                    _logger.LogInformation("Circuit for server {ServerId} half-open — letting one probe through", serverId);
                    return;
                }
                throw new ServerCircuitOpenException(serverId, c.LastReason);
            }

            // HalfOpen: exactly one probe at a time, everyone else keeps failing fast.
            if (c.ProbeInFlight) throw new ServerCircuitOpenException(serverId, c.LastReason);
            c.ProbeInFlight = true;
        }
    }

    public void RecordSuccess(string serverId)
    {
        var c = _circuits.GetOrAdd(serverId, _ => new Circuit());
        bool reopened;

        lock (c.Gate)
        {
            c.ProbeInFlight = false;
            c.ConsecutiveFailures = 0;
            reopened = c.State != ServerCircuitState.Closed;
            if (!reopened) return;

            c.State = ServerCircuitState.Closed;
            c.OpenedAt = null;
            c.LastReason = null;
        }

        _logger.LogInformation("Circuit for server {ServerId} closed — calls resumed", serverId);
        Announce(serverId, "server_throttling_ended", "Whiskers resumed its calls to this server after it answered again.");
    }

    public void RecordFailure(string serverId, Exception exception)
    {
        // A circuit that opens on application-level errors would trip on a healthy host — only transport
        // failures and our own timeouts say anything about reachability. Such an error still ends a probe,
        // though: returning with the slot taken left the circuit half-open with nobody allowed through.
        if (!IsHealthSignal(exception))
        {
            ReleaseProbe(serverId);
            return;
        }

        var c = _circuits.GetOrAdd(serverId, _ => new Circuit());
        var threshold = Math.Max(1, _settings.CurrentValue.CircuitFailureThreshold);
        bool justOpened;
        int failures;

        lock (c.Gate)
        {
            c.ProbeInFlight = false;
            failures = ++c.ConsecutiveFailures;
            // ONLY the step out of Closed is news. Coming from HalfOpen means the probe failed, which is the
            // circuit doing its job — announcing that again would send a second "server throttled" for a
            // server everyone already knows is throttled, every cooldown, for as long as it stays down.
            justOpened = c.State == ServerCircuitState.Closed && failures >= threshold;

            if (justOpened)
            {
                c.State = ServerCircuitState.Open;
                c.OpenedAt = DateTime.UtcNow;
                c.LastReason = exception.GetType().Name + ": " + exception.Message;
            }
            else if (c.State == ServerCircuitState.HalfOpen)
            {
                // The probe failed — back to open, and restart the cooldown.
                c.State = ServerCircuitState.Open;
                c.OpenedAt = DateTime.UtcNow;
            }
        }

        if (!justOpened) return;

        var cooldown = Math.Max(1, _settings.CurrentValue.CircuitCooldownSeconds);
        _logger.LogWarning("Circuit for server {ServerId} OPEN after {Failures} consecutive failures — pausing calls for {Cooldown}s",
            serverId, failures, cooldown);
        Announce(serverId, "server_throttled",
            $"{failures} calls in a row failed. Whiskers is pausing its own requests to this server for {cooldown}s " +
            "and will retry with a single probe. This is Whiskers throttling itself — the server is not being checked while it lasts.");

        // Plan-0006: this is an automatic action, so it gets checked. If the daemon is still crawling when the
        // window closes, the load was never ours and the throttle is a blind spot we imposed for nothing.
        // Resolved lazily like the notification above — the circuit breaker is constructed before most of the
        // graph and must not take a hard dependency on it.
        RecordForOutcome(serverId, failures);
    }

    public void ReleaseProbe(string serverId)
    {
        var c = _circuits.GetOrAdd(serverId, _ => new Circuit());
        lock (c.Gate)
        {
            // Only a probe that is still pending: after a success the circuit is closed, after a failed probe
            // it is open again, and in both cases there is nothing left to release.
            if (c.State == ServerCircuitState.HalfOpen)
                c.ProbeInFlight = false;
        }
    }

    public ServerCircuitSnapshot Snapshot(string serverId)
    {
        var c = _circuits.GetOrAdd(serverId, _ => new Circuit());
        lock (c.Gate)
            return new ServerCircuitSnapshot(serverId, c.State, c.ConsecutiveFailures, c.OpenedAt, c.LastReason);
    }

    public IReadOnlyList<ServerCircuitSnapshot> SnapshotAll() =>
        _circuits.Keys.Select(Snapshot).OrderBy(s => s.ServerId, StringComparer.Ordinal).ToList();

    /// <summary>Transport-level failures and our own timeouts. Everything else is the server answering.
    /// A cancellation while Whiskers itself is shutting down is not a timeout either: the loops' stopping token
    /// is linked into every in-flight read, so a restart used to tally one "failure" per open call against
    /// every server — the host had done nothing, Whiskers had hung up on it.</summary>
    private bool IsHealthSignal(Exception ex)
    {
        if (ex is OperationCanceledException && IsShuttingDown()) return false;
        return ex is TimeoutException or OperationCanceledException || DockerConnectionManager.IsConnectionFailure(ex);
    }

    private bool IsShuttingDown()
    {
        try
        {
            return _serviceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping.IsCancellationRequested == true;
        }
        catch (ObjectDisposedException)
        {
            return true; // the container is already being torn down
        }
    }

    /// <summary>Reports a transition. Never lets a notification failure break the Docker path — the circuit's
    /// job is to protect the server, and it must keep doing that even if no channel is reachable.</summary>
    /// <summary>Files this throttle for a later effectiveness check (Plan-0006). Best-effort and never
    /// allowed to interfere: a bookkeeping failure must not stop the circuit from opening.</summary>
    private void RecordForOutcome(string serverId, int failures)
    {
        try
        {
            var outcomes = _serviceProvider
                .GetService<Whiskers.Services.Observability.Outcomes.IActionOutcomeService>();
            if (outcomes is null) return;

            var name = _serviceProvider.GetService<IServerConfigService>()?.GetServer(serverId)?.Name ?? serverId;
            _ = outcomes.RecordAsync(
                Whiskers.Services.Observability.Outcomes.AutomaticActionKind.SelfThrottle,
                serverId, serverId, name, $"{failures} calls in a row failed");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not file the throttle of {ServerId} for an outcome check", serverId);
        }
    }

    private void Announce(string serverId, string eventType, string detail)
    {
        try
        {
            var notifications = _serviceProvider.GetService<INotificationService>();
            if (notifications is null) return;

            var name = _serviceProvider.GetService<IServerConfigService>()?.GetServer(serverId)?.Name ?? serverId;

            _ = notifications.SendAsync(new NotificationEvent
            {
                EventType = eventType,
                ServerId = serverId,
                ServerName = name,
                ContainerName = name,
                ImageInfo = detail,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not announce the circuit change for server {ServerId}", serverId);
        }
    }
}
