using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Cve;
using Whiskers.Services.Docker;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.AutoPatch;

/// <summary>
/// Nightly security patching: every container with fixable vulnerabilities is rolled onto the newest image
/// under its own tag, one at a time, inside the maintenance window. What it may and may not touch is decided
/// by <see cref="AutoPatchPlanner"/>; how a single container is patched, guarded and rolled back by the host
/// script behind <see cref="IAutoPatchRunner"/>.
///
/// <para>Off unless switched on, and a dry run until switched over — see <see cref="AutoPatchSettings"/>. Switching
/// it on in the configuration is not enough: an admin also has to take AutoPatch over in Settings
/// (<see cref="IAutomationOwnerStore"/>). Every nightly cycle then runs through the Mutation Gateway on that admin's
/// authority, re-checked per cycle (PC-D6).</para>
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
    private readonly IAutomationMutationRunner _automation;
    private readonly IAutomationOwnerStore _owners;

    private int _running;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _preview;
    private bool _stopRequested;
    private readonly object _previewGate = new();

    /// <summary>How long a preview started from a tool may run: a dry run pulls one image per candidate.</summary>
    public static readonly TimeSpan PreviewDeadline = TimeSpan.FromHours(2);
    private DateOnly? _lastNightlyRun;
    private bool _nightlyLoaded;
    private readonly IAutoPatchNightlyStore _nightly;
    private readonly Whiskers.Services.Mutations.IShutdownDrain _drain;

    public AutoPatchService(
        IOptionsMonitor<AutoPatchSettings> settings,
        IDockerService docker,
        ICveFindingsStore cves,
        IImageUpdateStore updates,
        IAutoPatchRunner runner,
        INotificationService notifications,
        IServiceScopeFactory scopeFactory,
        IAutomationMutationRunner automation,
        IAutomationOwnerStore owners,
        ILogger<AutoPatchService> logger,
        IAutoPatchNightlyStore? nightly = null,
        Whiskers.Services.Mutations.IShutdownDrain? drain = null)
        : this(settings, docker, cves, updates, runner, notifications, scopeFactory, automation, owners, logger,
            () => DateTime.UtcNow, nightly, drain) { }

    internal AutoPatchService(
        IOptionsMonitor<AutoPatchSettings> settings,
        IDockerService docker,
        ICveFindingsStore cves,
        IImageUpdateStore updates,
        IAutoPatchRunner runner,
        INotificationService notifications,
        IServiceScopeFactory scopeFactory,
        IAutomationMutationRunner automation,
        IAutomationOwnerStore owners,
        ILogger<AutoPatchService> logger,
        Func<DateTime> utcNow,
        IAutoPatchNightlyStore? nightly = null,
        Whiskers.Services.Mutations.IShutdownDrain? drain = null)
    {
        _automation = automation;
        _owners = owners;
        _settings = settings;
        _docker = docker;
        _cves = cves;
        _updates = updates;
        _runner = runner;
        _notifications = notifications;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _utcNow = utcNow;
        _nightly = nightly ?? new InMemoryAutoPatchNightlyStore();
        _drain = drain ?? Whiskers.Services.Mutations.NoShutdownDrain.Instance;
    }

    public AutoPatchCycleReport? LastCycle { get; private set; }

    /// <summary>The day of the last nightly cycle, as <see cref="IsDue"/> sees it. Internal for tests.</summary>
    internal DateOnly? LastNightlyRun => _lastNightlyRun;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    protected override async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_nightlyLoaded) await LoadNightlyAsync();
                if (IsDue(_settings.CurrentValue, _utcNow(), _lastNightlyRun))
                    await RunNightlyRecordedAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Auto-patch cycle failed"); }

            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>R1-39: the night's day comes from disk, so a restart in the window hour does not run it again; a cycle
    /// that started and never ended was cut off, which the status shows. Internal for tests.</summary>
    internal async Task LoadNightlyAsync()
    {
        AutoPatchNightlyState state;
        try
        {
            state = await _nightly.LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: an unreadable state could hide that tonight already ran. Nothing runs until it is readable.
            LastCycle = new(_utcNow(), _utcNow(), _settings.CurrentValue.DryRun, [], [], NightStateUnreadable);
            throw;
        }
        _lastNightlyRun = state.Day;
        _nightlyLoaded = true;
        if (state is { StartedAt: { } started, CompletedAt: null })
        {
            LastCycle ??= new(started, null, state.DryRun, [], [], InterruptedNight);
            _logger.LogWarning("The nightly auto-patch cycle started {Started:u} was interrupted by a stop", started);
        }
        else if (state is { StartedAt: { } began, CompletedAt: { } ended, Error: { } error })
        {
            // A night that ended early (a stop, a suspension, an unverified rollback) still says why after a restart.
            LastCycle ??= new(began, ended, state.DryRun, [], [], error);
        }
    }

    internal const string InterruptedNight =
        "Interrupted: Whiskers stopped while this nightly cycle ran. Containers it had not reached were not patched; " +
        "it was not run again that night.";

    internal const string NightStateUnreadable =
        "Not run: the nightly state (autopatch-nightly.json) cannot be read, so it is unknown whether tonight already " +
        "ran. No night runs until the file is fixed or removed.";

    internal const string NightNotRecorded =
        "Not run: the start of tonight's cycle could not be saved, so a restart could not tell that it ran. It runs " +
        "again tomorrow night.";

    /// <summary>The nightly cycle with its day, start and end on disk. The day is written first and must be saved
    /// before anything is patched: a night a restart could not see would be run a second time (fail closed — the night
    /// is skipped instead). Its error, if it ended early, is saved with its end. Internal for tests.</summary>
    internal async Task RunNightlyRecordedAsync(CancellationToken ct)
    {
        var now = _utcNow();
        _lastNightlyRun = DateOnly.FromDateTime(now);
        var state = new AutoPatchNightlyState { Day = _lastNightlyRun, StartedAt = now, DryRun = _settings.CurrentValue.DryRun };
        if (!await SaveNightlyAsync(state))
        {
            LastCycle = new(now, _utcNow(), state.DryRun, [], [], NightNotRecorded);
            return;
        }
        await RunNightlyAsync(ct);
        state.CompletedAt = _utcNow();
        state.Error = LastCycle is { } cycle && cycle.StartedAt >= now ? cycle.Error : null;
        await SaveNightlyAsync(state);
    }

    private async Task<bool> SaveNightlyAsync(AutoPatchNightlyState state)
    {
        try
        {
            await _nightly.SaveAsync(state);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The nightly auto-patch state could not be saved");
            return false;
        }
    }

    private const string NightlyRunner = "autopatch";

    internal const string SuspensionNotSaved =
        " The suspension could not be saved: it holds only until Whiskers restarts — take it over or switch it off now.";

    /// <summary>The nightly cycle, on the authority of the admin who took AutoPatch over. Without one it does not run:
    /// the configuration switch alone is not an owner. Internal for tests.</summary>
    internal async Task RunNightlyAsync(CancellationToken ct)
    {
        if (_owners.IsBlocked(IAutomationOwnerStore.AutoPatch))
        {
            _logger.LogError("Auto-patch owner could not be read; tonight's cycle did not run. Take AutoPatch over " +
                             "again in Settings → Automation owners");
            return;
        }
        var owner = _owners.Get(IAutomationOwnerStore.AutoPatch)?.OwnerId;
        if (owner is null)
        {
            _logger.LogWarning("Auto-patch is switched on but no admin has taken it over (Settings → Automation); " +
                               "tonight's cycle did not run");
            return;
        }

        var dryRun = _settings.CurrentValue.DryRun;
        // Only an admin taking it over in Settings records the owner, so the owner is also its provenance.
        var actor = new AutomationActor(NightlyRunner, "AutoPatch", owner, owner);
        var outcome = await _automation.RunAsync(actor, "autopatch.run",
            new { dryRun }, async token => MutationResults.Report(await RunGuardedAsync(forceDryRun: false, actor, token)), ct);
        if (outcome.Status is MutationStatus.Denied or MutationStatus.PendingApproval)
        {
            _logger.LogWarning("Auto-patch cycle refused: {Reason}", outcome.Reason);
            await SuspendIfOwnerLostAsync(actor, "autopatch.run", ct);
        }
        else if (outcome.Status == MutationStatus.Failed)
            _logger.LogError(outcome.Error, "Auto-patch cycle failed: {Reason}", outcome.Reason);
    }

    /// <summary>The owner lost their authority (PC-D6): AutoPatch is suspended until an admin takes it over, and the
    /// admins are told. A refusal by policy leaves it on.</summary>
    private async Task<bool> SuspendIfOwnerLostAsync(AutomationActor actor, string capabilityId, CancellationToken ct)
    {
        if (_automation.LostAuthority(actor, capabilityId) is not { } lost) return false;
        var result = await _owners.SuspendAsync(IAutomationOwnerStore.AutoPatch, actor.OwnerId!, lost, ct);
        if (result == SuspendResult.Superseded)
        {
            // An admin took AutoPatch over while this cycle ran on the old owner: stop the stale cycle, keep the new one.
            _logger.LogWarning("AutoPatch cycle stopped: its owner lost authority ({Reason}), and AutoPatch has a new " +
                               "owner since the cycle started", lost);
            return true;
        }
        var saved = result == SuspendResult.Suspended;
        _logger.LogError("AutoPatch was suspended: {Reason}. Take it over again in Settings → Automation owners", lost);
        try
        {
            await _notifications.SendAsync(new NotificationEvent
            {
                EventType = "automation_suspended",
                ContainerName = "AutoPatch",
                ImageInfo = $"AutoPatch was suspended: {lost}. It stays off until an admin takes it over in Settings."
                            + (saved ? "" : SuspensionNotSaved),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Notification for the suspended AutoPatch failed");
        }
        return true;
    }

    /// <summary>One container of an owned cycle, decided as <c>container.update</c> of exactly that container: the
    /// one-off <c>autopatch.run</c> decision for the night does not cover a protected container. A dry run is decided
    /// too: the host script still re-tags and pulls before it stops. An outcome that is not a success marks the
    /// operation failed, so the gateway's audit row does not read "executed".</summary>
    private async Task<AutoPatchOutcome> PatchGatedAsync(AutomationActor actor, ContainerInfo c, bool dryRun,
        CancellationToken ct)
    {
        var gated = await _automation.RunAsync(actor, "container.update",
            new { containerId = c.Id, name = c.Name, serverId = c.ServerId, dryRun },
            async token =>
            {
                var result = (await _runner.PatchAsync(c.ServerId, c.Name, dryRun, token)).Outcome;
                if (result.Failed) MutationScope.Fail($"Auto-patch ended as {result.Kind}.");
                return result;
            }, ct);
        return gated.Status switch
        {
            MutationStatus.Executed => gated.Value!,
            MutationStatus.Failed when gated.Value is { } reported => reported,
            MutationStatus.Failed => new(AutoPatchResultKind.Unknown, gated.Reason ?? "failed"),
            _ => new(AutoPatchResultKind.Aborted, $"refused: {gated.Reason}"),
        };
    }

    /// <summary>Once per day, inside the window hour. Internal for tests.</summary>
    internal static bool IsDue(AutoPatchSettings s, DateTime utcNow, DateOnly? lastRun)
        => s.Enabled
           && utcNow.Hour == Math.Clamp(s.RunAtHourUtc, 0, 23)
           && lastRun != DateOnly.FromDateTime(utcNow);

    public bool StartPreview(string onBehalfOf) => StartPreview(onBehalfOf, PreviewDeadline);

    internal bool StartPreview(string onBehalfOf, TimeSpan deadline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);
        // Under the gate together with StopAsync: a preview either starts before the stop takes its snapshot (and is
        // awaited) or sees the stop and does not start.
        lock (_previewGate)
        {
            if (_stopRequested) return false;
            // Claimed here, so "started" is never answered for a cycle that then does not run.
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
            var actor = new AutomationActor("autopatch-preview", "AutoPatch preview", onBehalfOf);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            cts.CancelAfter(deadline);
            Task run;
            // The tool's operation scope ends when the tool returns: it must not be what the cycle runs under.
            using (ExecutionContext.SuppressFlow())
                run = Task.Run(() => RunCycleCoreAsync(dryRun: true, actor, cts.Token));
            _preview = run.ContinueWith(t =>
            {
                Volatile.Write(ref _running, 0);
                if (t.Exception is { } ex)
                    _logger.LogError(ex.GetBaseException(), "Auto-patch preview failed");
                else if (cts.IsCancellationRequested && !_stopping.IsCancellationRequested)
                    _logger.LogWarning("Auto-patch preview stopped after {Deadline}: it ran out of time", deadline);
                cts.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>The running preview, if any (tests).</summary>
    internal Task? Preview { get { lock (_previewGate) return _preview; } }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? running;
        lock (_previewGate)
        {
            _stopRequested = true;
            running = _preview;
        }
        // Cancelled outside the gate: cancellation callbacks may run inline.
        await _stopping.CancelAsync();
        await base.StopAsync(cancellationToken);
        if (running is { } preview)
        {
            try { await preview.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* the host stops waiting; the preview was cancelled */ }
        }
    }

    public Task<bool> RunCycleAsync(bool forceDryRun, string onBehalfOf, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);
        return RunGuardedAsync(forceDryRun, new AutomationActor("autopatch-preview", "AutoPatch preview", onBehalfOf), ct);
    }

    /// <summary><paramref name="actor"/> is the nightly owner, or whoever started a preview; each container the cycle
    /// would patch, or try in a dry run, is decided on its own on that authority as it is right now (protected
    /// resources, kill switch, a key revoked half-way). <c>false</c> when a cycle was already running or a
    /// container's patch failed.</summary>
    private async Task<bool> RunGuardedAsync(bool forceDryRun, AutomationActor actor, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        try
        {
            return await RunCycleCoreAsync(forceDryRun || _settings.CurrentValue.DryRun, actor, ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<bool> RunCycleCoreAsync(bool dryRun, AutomationActor actor, CancellationToken ct)
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
            return true;
        }

        var containers = await _docker.ListAllContainersAsync(all: false);
        var results = _cves.GetAll();
        var localBuilds = await FindLocalBuildsAsync(containers, results);
        var plan = AutoPatchPlanner.Plan(containers, results, settings, UpdateAvailable,
            c => localBuilds.Contains((c.ServerId, c.Name)));
        LastCycle = new(started, null, dryRun, [], plan.Skipped);

        _logger.LogInformation("Auto-patch cycle started ({Mode}): {Candidates} candidate(s), {Skipped} skipped",
            dryRun ? "dry run" : "apply", plan.Candidates.Count, plan.Skipped.Count);

        foreach (var candidate in plan.Candidates)
        {
            var c = candidate.Container;
            if (ct.IsCancellationRequested)
            {
                // Said, so a night cut short is not recorded as complete (R1-39).
                LastCycle = LastCycle! with { Error = $"Stopped before {c.Name}: the cycle was cancelled." };
                break;
            }
            // R1-39: once Whiskers is stopping, no further container is started; the one in progress is drained.
            using var draining = _drain.TryEnter($"auto-patch of '{c.Name}'");
            if (draining is null)
            {
                LastCycle = LastCycle! with { Error = $"Stopped before {c.Name}: Whiskers is shutting down." };
                break;
            }
            var startedAt = _utcNow();

            AutoPatchOutcome outcome;
            try
            {
                outcome = await PatchGatedAsync(actor, c, dryRun, ct);
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

            // The owner lost their authority half-way (a key revoked, a demotion between two containers): the nightly
            // cycle is suspended and stops here, it does not carry on refusing container after container (R1-04).
            // A preview runs on the authority of the person who started it and suspends nothing.
            if (outcome.Kind == AutoPatchResultKind.Aborted && actor.RunnerId == NightlyRunner
                && await SuspendIfOwnerLostAsync(actor, "container.update", ct))
            {
                LastCycle = LastCycle! with { Error = $"Stopped at {c.Name}: AutoPatch was suspended." };
                break;
            }

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
        return !entries.Any(e => AutoPatchOutcome.IsFailure(e.Kind));
    }

    /// <summary>Which containers with findings run an image compose built on the host. One image inspect per
    /// such container; an image that cannot be inspected counts as not local — the host script checks the same
    /// label again before it pulls anything.</summary>
    private async Task<HashSet<(string ServerId, string Name)>> FindLocalBuildsAsync(
        IEnumerable<ContainerInfo> containers, IEnumerable<Whiskers.Models.Cve.CveScanResult> results)
    {
        var withFindings = results
            .Where(r => r.Findings.Count > 0 && !string.IsNullOrEmpty(r.ContainerName))
            .Select(r => (r.ServerId.ToUpperInvariant(), r.ContainerName!.ToUpperInvariant()))
            .ToHashSet();
        var local = new HashSet<(string, string)>();
        foreach (var c in containers.Where(c => withFindings.Contains((c.ServerId.ToUpperInvariant(), c.Name.ToUpperInvariant()))))
        {
            var imageRef = string.IsNullOrEmpty(c.ImageId) ? c.Image : c.ImageId;
            var contract = await _docker.GetImageContractAsync(imageRef, c.ServerId);
            if (contract?.IsComposeBuilt == true) local.Add((c.ServerId, c.Name));
        }
        return local;
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
