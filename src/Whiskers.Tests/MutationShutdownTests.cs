using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.AutoPatch;
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

        Task? stopping = null;
        var recreate = rig.Docker.On[nameof(IDockerService.RecreateContainerAsync)];
        rig.Docker.On[nameof(IDockerService.RecreateContainerAsync)] = args =>
        {
            // Whiskers is told to stop while the container is being recreated.
            stopping = drain.DrainAsync(CancellationToken.None);
            return recreate(args);
        };

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.NotNull(stopping);
        await stopping!.WaitAsync(Wait);
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
        Assert.Null(restarted.LastCycle);                     // it ended normally

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

    private static async Task WaitIdleAsync(AutoPatchService service)
    {
        var until = DateTime.UtcNow + Wait;
        while (service.IsRunning && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.False(service.IsRunning);
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
