using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.AutoUpdate;
using Whiskers.Services.Docker;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

// R1-36 (INV-44): an automatic update whose new container does not come up is rolled back when its policy says so —
// through the gateway on the owner's authority, and the rollback itself is read back. A snapshot that failed stops the
// update while rollback is on.
public sealed class AutoRollbackTests : IDisposable
{
    private const string OldId = "aaaaaaaaaaaa1111", NewId = "bbbbbbbbbbbb2222", RolledId = "cccccccccccc3333";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sw-autorollback-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;
    private readonly VerificationOutcomeTests.StatefulDocker _docker;
    private readonly IDockerService _service;
    private readonly AutomationMutationRunnerTests.Fixture _owner = AutomationMutationRunnerTests.Build(requireOwner: true);

    public AutoRollbackTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using (var scope = _sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();

        (_service, _docker) = VerificationOutcomeTests.StatefulDocker.Create();
        _docker.States[OldId] = "running";
        // The real target resolver lists the server's containers: every container the test knows is named "web".
        _docker.On[nameof(IDockerService.ListContainersAsync)] = _ => Task.FromResult<IList<ContainerInfo>>(
            _docker.States.Select(s => new ContainerInfo { Id = s.Key, Name = "web", ServerId = "local", State = s.Value })
                .ToList());
        _docker.On[nameof(IDockerService.ListAllContainersAsync)] = _ => Task.FromResult<IList<ContainerInfo>>(
            [new ContainerInfo { Id = OldId, Name = "web", ServerId = "local", Image = "nginx:latest", State = "running" }]);
        _docker.On[nameof(IDockerService.CaptureRollbackSnapshotAsync)] = _ => Task.FromResult(("sha256:old", "{\"Image\":\"nginx\"}"));
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_dbPath);
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
    }

    private async Task<AutoUpdateService> ServiceAsync(bool autoRollback, bool vault = true)
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.UpdatePolicies.Add(new UpdatePolicyEntity
            {
                ContainerId = OldId, ContainerName = "web", ServerId = "local", AutoUpdate = true,
                AutoRollback = autoRollback, NotifyBeforeUpdate = false,
            });
            await db.SaveChangesAsync();
        }
        var updates = new Updates();
        updates.Items.Add(new ImageUpdateInfo
        {
            ContainerId = OldId, ContainerName = "web", ServerId = "local", UpdateAvailable = true, RemoteDigest = "sha256:new",
        });
        var owners = new MemoryAutomationOwners();
        await owners.SetAsync(IAutomationOwnerStore.AutoUpdate, "user:admin@example.com");
        return new AutoUpdateService(_sp.GetRequiredService<IServiceScopeFactory>(), _service, updates,
            new FakeNotifications(), null!, Runner(), owners, NullLogger<AutoUpdateService>.Instance,
            vault ? TestVault.Usable : null) { HealthDelay = TimeSpan.Zero };
    }

    private AutomationMutationRunner Runner()
    {
        var f = _owner;
        f.Whitelist.Allowed.Add("admin@example.com");
        f.Roles.Map["admin@example.com"] = AppRole.Admin;
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var verifiers = VerificationOutcomeTests.Verifiers(new ServiceCollection().AddSingleton(_service).BuildServiceProvider());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), verifiers, NullLogger<MutationGateway>.Instance,
            targets: new MutationTargetResolver(new TestServices(_service,
                new FakeServerConfig(new ServerConfig { Id = "local", Name = "Local", IsDefault = true }))),
            approvals: new ApprovalGate(new ApprovalStore(), store));
        return new AutomationMutationRunner(gateway, new CapabilityCatalog(),
            new AutomationOwnerResolver(f.Roles, f.Whitelist, f.Keys, f.Accounts),
            new StaticOptionsMonitor<AutomationOptions>(f.Options));
    }

    /// <summary>The recreate brings up a container that exits; the rollback (if called) one in <paramref name="rolledState"/>.</summary>
    private void FailingUpdate(string rolledState = "running")
    {
        _docker.On[nameof(IDockerService.RecreateContainerAsync)] = _ =>
        {
            _docker.States.Remove(OldId);
            _docker.States[NewId] = "exited";
            return NewId;
        };
        _docker.On[nameof(IDockerService.RollbackContainerAsync)] = args =>
        {
            Assert.Equal("web", args[0]);
            Assert.Equal("sha256:old", args[1]);
            _docker.States.Remove(NewId);
            _docker.States[RolledId] = rolledState;
            return RolledId;
        };
    }

    private async Task<(UpdateHistoryEntity History, UpdatePolicyEntity Policy, int Snapshots)> ResultAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return (await db.UpdateHistory.SingleAsync(), await db.UpdatePolicies.SingleAsync(), await db.UpdateRollbacks.CountAsync());
    }

    /// <summary>The recreate brings up a running container; the inspects after it answer with <paramref name="later"/>
    /// (the first one is the gateway's read-back, the second the health check after the wait).</summary>
    private void UpdateThatLaterReads(Func<int, Task<(string, int, bool)>> later)
    {
        FailingUpdate();
        _docker.On[nameof(IDockerService.RecreateContainerAsync)] = _ =>
        {
            _docker.States.Remove(OldId);
            _docker.States[NewId] = "running";
            var reads = 0;
            _docker.On[nameof(IDockerService.InspectContainerStateAsync)] = args =>
                (string)args[0]! == NewId ? later(++reads) : Task.FromResult((_docker.States[(string)args[0]!], 0, false));
            return NewId;
        };
    }

    [Fact]
    public async Task A_container_that_is_down_after_the_health_wait_is_rolled_back()
    {
        UpdateThatLaterReads(n => Task.FromResult(n == 1 ? ("running", 0, false) : ("exited", 137, false)));
        var service = await ServiceAsync(autoRollback: true);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.Contains(nameof(IDockerService.RollbackContainerAsync), _docker.Calls);
        var (history, _, snapshots) = await ResultAsync();
        Assert.True(history.RolledBack);
        Assert.Contains("unhealthy", history.Error);
        Assert.Equal(0, snapshots);
    }

    [Fact]
    public async Task A_health_check_that_cannot_read_the_state_does_not_roll_a_verified_container_back()
    {
        UpdateThatLaterReads(n => n == 1
            ? Task.FromResult(("running", 0, false))
            : Task.FromException<(string, int, bool)>(new IOException("tunnel down")));
        var service = await ServiceAsync(autoRollback: true);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.DoesNotContain(nameof(IDockerService.RollbackContainerAsync), _docker.Calls);
        var (history, _, snapshots) = await ResultAsync();
        Assert.False(history.Success);
        Assert.False(history.RolledBack);
        Assert.Contains("could not be read", history.Error);
        Assert.Equal(1, snapshots);
    }

    [Fact]
    public async Task A_verification_that_cannot_read_the_state_does_not_roll_back()
    {
        UpdateThatLaterReads(_ => Task.FromException<(string, int, bool)>(new IOException("tunnel down")));
        var service = await ServiceAsync(autoRollback: true);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.DoesNotContain(nameof(IDockerService.RollbackContainerAsync), _docker.Calls);
        var (history, _, _) = await ResultAsync();
        Assert.False(history.Success);
        Assert.False(history.RolledBack);
    }

    [Fact]
    public async Task Without_a_usable_vault_the_update_runs_without_a_way_back_and_is_not_rolled_back()
    {
        FailingUpdate();
        var service = await ServiceAsync(autoRollback: true, vault: false);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.Contains(nameof(IDockerService.RecreateContainerAsync), _docker.Calls);
        Assert.DoesNotContain(nameof(IDockerService.RollbackContainerAsync), _docker.Calls);
        var (history, _, snapshots) = await ResultAsync();
        Assert.False(history.Success);
        Assert.False(history.RolledBack);
        Assert.Equal(0, snapshots);
    }

    [Fact]
    public async Task A_manual_rollback_uses_its_snapshot_up_only_once_it_is_completed()
    {
        _docker.On[nameof(IDockerService.RollbackContainerAsync)] = _ => RolledId;
        var service = await ServiceAsync(autoRollback: false);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.UpdateHistory.Add(new UpdateHistoryEntity { ContainerName = "web", ServerId = "local", StartedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await service.CaptureSnapshotAsync(new ContainerInfo { Id = OldId, Name = "web", ServerId = "local" });
        var snapshot = Assert.Single(await service.GetRollbacksAsync());

        await service.RollbackAsync(snapshot.Id);
        Assert.Single(await service.GetRollbacksAsync());           // not verified yet: still the way back

        await service.CompleteRollbackAsync(snapshot.Id);
        var (history, _, snapshots) = await ResultAsync();
        Assert.Equal(0, snapshots);
        Assert.True(history.RolledBack);
    }

    [Fact]
    public async Task An_image_that_was_rolled_back_is_not_tried_again()
    {
        FailingUpdate();
        var service = await ServiceAsync(autoRollback: true);
        await service.CheckAndUpdateAsync(CancellationToken.None);
        Assert.True((await ResultAsync()).History.RolledBack);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            (await db.UpdatePolicies.SingleAsync()).LastChecked = null;   // due again
            await db.SaveChangesAsync();
        }
        _docker.Calls.Clear();

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.DoesNotContain(nameof(IDockerService.RecreateContainerAsync), _docker.Calls);
        var (history, policy, _) = await ResultAsync();              // still the one history row
        Assert.True(history.RolledBack);
        Assert.StartsWith("SKIPPED", policy.LastUpdateResult);
    }

    [Fact]
    public async Task A_failed_update_is_rolled_back_and_the_rollback_is_verified()
    {
        FailingUpdate();
        var service = await ServiceAsync(autoRollback: true);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.Contains(nameof(IDockerService.RollbackContainerAsync), _docker.Calls);
        var (history, policy, snapshots) = await ResultAsync();
        Assert.False(history.Success);
        Assert.True(history.RolledBack);
        Assert.Contains("rolled back", history.Error);
        Assert.StartsWith("FEHLER (zurückgerollt)", policy.LastUpdateResult);
        Assert.Equal(0, snapshots);                   // consumed
        Assert.Equal("running", _docker.States[RolledId]);
    }

    [Fact]
    public async Task A_rollback_whose_container_does_not_run_is_reported_as_failed()
    {
        FailingUpdate(rolledState: "exited");
        var service = await ServiceAsync(autoRollback: true);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        var (history, policy, snapshots) = await ResultAsync();
        Assert.False(history.Success);
        Assert.False(history.RolledBack);
        Assert.Contains("automatic rollback failed", history.Error);
        Assert.StartsWith("FEHLER (Rollback fehlgeschlagen)", policy.LastUpdateResult);
        Assert.Equal(1, snapshots);                   // kept: the way back is still there for a manual rollback
    }

    [Fact]
    public async Task A_rollback_the_gateway_refuses_is_recorded_and_never_run()
    {
        FailingUpdate();
        var recreate = _docker.On[nameof(IDockerService.RecreateContainerAsync)];
        _docker.On[nameof(IDockerService.RecreateContainerAsync)] = args =>
        {
            _owner.Roles.Map["admin@example.com"] = AppRole.Viewer;   // the owner lost the right in between
            return recreate(args);
        };
        var service = await ServiceAsync(autoRollback: true);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.DoesNotContain(nameof(IDockerService.RollbackContainerAsync), _docker.Calls);
        var (history, policy, snapshots) = await ResultAsync();
        Assert.False(history.Success);
        Assert.False(history.RolledBack);
        Assert.Contains("automatic rollback failed", history.Error);
        Assert.StartsWith("FEHLER (Rollback fehlgeschlagen)", policy.LastUpdateResult);
        Assert.Equal(1, snapshots);
    }

    [Fact]
    public async Task A_recreate_that_threw_is_not_rolled_back()
    {
        _docker.On[nameof(IDockerService.RecreateContainerAsync)] = _ => throw new IOException("pull failed");
        var service = await ServiceAsync(autoRollback: true);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.DoesNotContain(nameof(IDockerService.RollbackContainerAsync), _docker.Calls);
        var (history, _, snapshots) = await ResultAsync();
        Assert.False(history.Success);
        Assert.False(history.RolledBack);
        Assert.Equal(1, snapshots);
        Assert.Equal("running", _docker.States[OldId]);   // the old container never left
    }

    [Fact]
    public async Task Without_automatic_rollback_a_failed_update_stays_as_it_is()
    {
        FailingUpdate();
        var service = await ServiceAsync(autoRollback: false);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.DoesNotContain(nameof(IDockerService.RollbackContainerAsync), _docker.Calls);
        var (history, _, snapshots) = await ResultAsync();
        Assert.False(history.Success);
        Assert.False(history.RolledBack);
        Assert.Equal(1, snapshots);                   // still there for a manual rollback
    }

    [Fact]
    public async Task A_snapshot_that_failed_stops_the_update_while_rollback_is_on()
    {
        FailingUpdate();
        _docker.On[nameof(IDockerService.CaptureRollbackSnapshotAsync)] = _ =>
            Task.FromException<(string, string)>(new IOException("inspect failed"));
        var service = await ServiceAsync(autoRollback: true);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.DoesNotContain(nameof(IDockerService.RecreateContainerAsync), _docker.Calls);
        var (history, _, _) = await ResultAsync();
        Assert.False(history.Success);
        Assert.Contains("no rollback snapshot could be kept", history.Error);
        Assert.Equal("running", _docker.States[OldId]);   // untouched
    }

    [Fact]
    public async Task A_verified_update_needs_no_rollback()
    {
        _docker.On[nameof(IDockerService.RecreateContainerAsync)] = _ =>
        {
            _docker.States.Remove(OldId);
            _docker.States[NewId] = "running";
            return NewId;
        };
        var service = await ServiceAsync(autoRollback: true);

        await service.CheckAndUpdateAsync(CancellationToken.None);

        Assert.DoesNotContain(nameof(IDockerService.RollbackContainerAsync), _docker.Calls);
        var (history, _, snapshots) = await ResultAsync();
        Assert.True(history.Success);
        Assert.Equal(1, snapshots);
    }

    private sealed class Updates : IImageUpdateStore
    {
        public List<ImageUpdateInfo> Items { get; } = [];
        public DateTime? LastCheckAt { get; set; }
        public bool IsChecking { get; set; }
        public void Set(ImageUpdateInfo info) => Items.Add(info);
        public ImageUpdateInfo? Get(string containerId, string? serverId = null) =>
            Items.FirstOrDefault(i => i.ContainerId == containerId && i.ServerId == (serverId ?? "local"));
        public IReadOnlyList<ImageUpdateInfo> GetUpdatesForServer(string serverId) => Items.Where(i => i.ServerId == serverId).ToList();
        public IReadOnlyList<ImageUpdateInfo> GetAllPendingUpdates() => Items.Where(i => i.UpdateAvailable).ToList();
        public IReadOnlyList<ImageUpdateInfo> GetAll() => Items;
        public void Remove(string containerId, string? serverId = null) =>
            Items.RemoveAll(i => i.ContainerId == containerId && i.ServerId == (serverId ?? "local"));
        public void Clear() => Items.Clear();
        public Task SaveAsync() => Task.CompletedTask;
    }
}
