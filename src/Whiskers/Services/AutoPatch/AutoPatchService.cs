using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Cve;
using Whiskers.Services.Docker;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.AutoPatch;

/// <summary>
/// Nightly security patching: every container with fixable vulnerabilities is rolled onto the newest image
/// under its own tag, one at a time, inside the maintenance window. What it may and may not touch is decided
/// by <see cref="AutoPatchPlanner"/>; how a single container is patched, guarded and rolled back by the host
/// script behind <see cref="IAutoPatchRunner"/>.
///
/// <para>Off unless switched on, and a dry run until switched over — see <see cref="AutoPatchSettings"/>.</para>
/// </summary>
public sealed class AutoPatchService : FleetBackgroundService, IAutoPatchService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    private readonly IOptionsMonitor<AutoPatchSettings> _settings;
    private readonly IDockerService _docker;
    private readonly ICveFindingsStore _cves;
    private readonly IImageUpdateStore _updates;
    private readonly IAutoPatchRunner _runner;
    private readonly INotificationService _notifications;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoPatchService> _logger;
    private readonly Func<DateTime> _utcNow;

    private int _running;
    private DateOnly? _lastNightlyRun;

    public AutoPatchService(
        IOptionsMonitor<AutoPatchSettings> settings,
        IDockerService docker,
        ICveFindingsStore cves,
        IImageUpdateStore updates,
        IAutoPatchRunner runner,
        INotificationService notifications,
        IServiceScopeFactory scopeFactory,
        ILogger<AutoPatchService> logger)
        : this(settings, docker, cves, updates, runner, notifications, scopeFactory, logger, () => DateTime.UtcNow) { }

    internal AutoPatchService(
        IOptionsMonitor<AutoPatchSettings> settings,
        IDockerService docker,
        ICveFindingsStore cves,
        IImageUpdateStore updates,
        IAutoPatchRunner runner,
        INotificationService notifications,
        IServiceScopeFactory scopeFactory,
        ILogger<AutoPatchService> logger,
        Func<DateTime> utcNow)
    {
        _settings = settings;
        _docker = docker;
        _cves = cves;
        _updates = updates;
        _runner = runner;
        _notifications = notifications;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _utcNow = utcNow;
    }

    public AutoPatchCycleReport? LastCycle { get; private set; }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    protected override async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (IsDue(_settings.CurrentValue, _utcNow(), _lastNightlyRun))
                {
                    _lastNightlyRun = DateOnly.FromDateTime(_utcNow());
                    await RunCycleAsync(forceDryRun: false, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Auto-patch cycle failed"); }

            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Once per day, inside the window hour. Internal for tests.</summary>
    internal static bool IsDue(AutoPatchSettings s, DateTime utcNow, DateOnly? lastRun)
        => s.Enabled
           && utcNow.Hour == Math.Clamp(s.RunAtHourUtc, 0, 23)
           && lastRun != DateOnly.FromDateTime(utcNow);

    public async Task<bool> RunCycleAsync(bool forceDryRun, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        try
        {
            await RunCycleCoreAsync(forceDryRun || _settings.CurrentValue.DryRun, ct);
            return true;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task RunCycleCoreAsync(bool dryRun, CancellationToken ct)
    {
        var settings = _settings.CurrentValue;
        var started = _utcNow();
        var entries = new List<AutoPatchEntry>();

        // Without a finished scan there is nothing to decide on — and "no findings" from a store that was
        // never filled would read as "nothing to patch".
        if (_cves.LastScanAt is null)
        {
            LastCycle = new(started, _utcNow(), dryRun, [], [], "No CVE scan has completed yet.");
            _logger.LogWarning("Auto-patch skipped: no CVE scan has completed yet");
            return;
        }

        var containers = await _docker.ListAllContainersAsync(all: false);
        var plan = AutoPatchPlanner.Plan(containers, _cves.GetAll(), settings, UpdateAvailable);
        LastCycle = new(started, null, dryRun, [], plan.Skipped);

        _logger.LogInformation("Auto-patch cycle started ({Mode}): {Candidates} candidate(s), {Skipped} skipped",
            dryRun ? "dry run" : "apply", plan.Candidates.Count, plan.Skipped.Count);

        foreach (var candidate in plan.Candidates)
        {
            if (ct.IsCancellationRequested) break;
            var c = candidate.Container;
            var startedAt = _utcNow();

            AutoPatchOutcome outcome;
            try
            {
                (outcome, _) = await _runner.PatchAsync(c.ServerId, c.Name, dryRun, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = new(AutoPatchResultKind.Unknown, ex.Message);
            }

            entries.Add(new(c.ServerId, c.Name, outcome.Kind, outcome.Detail));
            // Readers get a copy: the status tool reads this while the cycle is still adding to the list.
            LastCycle = LastCycle! with { Entries = entries.ToArray() };
            _logger.LogInformation("Auto-patch {Container} on {Server}: {Kind} {Detail}",
                c.Name, c.ServerId, outcome.Kind, outcome.Detail);

            if (outcome.Attempted) await RecordHistoryAsync(c, outcome, startedAt);
            if (outcome.NeedsAttention) await NotifyAsync(c, outcome);

            // A rollback that could not be verified means a host is in a state nobody has looked at.
            // Carrying on would stack a second unknown on top of the first.
            if (outcome.Kind == AutoPatchResultKind.RollbackNotVerified)
            {
                LastCycle = LastCycle! with { Error = $"Stopped after {c.Name}: rollback not verified." };
                break;
            }
        }

        LastCycle = LastCycle! with { CompletedAt = _utcNow() };
        _logger.LogInformation("Auto-patch cycle done: {Summary}",
            string.Join(", ", entries.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}")));
    }

    /// <summary>The update check's answer for one container; null when it has none or its check failed.</summary>
    private bool? UpdateAvailable(ContainerInfo c)
        => _updates.Get(c.Id, c.ServerId) is { Error: null } info ? info.UpdateAvailable : null;

    private async Task RecordHistoryAsync(ContainerInfo c, AutoPatchOutcome outcome, DateTime startedAt)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.UpdateHistory.Add(new UpdateHistoryEntity
            {
                ContainerId = c.Id,
                ContainerName = c.Name,
                ServerId = c.ServerId,
                Image = c.Image,
                StartedAt = startedAt,
                CompletedAt = _utcNow(),
                Success = outcome.Kind == AutoPatchResultKind.Updated,
                RolledBack = outcome.Kind is AutoPatchResultKind.RolledBack or AutoPatchResultKind.RollbackNotVerified,
                Error = outcome.Kind == AutoPatchResultKind.Updated ? null : $"auto-patch: {outcome.Kind} {outcome.Detail}".Trim(),
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record auto-patch history for {Container}", c.Name);
        }
    }

    private async Task NotifyAsync(ContainerInfo c, AutoPatchOutcome outcome)
    {
        try
        {
            await _notifications.SendAsync(new NotificationEvent
            {
                ContainerId = c.Id,
                ContainerName = c.Name,
                Image = c.Image,
                ServerId = c.ServerId,
                ServerName = c.ServerName,
                EventType = "auto_update_failed",
                ImageInfo = $"Auto-patch: {outcome.Kind} {outcome.Detail}".Trim(),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-patch notification failed for {Container}", c.Name);
        }
    }
}
