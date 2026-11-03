using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.AutoPatch;
using Whiskers.Services.AutoUpdate;
using Whiskers.Services.Cve;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Scheduler;

namespace Whiskers.Tests;

/// <summary>
/// R1-39 (F-16): a stop lets running unattended work finish within the drain budget and starts nothing new; what a
/// forced stop cuts off is reported as interrupted at the next start and never repeated — shown for a scheduled
/// command, a scheduled backup, an AutoUpdate recreate and an AutoPatch cycle.
/// </summary>
public sealed class MutationShutdownTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sw-shutdown-{Guid.NewGuid():N}.db");
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"sw-shutdown-data-{Guid.NewGuid():N}");
    private readonly ServiceProvider _sp;

    public MutationShutdownTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_dbPath);
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* temp dir */ }
    }

    private IServiceScopeFactory Scopes => _sp.GetRequiredService<IServiceScopeFactory>();

    private static ShutdownDrain Drain(TimeSpan? budget = null) =>
        new(NullLogger<ShutdownDrain>.Instance, budget ?? TimeSpan.FromSeconds(30));

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_drain_admits_work_until_stopping_and_waits_for_what_runs()
    {
        var drain = Drain();
        var first = drain.TryEnter("first");
        Assert.NotNull(first);
        Assert.Equal(["first"], drain.InFlight);

        var stopping = drain.DrainAsync(CancellationToken.None);
        Assert.True(drain.IsStopping);
        Assert.Null(drain.TryEnter("second"));               // nothing new starts
        Assert.False(stopping.IsCompleted);                   // the running one is waited for
        Assert.False(drain.Abort.IsCancellationRequested);

        first!.Dispose();
        first.Dispose();                                      // a second dispose changes nothing
        await stopping.WaitAsync(Wait);
        Assert.Empty(drain.InFlight);
        Assert.True(drain.Abort.IsCancellationRequested);
    }

    [Fact]
    public async Task A_drain_with_nothing_running_ends_at_once()
    {
        var drain = Drain();
        await drain.DrainAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.True(drain.Abort.IsCancellationRequested);
    }

    [Theory]
    [InlineData(ScheduledTaskType.CustomCommand)]
    [InlineData(ScheduledTaskType.SelfBackup)]
    public async Task A_stop_during_a_scheduled_run_waits_for_it_and_starts_no_due_task(ScheduledTaskType type)
    {
        await AddTaskAsync("t1", type);
        var executor = new GatedExecutor();
        var drain = Drain();
        var scheduler = Scheduler(executor, drain);

        await scheduler.CheckAndRunDueTasks(CancellationToken.None);
        await executor.Entered.Task.WaitAsync(Wait);
        Assert.Null((await RunAsync("t1")).CompletedAt);      // on record before it ran

        var stopping = drain.DrainAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        Assert.Equal(["scheduled task 't1'"], drain.InFlight);

        // Another task falls due while Whiskers stops: it is not started and stays due for after the restart.
        var due = await AddTaskAsync("t2", type);
        await scheduler.CheckAndRunDueTasks(CancellationToken.None);
        Assert.Equal(["t1"], executor.Ran);
        Assert.Equal(due, (await TaskAsync("t2")).NextRun);
        Assert.Null((await TaskAsync("t2")).LastRun);

        executor.Release.SetResult((true, "done"));
        await stopping.WaitAsync(Wait);

        var run = await RunAsync("t1");
        Assert.NotNull(run.CompletedAt);                      // the drain ended after the run was recorded
        Assert.True(run.Success);
        Assert.Equal("done", run.Output);
        Assert.True((await TaskAsync("t1")).LastSuccess);
        Assert.False(await AnyRunAsync("t2"));
    }

    [Fact]
    public async Task A_forced_stop_leaves_the_run_open_and_the_next_start_reports_it_interrupted()
    {
        await AddTaskAsync("t1", ScheduledTaskType.SelfBackup);
        var executor = new GatedExecutor();                    // never ends: the stop cuts it off
        var drain = Drain(TimeSpan.FromMilliseconds(100));
        var scheduler = Scheduler(executor, drain);

        await scheduler.CheckAndRunDueTasks(CancellationToken.None);
        await executor.Entered.Task.WaitAsync(Wait);
        await drain.DrainAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.True(drain.Abort.IsCancellationRequested);     // the budget is spent
        Assert.Equal(["scheduled task 't1'"], drain.InFlight); // ... and the run is cut off
        Assert.Null((await RunAsync("t1")).CompletedAt);

        // A run of the current process is left alone: it is running right now.
        var notifications = new FakeNotifications();
        Assert.Empty(await Reconciler(notifications, DateTime.UtcNow.AddHours(-1)).ReconcileAsync());
        Assert.Null((await RunAsync("t1")).CompletedAt);
        Assert.Empty(notifications.Events);

        // The next process: the run is marked interrupted, not repeated, and an admin is told.
        var marked = await Reconciler(notifications, DateTime.UtcNow.AddSeconds(5)).ReconcileAsync();
        Assert.Contains(marked, m => m.StartsWith("scheduled task t1", StringComparison.Ordinal));
        var run = await RunAsync("t1");
        Assert.NotNull(run.CompletedAt);
        Assert.False(run.Success);
        Assert.Equal(InterruptedWorkReconciler.InterruptedResult, run.Error);
        var task = await TaskAsync("t1");
        Assert.False(task.LastSuccess);
        Assert.Equal(InterruptedWorkReconciler.InterruptedResult, task.LastResult);
        var note = Assert.Single(notifications.Events);
        Assert.Equal("operations_interrupted", note.EventType);
        Assert.Contains("t1", note.ImageInfo);
        Assert.Equal(1, executor.Ran.Count);                  // nothing ran it again

        Assert.Empty(await Reconciler(notifications, DateTime.UtcNow.AddSeconds(5)).ReconcileAsync());   // once
    }

    [Fact]
    public async Task A_running_operation_of_an_earlier_process_becomes_interrupted_and_never_runs_again()
    {
        var before = new DbOperationJournal(Scopes);
        var request = Claim("op-1");
        Assert.Equal(OperationClaimResult.Claimed, (await before.ClaimAsync(request)).Result);

        var after = new DbOperationJournal(Scopes);            // the process after the restart
        var own = Claim("op-2");
        Assert.Equal(OperationClaimResult.Claimed, (await after.ClaimAsync(own)).Result);

        var notifications = new FakeNotifications();
        var marked = await new InterruptedWorkReconciler(Scopes, after, notifications,
            NullLogger<InterruptedWorkReconciler>.Instance, DateTime.UtcNow).ReconcileAsync();

        Assert.StartsWith("container.restart (started", Assert.Single(marked), StringComparison.Ordinal);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var row = await db.Operations.SingleAsync(o => o.Id == "op-1");
            Assert.Equal(DbOperationJournal.Interrupted, row.State);
            Assert.NotNull(row.FinishedAt);
            Assert.Equal(DbOperationJournal.Running, (await db.Operations.SingleAsync(o => o.Id == "op-2")).State);
        }
        Assert.Equal(OperationClaimResult.OutcomeUnknown, (await after.ClaimAsync(request)).Result);
        Assert.Equal(OperationClaimResult.OutcomeUnknown, await after.InspectAsync(request.OperationId, request.InputHash));
        Assert.Equal("operations_interrupted", Assert.Single(notifications.Events).EventType);
    }

    [Fact]
    public async Task A_stop_during_a_recreate_waits_for_it_and_updates_no_further_container()
    {
        using var rig = new AutoRollbackTests();
        var drain = Drain();
        rig.UpdateThatLaterReads(_ => Task.FromResult(("running", 0, false)));
        var service = await rig.ServiceAsync(autoRollback: false, drain: drain, second: "api");

        // The recreate blocks until the test lets it go.
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var recreate = rig.Docker.On[nameof(IDockerService.RecreateContainerAsync)];
        rig.Docker.On[nameof(IDockerService.RecreateContainerAsync)] = args =>
        {
            entered.Set();
            Assert.True(release.Wait(Wait));
            return recreate(args);
        };

        var updating = Task.Run(() => service.CheckAndUpdateAsync(CancellationToken.None));
        Assert.True(entered.Wait(Wait));

        // Whiskers is told to stop while the container is being recreated: the drain waits for it.
        var stopping = drain.DrainAsync(CancellationToken.None);
        Assert.Equal(["auto-update of 'web'"], drain.InFlight);
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted);

        release.Set();
        await updating.WaitAsync(Wait);
        await stopping.WaitAsync(Wait);
        Assert.Single(rig.Docker.Calls, c => c == nameof(IDockerService.RecreateContainerAsync));   // "api" never
        using var scope = rig.Scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var history = await db.UpdateHistory.SingleAsync();
        Assert.Equal("web", history.ContainerName);
        Assert.True(history.Success);                         // the started update ran to its end
    }

    [Fact]
    public async Task A_stop_during_the_nightly_cycle_patches_no_further_container()
    {
        var cves = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        cves.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        cves.Set(AutoPatchTests.Scan("b", "local", (CveSeverity.Critical, "1")));
        var drain = Drain();
        Task? stopping = null;
        var runner = new AutoPatchTests.RecordingRunner
        {
            AfterPatch = name => { if (name == "a") stopping = drain.DrainAsync(CancellationToken.None); },
        };
        var service = AutoPatchTests.NewService(runner, cves, new AutoPatchSettings { Enabled = true, DryRun = false },
            new MemoryAutomationOwners(), () => DateTime.UtcNow, new InMemoryAutoPatchNightlyStore(), drain,
            AutoPatchTests.Compose("a"), AutoPatchTests.Compose("b"));

        Assert.True(await service.RunCycleAsync(forceDryRun: false, "admin@example.com"));
        await WaitIdleAsync(service);

        Assert.NotNull(stopping);
        await stopping!.WaitAsync(Wait);
        Assert.Equal(["a"], runner.Calls.Select(c => c.Name));
        Assert.Contains("Stopped before b", service.LastCycle!.Error);
    }

    [Fact]
    public async Task The_nightly_day_survives_a_restart_and_an_interrupted_night_is_reported_not_repeated()
    {
        var settings = new AutoPatchSettings { Enabled = true, RunAtHourUtc = 3 };
        var night = new DateTime(2026, 11, 3, 3, 10, 0, DateTimeKind.Utc);
        var paths = new DataPathOptions(_dataDir);

        // Before the restart: the night ran (no owner, so it did nothing) and is on disk.
        var first = AutoPatchTests.NewService(new AutoPatchTests.RecordingRunner(), new CveFindingsStore(persistPath: AutoPatchTests.TempPath()),
            settings, new MemoryAutomationOwners(), () => night, new AutoPatchNightlyStore(paths), Drain());
        await first.LoadNightlyAsync();
        Assert.True(AutoPatchService.IsDue(settings, night, first.LastNightlyRun));
        await first.RunNightlyRecordedAsync(CancellationToken.None);

        var restarted = AutoPatchTests.NewService(new AutoPatchTests.RecordingRunner(), new CveFindingsStore(persistPath: AutoPatchTests.TempPath()),
            settings, new MemoryAutomationOwners(), () => night.AddMinutes(5), new AutoPatchNightlyStore(paths), Drain());
        await restarted.LoadNightlyAsync();
        Assert.False(AutoPatchService.IsDue(settings, night.AddMinutes(5), restarted.LastNightlyRun));   // not twice
        Assert.StartsWith("Not run", restarted.LastCycle!.Error);   // why it did nothing, still after the restart

        // A night that started and never ended (a forced stop) is shown as interrupted and still not repeated.
        await new AutoPatchNightlyStore(paths).SaveAsync(new AutoPatchNightlyState
        {
            Day = DateOnly.FromDateTime(night), StartedAt = night, CompletedAt = null,
        });
        var afterCrash = AutoPatchTests.NewService(new AutoPatchTests.RecordingRunner(), new CveFindingsStore(persistPath: AutoPatchTests.TempPath()),
            settings, new MemoryAutomationOwners(), () => night.AddMinutes(20), new AutoPatchNightlyStore(paths), Drain());
        await afterCrash.LoadNightlyAsync();
        Assert.False(AutoPatchService.IsDue(settings, night.AddMinutes(20), afterCrash.LastNightlyRun));
        Assert.StartsWith("Interrupted", afterCrash.LastCycle!.Error);
        Assert.Null(afterCrash.LastCycle.CompletedAt);
        Assert.True(AutoPatchService.IsDue(settings, night.AddDays(1), afterCrash.LastNightlyRun));   // the next night runs
    }

    [Fact]
    public async Task The_host_drains_before_it_cancels_the_loops_and_waits_for_running_work()
    {
        var drain = Drain();
        var worker = new HoldingWorker(drain);
        using var host = Host(drain, worker, TimeSpan.FromSeconds(30));
        await host.StartAsync();
        await worker.Entered.Task.WaitAsync(Wait);

        var stop = host.StopAsync();
        await UntilAsync(() => drain.IsStopping);
        Assert.False(worker.Token.IsCancellationRequested);   // the drain runs before the loops are cancelled
        await Task.Delay(100);
        Assert.False(stop.IsCompleted);                        // ... and waits for the running work

        worker.Release.SetResult();
        await stop.WaitAsync(Wait);
        Assert.Empty(drain.InFlight);
        Assert.True(drain.Abort.IsCancellationRequested);
    }

    [Fact]
    public async Task The_host_stops_after_the_budget_when_work_does_not_end()
    {
        var drain = Drain(TimeSpan.FromMilliseconds(200));
        var worker = new HoldingWorker(drain);                 // never released: a forced stop
        using var host = Host(drain, worker, TimeSpan.FromSeconds(2));
        await host.StartAsync();
        await worker.Entered.Task.WaitAsync(Wait);

        await host.StopAsync().WaitAsync(Wait);

        Assert.True(drain.Abort.IsCancellationRequested);
        Assert.True(worker.Token.IsCancellationRequested);
        Assert.Equal(["held work"], drain.InFlight);           // cut off: reconciled at the next start
    }

    [Fact]
    public async Task Run_now_is_admitted_like_a_scheduled_run()
    {
        await AddTaskAsync("t1", ScheduledTaskType.SelfBackup);
        var executor = new GatedExecutor();
        var drain = Drain();
        var scheduler = Scheduler(executor, drain);

        var running = scheduler.RunNowAsync("t1");
        await executor.Entered.Task.WaitAsync(Wait);
        Assert.Null((await RunAsync("t1")).CompletedAt);
        Assert.Equal((await RunAsync("t1")).StartedAt, (await TaskAsync("t1")).LastRun);

        // Not twice at once: neither by hand nor by the schedule.
        Assert.Equal((false, "The task is already running."), await scheduler.RunNowAsync("t1"));
        await scheduler.CheckAndRunDueTasks(CancellationToken.None);
        Assert.Equal(["t1"], executor.Ran);

        var stopping = drain.DrainAsync(CancellationToken.None);
        Assert.Equal(["scheduled task 't1' (run now)"], drain.InFlight);
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted);
        executor.Release.SetResult((true, "done"));
        Assert.Equal((true, "done"), await running.WaitAsync(Wait));
        await stopping.WaitAsync(Wait);
        Assert.True((await RunAsync("t1")).Success);

        Assert.Equal((false, "Whiskers is shutting down; the task was not started."), await scheduler.RunNowAsync("t1"));
        Assert.Equal(["t1"], executor.Ran);
    }

    [Fact]
    public async Task An_occurrence_due_while_the_task_runs_is_skipped_without_touching_its_last_run()
    {
        await AddTaskAsync("t1", ScheduledTaskType.CustomCommand);
        var executor = new GatedExecutor();
        var scheduler = Scheduler(executor, Drain());
        var running = scheduler.RunNowAsync("t1");
        await executor.Entered.Task.WaitAsync(Wait);
        var lastRun = (await TaskAsync("t1")).LastRun;

        await scheduler.CheckAndRunDueTasks(CancellationToken.None);

        var task = await TaskAsync("t1");
        Assert.Equal(lastRun, task.LastRun);                   // still the running one's start
        Assert.True(task.NextRun > DateTime.UtcNow);           // the occurrence is skipped, not queued
        Assert.Single(await RunsAsync("t1"));
        executor.Release.SetResult((true, "done"));
        await running.WaitAsync(Wait);
    }

    [Fact]
    public async Task Reconciling_never_replaces_the_result_of_a_later_run()
    {
        await AddTaskAsync("t1", ScheduledTaskType.CustomCommand);
        var old = DateTime.UtcNow.AddHours(-2);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.TaskRunHistory.Add(new TaskRunHistoryEntity { TaskId = "t1", TaskName = "t1", StartedAt = old });
            var task = await db.ScheduledTasks.SingleAsync(t => t.TaskId == "t1");
            task.LastRun = old.AddHours(1);                    // a later run has recorded its result since
            task.LastSuccess = true;
            task.LastResult = "later";
            await db.SaveChangesAsync();
        }

        await Reconciler(new FakeNotifications(), DateTime.UtcNow).ReconcileAsync();

        Assert.Equal(InterruptedWorkReconciler.InterruptedResult, (await RunAsync("t1")).Error);
        var after = await TaskAsync("t1");
        Assert.Equal("later", after.LastResult);
        Assert.True(after.LastSuccess);
    }

    [Fact]
    public async Task An_interrupted_auto_update_is_not_tried_again_and_its_policy_is_switched_off()
    {
        using var rig = new AutoRollbackTests();
        rig.UpdateThatLaterReads(_ => Task.FromResult(("running", 0, false)));
        var service = await rig.ServiceAsync(autoRollback: false);
        using (var scope = rig.Scopes.CreateScope())
        {
            // The earlier process recorded the attempt and was killed before its end.
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.UpdateHistory.Add(new UpdateHistoryEntity
            {
                ContainerId = "aaaaaaaaaaaa1111", ContainerName = "web", ServerId = "local", Image = "nginx:latest",
                StartedAt = DateTime.UtcNow.AddMinutes(-5), Error = AutoUpdateService.InProgressResult,
            });
            await db.SaveChangesAsync();
        }

        // Before the start-up reconciliation: the update is still available, but not started again.
        await service.CheckAndUpdateAsync(CancellationToken.None);
        Assert.DoesNotContain(nameof(IDockerService.RecreateContainerAsync), rig.Docker.Calls);

        var notifications = new FakeNotifications();
        var marked = await new InterruptedWorkReconciler(rig.Scopes, new DbOperationJournal(rig.Scopes), notifications,
            NullLogger<InterruptedWorkReconciler>.Instance, DateTime.UtcNow).ReconcileAsync();

        Assert.Contains(marked, m => m.StartsWith("auto-update of web", StringComparison.Ordinal));
        using (var scope = rig.Scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var history = await db.UpdateHistory.SingleAsync();
            Assert.NotNull(history.CompletedAt);
            Assert.False(history.Success);
            Assert.Equal(InterruptedWorkReconciler.InterruptedResult, history.Error);
            var policy = await db.UpdatePolicies.SingleAsync();
            Assert.False(policy.AutoUpdate);
            Assert.Equal(InterruptedWorkReconciler.InterruptedUpdateResult, policy.LastUpdateResult);
        }
        Assert.Contains("web", Assert.Single(notifications.Events).ImageInfo);

        // After an admin switched it on again, the update runs and is recorded with its end.
        using (var scope = rig.Scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            (await db.UpdatePolicies.SingleAsync()).AutoUpdate = true;
            await db.SaveChangesAsync();
        }
        await service.CheckAndUpdateAsync(CancellationToken.None);
        Assert.Single(rig.Docker.Calls, c => c == nameof(IDockerService.RecreateContainerAsync));
        using (var scope = rig.Scopes.CreateScope())
        {
            var latest = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().UpdateHistory
                .OrderByDescending(h => h.Id).FirstAsync();
            Assert.True(latest.Success);
            Assert.NotNull(latest.CompletedAt);
            Assert.Null(latest.Error);
        }
    }

    [Fact]
    public async Task A_night_whose_start_cannot_be_saved_patches_nothing()
    {
        var cves = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        cves.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        var owners = new MemoryAutomationOwners();
        await owners.SetAsync(IAutomationOwnerStore.AutoPatch, "user:admin@example.com");
        var runner = new AutoPatchTests.RecordingRunner();
        var service = AutoPatchTests.NewService(runner, cves, new AutoPatchSettings { Enabled = true, DryRun = false },
            owners, () => DateTime.UtcNow, new FailingNightlyStore(), Drain(), AutoPatchTests.Compose("a"));

        await service.RunNightlyRecordedAsync(CancellationToken.None);

        Assert.Empty(runner.Calls);
        Assert.Equal(AutoPatchService.NightNotRecorded, service.LastCycle!.Error);
    }

    [Fact]
    public async Task A_night_stopped_early_still_says_why_after_a_restart()
    {
        var cves = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        cves.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        cves.Set(AutoPatchTests.Scan("b", "local", (CveSeverity.Critical, "1")));
        var owners = new MemoryAutomationOwners();
        await owners.SetAsync(IAutomationOwnerStore.AutoPatch, "user:admin@example.com");
        var drain = Drain();
        Task? stopping = null;
        var runner = new AutoPatchTests.RecordingRunner
        {
            AfterPatch = name => { if (name == "a") stopping = drain.DrainAsync(CancellationToken.None); },
        };
        var paths = new DataPathOptions(_dataDir);
        var settings = new AutoPatchSettings { Enabled = true, DryRun = false };
        var night = DateTime.UtcNow;                            // one instant throughout: no midnight in between
        var service = AutoPatchTests.NewService(runner, cves, settings, owners, () => night,
            new AutoPatchNightlyStore(paths), drain, AutoPatchTests.Compose("a"), AutoPatchTests.Compose("b"));

        await service.RunNightlyRecordedAsync(CancellationToken.None);
        await stopping!.WaitAsync(Wait);
        Assert.Equal(["a"], runner.Calls.Select(c => c.Name));

        var restarted = AutoPatchTests.NewService(new AutoPatchTests.RecordingRunner(), cves, settings, owners,
            () => night, new AutoPatchNightlyStore(paths), Drain());
        await restarted.LoadNightlyAsync();
        Assert.Contains("Stopped before b", restarted.LastCycle!.Error);
        Assert.NotNull(restarted.LastCycle.CompletedAt);
        Assert.False(AutoPatchService.IsDue(new AutoPatchSettings { Enabled = true, RunAtHourUtc = night.Hour },
            night, restarted.LastNightlyRun));
    }

    private static IHost Host(ShutdownDrain drain, HoldingWorker worker, TimeSpan shutdownTimeout) =>
        new HostBuilder().ConfigureServices(s =>
        {
            s.AddLogging();
            s.Configure<HostOptions>(o => o.ShutdownTimeout = shutdownTimeout);
            s.AddHostedService(_ => drain);
            s.AddHostedService(_ => worker);
        }).Build();

    private static async Task UntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>A loop that starts one piece of unattended work and holds it until the test releases it.</summary>
    private sealed class HoldingWorker(IShutdownDrain drain) : BackgroundService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Token = stoppingToken;
            using var held = drain.TryEnter("held work");
            Entered.TrySetResult();
            await Release.Task;   // the work does not take the loop's token: the drain is what waits for it
        }
    }

    private sealed class FailingNightlyStore : IAutoPatchNightlyStore
    {
        public Task<AutoPatchNightlyState> LoadAsync() => Task.FromResult(new AutoPatchNightlyState());
        public Task SaveAsync(AutoPatchNightlyState state) => throw new IOException("disk full");
    }

    private async Task<List<TaskRunHistoryEntity>> RunsAsync(string id)
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().TaskRunHistory.AsNoTracking()
            .Where(r => r.TaskId == id).ToListAsync();
    }

    private static async Task WaitIdleAsync(AutoPatchService service)
    {
        var until = DateTime.UtcNow + Wait;
        while (service.IsRunning && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task An_interrupted_update_is_closed_only_together_with_switching_its_policy_off()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.UpdatePolicies.Add(new UpdatePolicyEntity { ContainerId = "w1", ContainerName = "web", ServerId = "local", AutoUpdate = true });
            db.UpdateHistory.Add(new UpdateHistoryEntity
            {
                ContainerId = "w1", ContainerName = "web", ServerId = "local", StartedAt = DateTime.UtcNow.AddMinutes(-5),
                Error = AutoUpdateService.InProgressResult,
            });
            await db.SaveChangesAsync();
        }

        // The policy write fails (a stop, a database error): the attempt must stay open, so nothing re-runs it.
        using (var failing = ProviderWith(new FailingCommands("UpdatePolicies")))
        {
            var scopes = failing.GetRequiredService<IServiceScopeFactory>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => new InterruptedWorkReconciler(scopes,
                new DbOperationJournal(scopes), new FakeNotifications(), NullLogger<InterruptedWorkReconciler>.Instance,
                DateTime.UtcNow).ReconcileAsync());
        }
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            Assert.Null((await db.UpdateHistory.SingleAsync()).CompletedAt);
            Assert.True((await db.UpdatePolicies.SingleAsync()).AutoUpdate);
        }

        // The next pass does both.
        await Reconciler(new FakeNotifications(), DateTime.UtcNow).ReconcileAsync();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            Assert.NotNull((await db.UpdateHistory.SingleAsync()).CompletedAt);
            Assert.False((await db.UpdatePolicies.SingleAsync()).AutoUpdate);
        }
    }

    [Fact]
    public async Task A_run_whose_record_cannot_be_saved_neither_runs_nor_consumes_its_occurrence()
    {
        var due = await AddTaskAsync("t1", ScheduledTaskType.CustomCommand);
        var executor = new GatedExecutor();
        using var failing = ProviderWith(new FailingRunInsert());
        var scheduler = new SchedulerService(failing.GetRequiredService<IServiceScopeFactory>(), executor, null!,
            NullLogger<SchedulerService>.Instance, Drain());

        await scheduler.CheckAndRunDueTasks(CancellationToken.None);

        Assert.Empty(executor.Ran);
        Assert.False(await AnyRunAsync("t1"));
        var task = await TaskAsync("t1");
        Assert.Equal(due, task.NextRun);                       // still due: it runs at the next tick
        Assert.Null(task.LastRun);                             // nothing ran: no run time is invented
        Assert.False(task.LastSuccess);                        // the error was recorded
        Assert.StartsWith("FEHLER", task.LastResult);

        // The guard is free again: the next tick runs it, and its result is recorded before the fixture goes away.
        await scheduler.CheckAndRunDueTasks(CancellationToken.None);
        await executor.Entered.Task.WaitAsync(Wait);
        executor.Release.SetResult((true, "ok"));
        var until = DateTime.UtcNow + Wait;
        while ((await RunAsync("t1")).CompletedAt is null && DateTime.UtcNow < until) await Task.Delay(20);
        var run = await RunAsync("t1");
        Assert.True(run.Success);
        Assert.Equal("ok", (await TaskAsync("t1")).LastResult);
    }

    [Fact]
    public async Task A_failed_skip_of_a_running_task_records_no_result_of_its_own()
    {
        await AddTaskAsync("t1", ScheduledTaskType.CustomCommand);
        var failOnce = new FailingCommandsOnce("ScheduledTasks");
        using var provider = ProviderWith(failOnce);
        var executor = new GatedExecutor();
        var scheduler = new SchedulerService(provider.GetRequiredService<IServiceScopeFactory>(), executor, null!,
            NullLogger<SchedulerService>.Instance, Drain());
        var running = scheduler.RunNowAsync("t1");               // the task runs (by hand)
        await executor.Entered.Task.WaitAsync(Wait);

        failOnce.Armed = true;                                   // the save of the skipped occurrence fails
        await scheduler.CheckAndRunDueTasks(CancellationToken.None);

        var during = await TaskAsync("t1");
        Assert.False(during.LastResult?.StartsWith("FEHLER", StringComparison.Ordinal) ?? false);   // not its result to write
        executor.Release.SetResult((true, "ok"));
        Assert.True((await running).Success);
        Assert.Equal("ok", (await TaskAsync("t1")).LastResult);
    }

    [Fact]
    public async Task An_operation_mark_whose_commit_answer_was_lost_is_still_reported()
    {
        var before = new DbOperationJournal(Scopes);
        Assert.Equal(OperationClaimResult.Claimed, (await before.ClaimAsync(Claim("op-1"))).Result);

        var lost = new LostCommitOnce();
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}",
            sqlite => sqlite.ExecutionStrategy(d => new Retrying(d))).AddInterceptors(lost));
        using var retrying = services.BuildServiceProvider();
        var scopes = retrying.GetRequiredService<IServiceScopeFactory>();
        var notifications = new FakeNotifications();
        var marked = await new InterruptedWorkReconciler(scopes, new DbOperationJournal(scopes), notifications,
            NullLogger<InterruptedWorkReconciler>.Instance, DateTime.UtcNow).ReconcileAsync();

        Assert.True(lost.Fired);
        Assert.StartsWith("container.restart (started", Assert.Single(marked), StringComparison.Ordinal);
        Assert.Single(notifications.Events);
    }

    [Fact]
    public async Task An_operation_whose_target_cannot_be_read_is_still_marked_and_reported()
    {
        var before = new DbOperationJournal(Scopes);
        Assert.Equal(OperationClaimResult.Claimed, (await before.ClaimAsync(
            new OperationClaimRequest("op-1", null, "container.restart", "user:admin@example.com", "hash-op-1", null, null, 42))).Result);

        var notifications = new FakeNotifications();
        using var failing = ProviderWith(new FailingReads("AuditLog"));
        var scopes = failing.GetRequiredService<IServiceScopeFactory>();
        var marked = await new InterruptedWorkReconciler(scopes, new DbOperationJournal(scopes), notifications,
            NullLogger<InterruptedWorkReconciler>.Instance, DateTime.UtcNow).ReconcileAsync();

        Assert.StartsWith("container.restart (started", Assert.Single(marked), StringComparison.Ordinal);
        Assert.Contains("container.restart", Assert.Single(notifications.Events).ImageInfo);
    }

    [Fact]
    public async Task What_was_marked_before_a_failure_is_still_reported()
    {
        var before = new DbOperationJournal(Scopes);
        Assert.Equal(OperationClaimResult.Claimed, (await before.ClaimAsync(Claim("op-1"))).Result);
        await AddTaskAsync("t1", ScheduledTaskType.CustomCommand);
        var old = DateTime.UtcNow.AddHours(-1);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.TaskRunHistory.Add(new TaskRunHistoryEntity { TaskId = "t1", TaskName = "t1", StartedAt = old });
            (await db.ScheduledTasks.SingleAsync(t => t.TaskId == "t1")).LastRun = old;
            await db.SaveChangesAsync();
        }

        // The operation is marked; the task run's transaction then fails.
        var notifications = new FakeNotifications();
        using (var failing = ProviderWith(new FailingCommands("ScheduledTasks")))
        {
            var scopes = failing.GetRequiredService<IServiceScopeFactory>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => new InterruptedWorkReconciler(scopes,
                new DbOperationJournal(scopes), notifications, NullLogger<InterruptedWorkReconciler>.Instance,
                DateTime.UtcNow).ReconcileAsync());
        }
        var report = Assert.Single(notifications.Events);
        Assert.Contains("container.restart", report.ImageInfo);
        Assert.DoesNotContain("scheduled task", report.ImageInfo);

        // The next start marks and reports the run that stayed open — and not the operation a second time.
        var next = new FakeNotifications();
        var marked = await Reconciler(next, DateTime.UtcNow).ReconcileAsync();
        Assert.StartsWith("scheduled task t1", Assert.Single(marked), StringComparison.Ordinal);
        Assert.Single(next.Events);
    }

    [Fact]
    public async Task The_nightly_file_keeps_no_secret_from_a_failure_reason()
    {
        var paths = new DataPathOptions(_dataDir);
        var store = new AutoPatchNightlyStore(paths);
        await store.SaveAsync(new AutoPatchNightlyState
        {
            Day = new DateOnly(2026, 11, 3), StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            Error = "Failed: login to https://deploy:s3cr3t-pw@registry.example.com failed, password=hunter2-canary",
        });

        var text = await File.ReadAllTextAsync(paths.AutoPatchNightlyJson);
        Assert.DoesNotContain("s3cr3t-pw", text);
        Assert.DoesNotContain("hunter2-canary", text);
        var restored = (await store.LoadAsync()).Error!;
        Assert.DoesNotContain("hunter2-canary", restored);
        Assert.StartsWith("Failed:", restored);
    }

    [Fact]
    public async Task Reconciling_works_under_a_retrying_execution_strategy()
    {
        // PostgreSQL is registered with EnableRetryOnFailure, which refuses a transaction opened outside the strategy.
        await AddTaskAsync("t1", ScheduledTaskType.CustomCommand);
        var old = DateTime.UtcNow.AddHours(-1);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.TaskRunHistory.Add(new TaskRunHistoryEntity { TaskId = "t1", TaskName = "t1", StartedAt = old });
            (await db.ScheduledTasks.SingleAsync(t => t.TaskId == "t1")).LastRun = old;
            db.UpdatePolicies.Add(new UpdatePolicyEntity { ContainerId = "w1", ContainerName = "web", ServerId = "local", AutoUpdate = true });
            db.UpdateHistory.Add(new UpdateHistoryEntity
            {
                ContainerId = "w1", ContainerName = "web", ServerId = "local", StartedAt = old,
                Error = AutoUpdateService.InProgressResult,
            });
            await db.SaveChangesAsync();
        }

        // The first transaction commits but its answer is lost: the strategy retries it.
        var lost = new LostCommitOnce();
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}",
            sqlite => sqlite.ExecutionStrategy(d => new Retrying(d))).AddInterceptors(lost));
        using var retrying = services.BuildServiceProvider();
        var scopes = retrying.GetRequiredService<IServiceScopeFactory>();
        var notifications = new FakeNotifications();
        var marked = await new InterruptedWorkReconciler(scopes, new DbOperationJournal(scopes), notifications,
            NullLogger<InterruptedWorkReconciler>.Instance, DateTime.UtcNow).ReconcileAsync();

        // Each committed mark is reported once — the retried one too.
        Assert.True(lost.Fired);
        Assert.Equal(2, marked.Count);
        Assert.Single(marked, m => m.StartsWith("scheduled task t1", StringComparison.Ordinal));
        Assert.Single(marked, m => m.StartsWith("auto-update of web", StringComparison.Ordinal));
        Assert.Single(notifications.Events);
        Assert.Equal(InterruptedWorkReconciler.InterruptedResult, (await RunAsync("t1")).Error);
        Assert.Equal(InterruptedWorkReconciler.InterruptedResult, (await TaskAsync("t1")).LastResult);
        using var check = _sp.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<MetricsDbContext>();
        Assert.False((await after.UpdatePolicies.SingleAsync()).AutoUpdate);
    }

    [Fact]
    public async Task An_interrupted_run_is_closed_only_together_with_its_task_result()
    {
        await AddTaskAsync("t1", ScheduledTaskType.CustomCommand);
        var old = DateTime.UtcNow.AddHours(-1);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.TaskRunHistory.Add(new TaskRunHistoryEntity { TaskId = "t1", TaskName = "t1", StartedAt = old });
            var task = await db.ScheduledTasks.SingleAsync(t => t.TaskId == "t1");
            (task.LastRun, task.LastSuccess, task.LastResult) = (old, true, "before");
            await db.SaveChangesAsync();
        }

        // The task write fails: the run must stay open, so the next start still repairs the task's result.
        using (var failing = ProviderWith(new FailingCommands("ScheduledTasks")))
        {
            var scopes = failing.GetRequiredService<IServiceScopeFactory>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => new InterruptedWorkReconciler(scopes,
                new DbOperationJournal(scopes), new FakeNotifications(), NullLogger<InterruptedWorkReconciler>.Instance,
                DateTime.UtcNow).ReconcileAsync());
        }
        Assert.Null((await RunAsync("t1")).CompletedAt);
        Assert.Equal("before", (await TaskAsync("t1")).LastResult);

        var notifications = new FakeNotifications();
        await Reconciler(notifications, DateTime.UtcNow).ReconcileAsync();
        Assert.Equal(InterruptedWorkReconciler.InterruptedResult, (await RunAsync("t1")).Error);
        var repaired = await TaskAsync("t1");
        Assert.Equal(InterruptedWorkReconciler.InterruptedResult, repaired.LastResult);
        Assert.False(repaired.LastSuccess);
        Assert.Single(notifications.Events);
    }

    [Fact]
    public async Task A_night_cancelled_during_its_last_container_is_reported_after_a_restart()
    {
        var cves = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        cves.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        var owners = new MemoryAutomationOwners();
        await owners.SetAsync(IAutomationOwnerStore.AutoPatch, "user:admin@example.com");
        var paths = new DataPathOptions(_dataDir);
        var settings = new AutoPatchSettings { Enabled = true, DryRun = false };
        using var stopping = new CancellationTokenSource();
        // The host stops while the only container is being patched.
        var runner = new AutoPatchTests.RecordingRunner { AfterPatch = _ => stopping.Cancel() };
        var service = AutoPatchTests.NewService(runner, cves, settings, owners, () => DateTime.UtcNow,
            new AutoPatchNightlyStore(paths), Drain(), AutoPatchTests.Compose("a"));

        try { await service.RunNightlyRecordedAsync(stopping.Token); }
        catch (OperationCanceledException) { /* cut off: recorded without an end */ }

        Assert.Single(runner.Calls);
        var restarted = AutoPatchTests.NewService(new AutoPatchTests.RecordingRunner(), cves, settings, owners,
            () => DateTime.UtcNow, new AutoPatchNightlyStore(paths), Drain());
        await restarted.LoadNightlyAsync();
        Assert.NotNull(restarted.LastCycle?.Error);            // never a clean night
    }

    [Fact]
    public async Task An_unreadable_nightly_state_runs_no_night_and_keeps_no_older_generation()
    {
        var paths = new DataPathOptions(_dataDir);
        var store = new AutoPatchNightlyStore(paths);
        await store.SaveAsync(new AutoPatchNightlyState { Day = new DateOnly(2026, 11, 2) });
        await store.SaveAsync(new AutoPatchNightlyState { Day = new DateOnly(2026, 11, 3), StartedAt = DateTime.UtcNow });
        Assert.False(File.Exists(paths.AutoPatchNightlyJson + ".bak"));   // no previous night to fall back to

        await File.WriteAllTextAsync(paths.AutoPatchNightlyJson, "{ torn");
        var service = AutoPatchTests.NewService(new AutoPatchTests.RecordingRunner(),
            new CveFindingsStore(persistPath: AutoPatchTests.TempPath()), new AutoPatchSettings { Enabled = true },
            new MemoryAutomationOwners(), () => DateTime.UtcNow, store, Drain());

        await Assert.ThrowsAnyAsync<Exception>(service.LoadNightlyAsync);
        Assert.Equal(AutoPatchService.NightStateUnreadable, service.LastCycle!.Error);
    }

    [Fact]
    public async Task A_night_cancelled_before_its_first_container_is_not_recorded_as_complete()
    {
        var cves = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        cves.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        var owners = new MemoryAutomationOwners();
        await owners.SetAsync(IAutomationOwnerStore.AutoPatch, "user:admin@example.com");
        var paths = new DataPathOptions(_dataDir);
        var settings = new AutoPatchSettings { Enabled = true, DryRun = false };
        var runner = new AutoPatchTests.RecordingRunner();
        var service = AutoPatchTests.NewService(runner, cves, settings, owners, () => DateTime.UtcNow,
            new AutoPatchNightlyStore(paths), Drain(), AutoPatchTests.Compose("a"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();                                    // the host stops while the night is being planned

        try { await service.RunNightlyRecordedAsync(cancelled.Token); }
        catch (OperationCanceledException) { /* cut off: recorded without an end */ }

        Assert.Empty(runner.Calls);
        var restarted = AutoPatchTests.NewService(new AutoPatchTests.RecordingRunner(), cves, settings, owners,
            () => DateTime.UtcNow, new AutoPatchNightlyStore(paths), Drain());
        await restarted.LoadNightlyAsync();
        Assert.NotNull(restarted.LastCycle?.Error);            // interrupted or stopped — never a clean night
    }

    /// <summary>An execution strategy that retries, as PostgreSQL's <c>EnableRetryOnFailure</c> does.</summary>
    private sealed class Retrying(Microsoft.EntityFrameworkCore.Storage.ExecutionStrategyDependencies dependencies)
        : Microsoft.EntityFrameworkCore.Storage.ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(10))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientFailure;
    }

    private sealed class TransientFailure() : Exception("transient");

    /// <summary>The first commit happens, but its answer is lost: the caller sees a transient failure and retries.</summary>
    private sealed class LostCommitOnce : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        private int _done;
        public bool Fired => Volatile.Read(ref _done) == 1;

        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken ct = default)
            => Interlocked.Exchange(ref _done, 1) == 0 ? throw new TransientFailure() : Task.CompletedTask;
    }

    /// <summary>Fails every read of the named table.</summary>
    private sealed class FailingReads(string table) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
            => command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal)
                ? throw new InvalidOperationException("the read failed")
                : ValueTask.FromResult(result);
    }

    /// <summary>A provider on the same database with one interceptor added.</summary>
    private ServiceProvider ProviderWith(Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor interceptor)
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}").AddInterceptors(interceptor));
        return services.BuildServiceProvider();
    }

    /// <summary>Fails every command that writes the named table.</summary>
    private sealed class FailingCommands(string table) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
            => command.CommandText.Contains($"UPDATE \"{table}\"", StringComparison.Ordinal)
                ? throw new InvalidOperationException("the database went away")
                : ValueTask.FromResult(result);
    }

    /// <summary>Fails the next command that writes the named table once it is armed.</summary>
    private sealed class FailingCommandsOnce(string table) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public volatile bool Armed;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Armed && command.CommandText.Contains($"UPDATE \"{table}\"", StringComparison.Ordinal))
            {
                Armed = false;
                throw new InvalidOperationException("the database went away");
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        {
            if (Armed && command.CommandText.Contains($"UPDATE \"{table}\"", StringComparison.Ordinal))
            {
                Armed = false;
                throw new InvalidOperationException("the database went away");
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Fails the save that would put a run on record, once.</summary>
    private sealed class FailingRunInsert : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private int _failed;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            var adding = eventData.Context!.ChangeTracker.Entries<TaskRunHistoryEntity>().Any(e => e.State == EntityState.Added);
            if (adding && Interlocked.Exchange(ref _failed, 1) == 0)
                throw new InvalidOperationException("disk full");
            return ValueTask.FromResult(result);
        }
    }

    private SchedulerService Scheduler(ITaskExecutor executor, IShutdownDrain drain) =>
        new(Scopes, executor, null!, NullLogger<SchedulerService>.Instance, drain);

    private InterruptedWorkReconciler Reconciler(FakeNotifications notifications, DateTime processStart) =>
        new(Scopes, new DbOperationJournal(Scopes), notifications, NullLogger<InterruptedWorkReconciler>.Instance, processStart);

    private static OperationClaimRequest Claim(string id) =>
        new(id, null, "container.restart", "user:admin@example.com", "hash-" + id, null, null, null);

    /// <summary>Adds an enabled task that is due now; returns its NextRun.</summary>
    private async Task<DateTime> AddTaskAsync(string id, ScheduledTaskType type)
    {
        var due = DateTime.UtcNow.AddMinutes(-1);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.ScheduledTasks.Add(new ScheduledTaskEntity
        {
            TaskId = id, Name = id, CronExpression = "0 3 * * *", TaskType = type, Enabled = true, NextRun = due,
            CreatedBy = "user:admin@example.com",
        });
        await db.SaveChangesAsync();
        return due;
    }

    private async Task<ScheduledTaskEntity> TaskAsync(string id)
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().ScheduledTasks.AsNoTracking()
            .SingleAsync(t => t.TaskId == id);
    }

    private async Task<TaskRunHistoryEntity> RunAsync(string id)
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().TaskRunHistory.AsNoTracking()
            .SingleAsync(r => r.TaskId == id);
    }

    private async Task<bool> AnyRunAsync(string id)
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().TaskRunHistory.AnyAsync(r => r.TaskId == id);
    }

    /// <summary>A task that runs until the test releases it.</summary>
    private sealed class GatedExecutor : ITaskExecutor
    {
        private readonly List<string> _ran = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(bool, string)> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Ran
        {
            get { lock (_ran) return [.. _ran]; }
        }

        public async Task<(bool Success, string Output)> ExecuteAsync(ScheduledTaskEntity task)
        {
            lock (_ran) _ran.Add(task.TaskId);
            Entered.TrySetResult();
            return await Release.Task;
        }
    }
}
