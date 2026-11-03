using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Services.Observability.SelfMetrics;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Observability;

/// <summary>
/// Watches the watchers (Plan-0002 WP5.3).
///
/// <para>Every other guard in this codebase reports a problem it noticed. This one reports the absence of
/// reports. It compares each loop's last successful cycle against that loop's own cadence and raises an alert
/// when the gap grows past three intervals — no matter <em>why</em>. A wedged Docker socket, a suspended
/// container, a paused loop, an exception nobody caught, a thread that simply died: all of them look the same
/// from here, and all of them mean the same thing to an operator. Whiskers has stopped looking.</para>
///
/// <para>That is deliberately a weaker claim than the specific guards make, and deliberately more robust. On
/// 2026-08-26 the specific signals existed — the timeouts were logged every cycle — and nothing acted on them.
/// This supervisor does not depend on any of them being wired correctly; it only depends on cycles happening.
/// It is therefore <b>not</b> suppressible by the mechanisms it supervises, and must stay that way when the
/// emergency stop arrives (SP-5): a switch that can silence the alarm about being silent is not a switch, it
/// is a blindfold.</para>
/// </summary>
public sealed class ScanSupervisor : Whiskers.Services.FleetBackgroundService
{
    /// <summary>A gap of more than this many intervals is reported. Three, not one: a single missed cycle is
    /// normal (a slow host, a long scan), three in a row is not.</summary>
    /// <summary>Public because the status view and the MCP tool read it rather than keeping their own copy.
    /// Two constants meaning the same thing drift, and the day they do, the page says "fine" about the loop
    /// that just paged someone — after which the next person decides the alerting is broken, not the loop.</summary>
    public const int IntervalsBeforeAlarm = 3;

    /// <summary>Never alarm faster than this, however short a loop's cadence is — a loop running every second
    /// must not be able to page someone after three seconds. Injectable ONLY so a test can produce a real
    /// stall in milliseconds instead of minutes; production always uses the default.</summary>
    private static readonly TimeSpan DefaultMinimumGap = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly ISelfMetrics _metrics;
    private readonly INotificationService _notifications;
    private readonly IServerConfigService _servers;
    private readonly ILogger<ScanSupervisor> _logger;
    private readonly TimeSpan _minimumGap;

    // Which (loop, server) pairs are currently reported, so a lasting stall is one alert and not one a minute.
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public ScanSupervisor(
        ISelfMetrics metrics,
        INotificationService notifications,
        IServerConfigService servers,
        ILogger<ScanSupervisor> logger,
        TimeSpan? minimumGap = null)
    {
        _metrics = metrics;
        _notifications = notifications;
        _servers = servers;
        _logger = logger;
        _minimumGap = minimumGap ?? DefaultMinimumGap;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        // Long enough that the loops have had a chance to record a first cycle; otherwise every restart
        // reports the whole fleet as stalled.
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync();
            }
            catch (Exception ex) when (!IsHostStopping(ex, ct))
            {
                _logger.LogError(ex, "Scan supervisor check failed");
            }

            await Task.Delay(CheckInterval, ct);
        }
    }

    /// <summary>One supervision pass. Public so a test can drive it without the hosted-service loop.</summary>
    public async Task CheckAsync()
    {
        var now = DateTime.UtcNow;

        foreach (var loop in _metrics.Loops())
        {
            // A loop that deliberately skips this server is not stalled — it is explicitly not covering it,
            // which the metrics already say. Alarming here would drown the real cases.
            if (loop.SkipReason is not null) continue;

            // Without a declared cadence there is no basis for a verdict. Staying silent is the honest
            // answer; inventing a threshold would make this supervisor a source of noise.
            if (loop.ExpectedInterval is not { } interval) continue;

            var allowed = interval * IntervalsBeforeAlarm;
            if (allowed < _minimumGap) allowed = _minimumGap;

            var key = $"{loop.Loop}|{loop.ServerId}";

            // A loop that has NEVER succeeded is judged by how many chances it has had, not by how recently it
            // tried. Falling back to the last attempt here — which this did, while the comment claimed the
            // opposite — meant a loop failing every single cycle looked permanently healthy: it kept attempting,
            // so its "age" kept resetting to zero. That is precisely the 2026-08-26 shape, where the thing that
            // ran on time and achieved nothing was the thing nobody noticed. Below the threshold it stays
            // silent, because a process that has just started deserves a few cycles before being called broken.
            var stalled = loop.LastSuccess is { } lastSuccess
                ? now - lastSuccess > allowed
                : loop.Cycles >= IntervalsBeforeAlarm;

            var reference = loop.LastSuccess;

            if (stalled && _reported.Add(key))
                await ReportAsync(loop, now - (reference ?? now), allowed, stalled: true);
            else if (!stalled && _reported.Remove(key))
                await ReportAsync(loop, TimeSpan.Zero, allowed, stalled: false);
        }
    }

    private async Task ReportAsync(LoopHealth loop, TimeSpan age, TimeSpan allowed, bool stalled)
    {
        var serverName = _servers.GetServer(loop.ServerId)?.Name ?? loop.ServerId;

        if (stalled)
            _logger.LogWarning(
                "Loop {Loop} has not completed a cycle for {Server} in {Age} (expected at most {Allowed})",
                loop.Loop, serverName, age, allowed);
        else
            _logger.LogInformation("Loop {Loop} is running again for {Server}", loop.Loop, serverName);

        await _notifications.SendAsync(new NotificationEvent
        {
            EventType = stalled ? NotificationEventTypes.MonitoringStalled : NotificationEventTypes.MonitoringResumed,
            ServerId = loop.ServerId,
            ServerName = serverName,
            ContainerName = serverName,
            ImageInfo = stalled
                ? $"The '{loop.Loop}' check has not completed for {serverName} in {Describe(age)} " +
                  $"(it runs every {Describe(loop.ExpectedInterval ?? TimeSpan.Zero)}). Whatever the cause, " +
                  "nothing is being checked there right now — which is not the same as nothing being wrong."
                : $"The '{loop.Loop}' check is completing again for {serverName}.",
            Timestamp = DateTime.UtcNow
        });
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{span.TotalHours:F1} h"
        : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:F0} min"
        : $"{span.TotalSeconds:F0} s";
}
