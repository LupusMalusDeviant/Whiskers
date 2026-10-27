using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.AutoPatch;
using Whiskers.Services.AutoUpdate;
using Whiskers.Services.Cve;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Scheduler;

namespace Whiskers.Tests;

/// <summary>
/// R1-01: an unattended run has nobody who could confirm it. Where the policy asks for a confirmation (the default
/// preset does for every write by an API key), the scheduler, AutoUpdate and AutoPatch are refused outright: no
/// mutation, no approval anyone could later grant, a denied audit row. Each is driven through its real service.
/// </summary>
public sealed class KillSwitchTests : IDisposable
{
    private const string Key = "key:k1";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"killswitch-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public KillSwitchTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    private static AutomationMutationRunnerTests.Fixture KeyOwnedFixture()
    {
        var f = AutomationMutationRunnerTests.Build();   // SafeDefault: keys confirm every write
        f.Keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = "admin" });
        return f;
    }

    [Fact]
    public async Task Unattended_runs_never_auto_confirm()
    {
        // Scheduler: a key-owned custom command through the real executor.
        var scheduled = KeyOwnedFixture();
        var host = new AutomationMutationRunnerTests.CapturingHost();
        var executor = new TaskExecutor(new FakeDocker(), null!, null!, null!, host, scheduled.Runner,
            NullLogger<TaskExecutor>.Instance, notify: new FakeNotifications());
        var (taskOk, _) = await executor.ExecuteAsync(new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "sync", TaskType = ScheduledTaskType.CustomCommand, CreatedBy = Key,
            Config = "{\"command\":\"echo hello\"}",
        });

        // AutoUpdate: an opt-in policy with an update available, the feature owned by the key, through the service.
        var updated = KeyOwnedFixture();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.UpdatePolicies.Add(new UpdatePolicyEntity
            {
                ContainerId = "web-1", ContainerName = "web", ServerId = "local", AutoUpdate = true, NotifyBeforeUpdate = false,
            });
            await db.SaveChangesAsync();
        }
        var updates = new UpdatesAvailable();
        updates.Set(new ImageUpdateInfo { ContainerId = "web-1", ContainerName = "web", ServerId = "local", UpdateAvailable = true });
        var updateOwners = new MemoryAutomationOwners();
        await updateOwners.SetAsync(IAutomationOwnerStore.AutoUpdate, Key);
        var autoUpdate = new AutoUpdateService(_sp.GetRequiredService<IServiceScopeFactory>(),
            // RecreateContainerAsync throws on this double: reaching it would fail the run, not refuse it.
            new FakeDocker(new ContainerInfo { Id = "web-1", Name = "web", ServerId = "local", State = "running", Image = "web:1" }),
            updates, new FakeNotifications(), null!, updated.Runner, updateOwners, NullLogger<AutoUpdateService>.Instance);
        await autoUpdate.CheckAndUpdateAsync(CancellationToken.None);

        // AutoPatch: a key-owned night through the real service.
        var patched = KeyOwnedFixture();
        var cves = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        cves.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        var patchOwners = new MemoryAutomationOwners();
        await patchOwners.SetAsync(IAutomationOwnerStore.AutoPatch, Key);
        var script = new AutoPatchTests.RecordingRunner();
        var autoPatch = AutoPatchTests.NewService(script, cves, patched.Runner, patchOwners,
            new AutoPatchSettings { Enabled = true, DryRun = false }, AutoPatchTests.Compose("a"));
        await autoPatch.RunNightlyAsync(CancellationToken.None);

        Assert.False(taskOk);
        Assert.Empty(host.Commands);
        Assert.Empty(script.Calls);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var history = Assert.Single(db.UpdateHistory);
            Assert.False(history.Success);                      // refused, recorded as a failed update
            Assert.Contains("refused", history.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(db.UpdateRollbacks);                   // the snapshot inside the gated action never ran
        }
        foreach (var f in new[] { scheduled, updated, patched })
        {
            Assert.Empty(f.Approvals.GetAll());
            Assert.Contains(f.Audit.Events, e => e == $"denied:{MutationStatus.Denied}");
            Assert.DoesNotContain(f.Audit.Events, e => e.StartsWith("complete:", StringComparison.Ordinal));
        }
    }

    public void Dispose()
    {
        _sp.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
    }

    /// <summary>The update check's answers, held in memory.</summary>
    private sealed class UpdatesAvailable : IImageUpdateStore
    {
        private readonly List<ImageUpdateInfo> _items = [];
        public DateTime? LastCheckAt { get; set; }
        public bool IsChecking { get; set; }
        public void Set(ImageUpdateInfo info) => _items.Add(info);
        public ImageUpdateInfo? Get(string containerId, string? serverId = null) =>
            _items.FirstOrDefault(i => i.ContainerId == containerId && i.ServerId == (serverId ?? "local"));
        public IReadOnlyList<ImageUpdateInfo> GetUpdatesForServer(string serverId) => _items.Where(i => i.ServerId == serverId).ToList();
        public IReadOnlyList<ImageUpdateInfo> GetAllPendingUpdates() => _items.Where(i => i.UpdateAvailable).ToList();
        public IReadOnlyList<ImageUpdateInfo> GetAll() => _items;
        public void Remove(string containerId, string? serverId = null) =>
            _items.RemoveAll(i => i.ContainerId == containerId && i.ServerId == (serverId ?? "local"));
        public void Clear() => _items.Clear();
        public Task SaveAsync() => Task.CompletedTask;
    }
}
