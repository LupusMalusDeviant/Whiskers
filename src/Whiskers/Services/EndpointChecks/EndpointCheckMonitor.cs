using System.Collections.Concurrent;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.EndpointChecks;

/// <summary>
/// R1-57 (contract 0004): runs the checks that are due — at most <see cref="MaxConcurrent"/> at a time across the fleet
/// and one per server — records each run and turns a sustained failure into <c>endpoint_check_failed</c> (R1-56's
/// duration model). A failing run is logged and recorded and never ends the loop (R1-41).
/// </summary>
public sealed class EndpointCheckMonitor : FleetBackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    public const int MaxConcurrent = EndpointCheckSlots.MaxConcurrent;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public static readonly TimeSpan OpenAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ClearAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    private readonly IEndpointCheckStore _store;
    private readonly IEndpointCheckRunner _runner;
    private readonly IAutomationMutationRunner _automation;
    private readonly IServerConfigService _servers;
    private readonly INotificationService _notify;
    private readonly ILogger<EndpointCheckMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly JsonFileStore<EndpointCheckBreachFile>? _breachFile;
    private readonly BreachTracker _tracker = new(new HostLoadThresholds());
    private readonly IEndpointCheckSlots _slots;   // shared with "Run now" (review m1)
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
    /// <summary>When each check's run was last refused before the gateway, and the definition it was refused as (its
    /// <c>UpdatedUtc</c>) — in memory: a restart may refuse once more.</summary>
    private readonly ConcurrentDictionary<string, (DateTime At, DateTime Saved)> _refusedAt = new(StringComparer.Ordinal);
    private DateTime _lastPrune = DateTime.MinValue;
    private bool _restored;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private string? _savedBreaches;

    public EndpointCheckMonitor(IEndpointCheckStore store, IEndpointCheckRunner runner, IAutomationMutationRunner automation,
        IServerConfigService servers, INotificationService notify, ILogger<EndpointCheckMonitor> logger,
        IEndpointCheckSlots slots, DataPathOptions? paths = null, TimeProvider? time = null)
    {
        _store = store;
        _runner = runner;
        _automation = automation;
        _servers = servers;
        _notify = notify;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        // Review n6: required — a private instance would let "Run now" and the monitor run the same check at once.
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _breachFile = paths is null ? null : new JsonFileStore<EndpointCheckBreachFile>(paths.EndpointCheckBreachesJson,
            problem: f => f.Breaches is null || f.Breaches.Any(b => b is null) ? "the list of breaches is missing or holds an empty entry" : null);
    }

    /// <summary>The run slots this monitor takes (the boot test checks they are the app's single shared instance).</summary>
    internal IEndpointCheckSlots Slots => _slots;

    /// <summary>The rules of <c>endpoint_check_failed</c>: open after 5 minutes of failing runs (two at least — one
    /// reading has no duration), clear after 5 minutes of passing ones. Value 1 = failed or refused, 0 = passed.</summary>
    public static BreachRules RulesFor(int intervalSeconds) =>
        new(OpenAfter, 0.5, ClearAfter, double.PositiveInfinity,
            BreachRules.GapToleranceFor(TimeSpan.FromSeconds(intervalSeconds)), TimeSpan.Zero);

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, _time);
        do
        {
            try
            {
                await CycleAsync(stoppingToken);
            }
            catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))
            {
                _logger.LogError(ex, "Endpoint-check cycle failed; the next one runs as usual");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        // Let runs in flight finish their own deadline before the host goes away.
        try { await Task.WhenAll(_inFlight.Values); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* each run logs its own failure */ }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>One tick: starts the runs that are due (not awaited — a slow host does not hold the others up), closes
    /// the alerts of checks that are gone or off, prunes old results once an hour.</summary>
    internal async Task CycleAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await RestoreAsync(now);
        foreach (var done in _inFlight.Where(kv => kv.Value.IsCompleted).ToList()) _inFlight.TryRemove(done);
        var checks = _store.List();
        await CloseDroppedAsync(checks, now, ct);
        // A run refused before the gateway counts as a run for the interval: a check whose suspension could not be
        // saved stays on and is refused again at its interval, not every tick (each refusal is an audit event). A check
        // saved since its refusal (taken over, given an owner) is a new definition: the refusal no longer holds it back.
        foreach (var c in checks)
            if (_refusedAt.TryGetValue(c.Id, out var refused) && c.UpdatedUtc != refused.Saved)
                _refusedAt.TryRemove(new KeyValuePair<string, (DateTime, DateTime)>(c.Id, refused));
        checks = checks.Select(c => _refusedAt.TryGetValue(c.Id, out var r) && r.At is var at && (c.LastRunUtc is not { } last || last < at)
            ? c with { LastRunUtc = at }
            : c).ToList();

        foreach (var check in Due(checks, now, _slots.Running))
        {
            if (!_slots.TryAcquire(check.Id, check.ServerId)) continue;   // taken meanwhile ("Run now")
            _inFlight[check.Id] = RunOneAsync(check, ct);   // yields first, so the slot is taken before it can end
        }

        if (now - _lastPrune >= PruneEvery)
        {
            _lastPrune = now;
            var pruned = await _store.PruneAsync(now - Retention, ct);
            if (pruned > 0) _logger.LogInformation("Pruned {Count} endpoint-check results older than 7 days", pruned);
        }
    }

    /// <summary>The checks to start now: on, due, not running, at most one per server (also counting the running ones)
    /// and at most <see cref="MaxConcurrent"/> in all — the longest overdue first.</summary>
    internal static IReadOnlyList<EndpointCheckDefinition> Due(IReadOnlyList<EndpointCheckDefinition> checks, DateTime now,
        IReadOnlyDictionary<string, string> running)
    {
        var busy = running.Values.ToHashSet(StringComparer.Ordinal);
        var free = MaxConcurrent - running.Count;
        var picked = new List<EndpointCheckDefinition>();
        foreach (var check in checks
                     .Where(c => c.Enabled && !running.ContainsKey(c.Id)
                                 && (c.LastRunUtc is not { } last || now - last >= TimeSpan.FromSeconds(Math.Max(c.IntervalSeconds, EndpointCheckValidator.MinIntervalSeconds))))
                     .OrderBy(c => c.LastRunUtc ?? DateTime.MinValue).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            if (picked.Count >= free) break;
            if (!busy.Add(check.ServerId)) continue;
            picked.Add(check);
        }
        return picked;
    }

    private async Task RunOneAsync(EndpointCheckDefinition check, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            // Review m5: a check without an owner never runs, grace period or not — it would run on the capability's
            // own level (Admin, root-equivalent). Only a row written past the store can have none.
            if (string.IsNullOrWhiteSpace(check.OwnerId))
            {
                await RefuseRunAsync(check, "The check has no owner; an Admin must save it again before it runs.", ct);
                await SuspendAsync(check, "it has no owner", ct);
                return;
            }

            // INV-04: an owner who lost the authority suspends the check instead of running it.
            if (_automation.LostAuthority(EndpointCheckRunner.ActorOf(check), "endpointcheck.run") is { } lost)
            {
                await RefuseRunAsync(check, $"Refused: {lost}.", ct);
                await SuspendAsync(check, lost, ct);
                return;
            }

            // It runs: a refusal before no longer stands in for its last run (review NIT-1).
            _refusedAt.TryRemove(check.Id, out _);
            var run = await _runner.RunAsync(check, ct);
            await _store.RecordAsync(run, ct);
            await ConsiderAsync(check, run, ct);
        }
        catch (Exception ex) when (!IsHostStopping(ex, ct))
        {
            _logger.LogError(ex, "Endpoint check {Check} on {Server} failed to run", check.Name, check.ServerId);
            try
            {
                var failed = new EndpointCheckRun(check.Id, _time.GetUtcNow().UtcDateTime, EndpointCheckOutcome.Failed,
                    EndpointCheckRunner.Bounded("The run could not be completed: " + ex.Message));
                await _store.RecordAsync(failed, ct);
                await ConsiderAsync(check, failed, ct);
            }
            catch (Exception inner) when (!IsHostStopping(inner, ct))
            {
                _logger.LogError(inner, "The failed run of endpoint check {Check} could not be recorded", check.Name);
            }
        }
        finally
        {
            _slots.Release(check.Id);
        }
    }

    /// <summary>A due run the monitor refuses before the gateway — no owner, an owner who lost the authority — is
    /// recorded as the gateway's Denied event for <c>endpointcheck.run</c>, so the attempt is on record exactly once
    /// (INV-45, as the VPN bring-up does, R1-43). Nothing runs.</summary>
    private async Task RefuseRunAsync(EndpointCheckDefinition check, string reason, CancellationToken ct)
    {
        _refusedAt[check.Id] = (_time.GetUtcNow().UtcDateTime, check.UpdatedUtc);
        try
        {
            await _automation.RefuseAsync(EndpointCheckRunner.ActorOf(check), "endpointcheck.run",
                new { serverId = check.ServerId, checkId = check.Id }, reason, ct);
        }
        catch (Exception ex) when (!IsHostStopping(ex, ct))
        {
            // The refusal stands either way; the audit trail's own failure is reported by the gateway.
            _logger.LogError(ex, "The refused run of endpoint check {Check} could not be recorded", check.Name);
        }
    }

    private async Task SuspendAsync(EndpointCheckDefinition check, string lost, CancellationToken ct)
    {
        var reason = $"Suspended: {lost}. An Admin can take it over by saving it again.";
        bool suspended;
        try { suspended = await _store.SuspendAsync(check.Id, check.OwnerId, reason, ct); }
        catch (Exception ex) when (!IsHostStopping(ex, ct))
        {
            _logger.LogError(ex, "Endpoint check {Check} could not be suspended", check.Name);
            suspended = false;
        }
        if (!suspended)
        {
            _logger.LogWarning("Endpoint check {Check} was not run ({Reason}); it was not suspended (taken over, or the write failed)",
                check.Name, lost);
            return;
        }
        _logger.LogWarning("Endpoint check {Check} was suspended: {Reason}", check.Name, lost);
        await SendAsync(new NotificationEvent
        {
            EventType = NotificationEventTypes.AutomationSuspended,
            ServerId = check.ServerId,
            ServerName = ServerName(check.ServerId),
            ContainerName = check.Name,
            // Review round 3 N1: the reason names the former owner; it stays on the check (Admins see it on /checks).
            ImageInfo = $"Endpoint check \"{check.Name}\" was suspended: its owner can no longer run it. It stays off until an Admin saves it again.",
        }, ct);
    }

    private async Task ConsiderAsync(EndpointCheckDefinition check, EndpointCheckRun run, CancellationToken ct)
    {
        var serverName = ServerName(check.ServerId);
        var label = $"{check.Name} on {serverName}";
        var value = run.Outcome == EndpointCheckOutcome.Passed ? 0 : 1;
        // Review round 3 N1: notifications reach every role (/notifications, list_recent_alerts), so internal addresses
        // are masked as on the page below Admin.
        var detail = EndpointCheckRunner.MaskInternalAddresses(run.Detail);
        var finding = _tracker.Consider(run.AtUtc, SeriesOf(check.Id), label, NotificationEventTypes.EndpointCheckFailed,
            value, 1,
            (_, span) => $"{label}: failing for {BreachTracker.Describe(span)} — {detail}",
            _ => $"{label}: passing again — {detail}",
            RulesFor(check.IntervalSeconds));
        await NotifyAsync(check, finding, ct);
        await SaveBreachesAsync();
    }

    /// <summary>Closes the alert of a check that was deleted or switched off: nothing checks it any more.</summary>
    private async Task CloseDroppedAsync(IReadOnlyList<EndpointCheckDefinition> checks, DateTime now, CancellationToken ct)
    {
        var live = checks.Where(c => c.Enabled).ToDictionary(c => SeriesOf(c.Id), StringComparer.Ordinal);
        var all = checks.ToDictionary(c => SeriesOf(c.Id), StringComparer.Ordinal);
        var changed = false;
        foreach (var series in _tracker.Tracked().ToList())
        {
            if (live.ContainsKey(series)) continue;
            var reason = all.ContainsKey(series) ? "the check was switched off" : "the check was deleted";
            foreach (var finding in _tracker.Close(series, now, reason))
                await NotifyAsync(all.GetValueOrDefault(series), finding, ct);
            _tracker.Forget(series);
            changed = true;
        }
        if (changed) await SaveBreachesAsync();
    }

    private async Task NotifyAsync(EndpointCheckDefinition? check, HostLoadFinding? finding, CancellationToken ct)
    {
        if (finding is null) return;
        _logger.Log(finding.What == FindingKind.Cleared ? LogLevel.Information : LogLevel.Warning,
            "{EventType}: {Summary}", finding.EventType, finding.Summary);
        await SendAsync(new NotificationEvent
        {
            EventType = finding.EventType,
            RecoveryUnconfirmed = finding.Unconfirmed,
            ServerId = check?.ServerId,
            ServerName = check is null ? finding.ServerName : ServerName(check.ServerId),
            ContainerName = check?.Name ?? finding.ServerName,
            ImageInfo = finding.Summary,
            Timestamp = finding.AtUtc,
        }, ct);
    }

    private async Task SendAsync(NotificationEvent evt, CancellationToken ct)
    {
        try { await _notify.SendAsync(evt, ct); }
        catch (Exception ex) when (!IsHostStopping(ex, ct))
        {
            _logger.LogWarning(ex, "Notification {EventType} could not be handed over", evt.EventType);
        }
    }

    private string ServerName(string serverId) => _servers.GetServer(serverId)?.Name ?? serverId;

    internal static string SeriesOf(string checkId) => "check:" + checkId;

    // --- Open alerts across a restart (as R1-56 keeps the threshold alerts) -------------------------------------------

    private async Task RestoreAsync(DateTime now)
    {
        if (_restored) return;
        _restored = true;
        if (_breachFile is null || !_breachFile.Exists()) return;
        try
        {
            _tracker.Restore((await _breachFile.LoadAsync()).Breaches, now);
        }
        catch (Exception ex)
        {
            // Raised again if the failure goes on: delivered rather than lost.
            _logger.LogWarning(ex, "The open endpoint-check alerts could not be read; they are raised again if the failures go on");
        }
    }

    private async Task SaveBreachesAsync()
    {
        if (_breachFile is null) return;
        await _saveLock.WaitAsync();
        try { await SaveBreachesLockedAsync(); }
        finally { _saveLock.Release(); }
    }

    private async Task SaveBreachesLockedAsync()
    {
        if (_breachFile is null) return;
        var state = new EndpointCheckBreachFile { Breaches = [.. _tracker.Snapshot()] };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        if (json == _savedBreaches) return;
        try
        {
            await _breachFile.SaveAsync(state);
            _savedBreaches = json;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The open endpoint-check alerts could not be saved; a restart now would raise them again");
        }
    }
}

/// <summary>R1-57: the open <c>endpoint_check_failed</c> alerts, kept across a restart.</summary>
public sealed class EndpointCheckBreachFile
{
    public List<BreachState> Breaches { get; set; } = [];
}
