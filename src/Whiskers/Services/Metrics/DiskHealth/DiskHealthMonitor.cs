using Whiskers.Models;
using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Notifications;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Metrics.DiskHealth;

/// <summary>
/// R1-58 (contract 0005): reads every enabled server's disk health from its metrics endpoint every 5 minutes — only while
/// <see cref="IDiskHealthSettingsService.Enabled"/> is on. Six fixed instant queries per host, each selected on
/// <c>server="&lt;id&gt;"</c> (<see cref="DiskHealthQueries.For"/>), through <see cref="IPrometheusQueryClient"/> (10 s per
/// query, the caller's cancellation honoured). Never a command on the host. A server without a metrics endpoint is
/// recorded as Unsupported without a query. A failure is isolated to its host and its cycle (R1-41).
///
/// <para>Off: nothing is queried and nothing is shown — the readings are dropped and the open alerts are closed,
/// unconfirmed ("disk-health readings were switched off"), so no raise is left without its end. A server no longer
/// configured or enabled is closed and dropped the same way.</para>
/// </summary>
public sealed class DiskHealthMonitor : FleetBackgroundService
{
    /// <summary>The cadence (contract 0005).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServerConfigService _servers;
    private readonly IPrometheusQueryClient _query;
    private readonly IDiskHealthSettingsService _settings;
    private readonly DiskHealthService _readings;
    private readonly INotificationService _notify;
    private readonly IDiskHealthStateStore? _state;
    private readonly ILogger<DiskHealthMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly DiskHealthAlerts _alerts = new();
    private readonly TimeSpan _freshness;
    private bool _restored;
    private string? _saved;

    public DiskHealthMonitor(IServerConfigService servers, IPrometheusQueryClient query, IDiskHealthSettingsService settings,
        DiskHealthService readings, INotificationService notify, ILogger<DiskHealthMonitor> logger,
        IDiskHealthStateStore? state = null, TimeProvider? time = null)
    {
        _servers = servers;
        _query = query;
        _settings = settings;
        _readings = readings;
        _notify = notify;
        _logger = logger;
        _state = state;
        _time = time ?? TimeProvider.System;
        _freshness = DiskHealthParser.DefaultFreshness;
    }

    /// <summary>Tests only: the alerts' state.</summary>
    internal DiskHealthAlerts Alerts => _alerts;

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))
            {
                _logger.LogWarning(ex, "Disk-health cycle failed; the next one runs in {Interval}", Interval);
            }

            try { await Task.Delay(Interval, _time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One cycle. Internal for tests, which drive it with a fake clock.</summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        var now = Now();
        await RestoreAsync(now);

        if (!_settings.Enabled)
        {
            // Off: nothing is queried and nothing is shown. What was raised is closed, so no alert is left open.
            foreach (var serverId in _alerts.Tracked().Concat(_readings.All().Keys).Distinct(StringComparer.Ordinal).ToList())
                await CloseAsync(serverId, now, "disk-health readings were switched off", ct);
            _readings.Clear();
            await SaveAsync();
            return;
        }

        var servers = _servers.GetEnabledServers();
        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ReadAsync(server, now, ct);
            }
            catch (Exception ex) when (!IsHostStopping(ex, ct))
            {
                // R1-41: one host's failure is that host's, this cycle's. The last reading stays as it was.
                _logger.LogWarning(ex, "Disk health of {Server} could not be read this cycle", server.Name);
            }
        }

        // A server no longer configured or enabled: its alerts are closed and its reading dropped.
        var current = servers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _alerts.Tracked().Concat(_readings.All().Keys).Where(id => !current.Contains(id))
                     .Distinct(StringComparer.Ordinal).ToList())
            await CloseAsync(gone, now, "the server is no longer read", ct);

        await SaveAsync();
    }

    private async Task ReadAsync(Models.ServerConfig server, DateTime now, CancellationToken ct)
    {
        var previous = _readings.Latest(server.Id);
        DiskHealthQueries results;
        if (server.MetricsEndpoint is not { Length: > 0 } endpoint)
        {
            results = DiskHealthQueries.AllFailed("no metrics endpoint");
        }
        else
        {
            // Side by side: a host whose endpoint does not answer costs one timeout per cycle, not six.
            var answers = await Task.WhenAll(DiskHealthQueries.For(server.Id).Select(q => _query.QueryAsync(endpoint, q, ct)));
            results = new DiskHealthQueries(answers[0], answers[1], answers[2], answers[3], answers[4], answers[5]);
        }

        var reading = DiskHealthParser.Parse(server.Id, results, now, previous, _freshness);
        _readings.Set(reading);

        var template = new NotificationEvent { ServerId = server.Id, ServerName = server.Name, ContainerName = server.Name };
        foreach (var finding in _alerts.Evaluate(server.Id, server.Name, reading, previous?.Overall, now))
            await NotifyAsync(template, finding, ct);
    }

    private async Task CloseAsync(string serverId, DateTime now, string reason, CancellationToken ct)
    {
        var name = _servers.GetServer(serverId)?.Name ?? serverId;
        foreach (var finding in _alerts.Close(serverId, now, reason))
        {
            try
            {
                await NotifyAsync(new NotificationEvent { ServerId = serverId, ServerName = name, ContainerName = name }, finding, ct);
            }
            catch (Exception ex) when (!IsHostStopping(ex, ct))
            {
                _logger.LogWarning(ex, "The close of {EventType} on {Server} could not be sent", finding.EventType, name);
            }
        }
        _readings.Remove(serverId);
    }

    private async Task NotifyAsync(NotificationEvent template, HostLoadFinding finding, CancellationToken ct)
    {
        _logger.Log(finding.What == FindingKind.Cleared ? LogLevel.Information : LogLevel.Warning,
            "{EventType} on {Server}: {Summary}", finding.EventType, finding.ServerName, finding.Summary);
        var evt = template.Copy();
        evt.EventType = finding.EventType;
        evt.RecoveryUnconfirmed = finding.Unconfirmed;
        evt.ImageInfo = finding.Summary;
        evt.Timestamp = finding.AtUtc;
        await _notify.SendAsync(evt, ct);
    }

    /// <summary>Takes over the state saved before a restart, once. A file that cannot be read is logged and ignored — an
    /// alert still going on is then raised again (delivered rather than lost).</summary>
    private async Task RestoreAsync(DateTime now)
    {
        if (_restored) return;
        _restored = true;
        if (_state is null) return;
        try
        {
            var saved = await _state.LoadAsync();
            foreach (var reading in saved.Readings) _readings.Set(reading);
            _alerts.Restore(saved.Breaches, saved.UnknownArmed, now);
            _saved = Serialize(saved);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The disk-health state of the last run cannot be read; an alert still going on is raised again");
        }
    }

    /// <summary>Writes the state when it changed. A failed write is logged; the next cycle tries again.</summary>
    private async Task SaveAsync()
    {
        if (_state is null) return;
        var (breaches, armed) = _alerts.Snapshot();
        var state = new DiskHealthStateFile
        {
            Readings = _readings.All().Values.OrderBy(r => r.ServerId, StringComparer.Ordinal).ToList(),
            Breaches = [.. breaches],
            UnknownArmed = [.. armed],
        };
        var json = Serialize(state);
        if (json == _saved) return;
        try
        {
            await _state.SaveAsync(state);
            _saved = json;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The disk-health state could not be saved; a restart now would forget what went missing");
        }
    }

    private static string Serialize(DiskHealthStateFile state) => System.Text.Json.JsonSerializer.Serialize(state);
}
