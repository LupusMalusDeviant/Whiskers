using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public async Task AutoUpdate_and_AutoPatch_are_suspended_when_their_owner_lost_authority()
    {
        // The owner's local account was deleted; whitelist and role entries were left behind (R1-04).
        const string owner = "user:ops@example.com";
        AutomationMutationRunnerTests.Fixture Lost()
        {
            var f = AutomationMutationRunnerTests.Build();
            f.Whitelist.Allowed.Add("ops@example.com");
            f.Roles.Map["ops@example.com"] = AppRole.Admin;
            f.Accounts.Deleted.Add("ops@example.com");
            return f;
        }

        var updated = Lost();
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
        var updateOwners = new MemoryAutomationOwners { FailToPersist = true };
        await updateOwners.SetAsync(IAutomationOwnerStore.AutoUpdate, owner);
        var updateNotes = new FakeNotifications();
        var autoUpdate = new AutoUpdateService(_sp.GetRequiredService<IServiceScopeFactory>(),
            new FakeDocker(new ContainerInfo { Id = "web-1", Name = "web", ServerId = "local", State = "running", Image = "web:1" }),
            updates, updateNotes, null!, updated.Runner, updateOwners, NullLogger<AutoUpdateService>.Instance);
        await autoUpdate.CheckAndUpdateAsync(CancellationToken.None);

        var patched = Lost();
        var cves = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        cves.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        var patchOwners = new MemoryAutomationOwners();
        await patchOwners.SetAsync(IAutomationOwnerStore.AutoPatch, owner);
        var script = new AutoPatchTests.RecordingRunner();
        var autoPatch = AutoPatchTests.NewService(script, cves, patched.Runner, patchOwners,
            new AutoPatchSettings { Enabled = true, DryRun = false }, AutoPatchTests.Compose("a"));
        await autoPatch.RunNightlyAsync(CancellationToken.None);

        Assert.True(updateOwners.IsBlocked(IAutomationOwnerStore.AutoUpdate));
        Assert.Contains("account deleted", updateOwners.Suspended[IAutomationOwnerStore.AutoUpdate]);
        // The admins are told it was suspended, with the reason and how to take it over — not just "update failed".
        var suspended = Assert.Single(updateNotes.Events, e => e.EventType == "automation_suspended");
        Assert.Contains("account deleted", suspended.ImageInfo);
        Assert.Contains("Automation owners", suspended.ImageInfo);
        Assert.Contains("could not be saved", suspended.ImageInfo);   // a store that failed to write says so
        Assert.True(patchOwners.IsBlocked(IAutomationOwnerStore.AutoPatch));
        Assert.Empty(script.Calls);
    }

    [Fact]
    public async Task AutoPatch_is_suspended_when_its_owner_loses_authority_half_way_through_a_cycle()
    {
        var f = AutomationMutationRunnerTests.Build(requireOwner: true);
        f.Whitelist.Allowed.Add("ops@example.com");
        f.Roles.Map["ops@example.com"] = AppRole.Admin;
        var cves = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        cves.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        cves.Set(AutoPatchTests.Scan("b", "local", (CveSeverity.Critical, "1")));
        cves.Set(AutoPatchTests.Scan("c", "local", (CveSeverity.Critical, "1")));
        var owners = new MemoryAutomationOwners { FailToPersist = true };
        await owners.SetAsync(IAutomationOwnerStore.AutoPatch, "user:ops@example.com");
        // The owner is demoted right after the first container was patched.
        var script = new AutoPatchTests.RecordingRunner
        {
            AfterPatch = _ => f.Roles.Map["ops@example.com"] = AppRole.Viewer,
        };
        var notes = new FakeNotifications();
        var autoPatch = AutoPatchTests.NewService(script, cves, f.Runner, owners,
            new AutoPatchSettings { Enabled = true, DryRun = false }, notes,
            AutoPatchTests.Compose("a"), AutoPatchTests.Compose("b"), AutoPatchTests.Compose("c"));

        await autoPatch.RunNightlyAsync(CancellationToken.None);

        Assert.Single(script.Calls);                                    // stopped, not refused container by container
        Assert.True(owners.IsBlocked(IAutomationOwnerStore.AutoPatch));
        Assert.Contains(notes.Events, e => e.EventType == "automation_suspended" && e.ContainerName == "AutoPatch"
                                           && e.ImageInfo!.Contains("could not be saved"));
        Assert.Contains("suspended", autoPatch.LastCycle!.Error);
    }

    [Fact]
    public void A_deleted_local_account_ends_authority_with_local_and_with_mixed_sign_in()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"identity-{Guid.NewGuid():N}.db");
        var dataDir = Path.Combine(Path.GetTempPath(), $"owners-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
            using var sp = services.BuildServiceProvider();
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
                db.Database.EnsureCreated();
                db.Users.Add(new AppUser
                {
                    Id = "u1", UserName = "ops@example.com", NormalizedUserName = "OPS@EXAMPLE.COM",
                    Email = "ops@example.com", NormalizedEmail = "OPS@EXAMPLE.COM",
                });
                db.SaveChanges();
            }
            IConfiguration Config(params (string, string)[] values) =>
                new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .AddInMemoryCollection(values.ToDictionary(v => v.Item1, v => (string?)v.Item2)).Build();
            var scopes = sp.GetRequiredService<IServiceScopeFactory>();

            var local = new OwnerAccounts(scopes, Config());
            Assert.True(local.Exists("Ops@Example.com"));
            Assert.False(local.Exists("gone@example.com"));

            // Mixed sign-in: local login stays available next to OIDC. A user without a local row is unverified
            // (refused) until seen signed in federated; a user who had a local account and lost it is deleted.
            var paths = new DataPathOptions(dataDir);
            var mixed = new OwnerAccounts(scopes,
                Config(("Oidc:Enabled", "true"), ("Oidc:Authority", "https://id.example.com")), paths);
            Assert.Equal(OwnerAccountState.Unverified, mixed.StateOf("federated@example.com"));
            mixed.RememberFederated("Federated@Example.com");
            Assert.True(mixed.Exists("federated@example.com"));
            Assert.True(mixed.Exists("ops@example.com"));
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
                db.Users.Remove(db.Users.Single(u => u.Id == "u1"));
                db.SaveChanges();
            }
            Assert.Equal(OwnerAccountState.Deleted, mixed.StateOf("ops@example.com"));
            Assert.True(mixed.Exists("federated@example.com"));
            // Deleted before the record began (missing history): never seen at all → unverified, not federated.
            Assert.Equal(OwnerAccountState.Unverified, mixed.StateOf("deleted-before-upgrade@example.com"));
            // Remembered across restarts.
            var restarted = new OwnerAccounts(scopes,
                Config(("Oidc:Enabled", "true"), ("Oidc:Authority", "https://id.example.com")), paths);
            Assert.False(restarted.Exists("ops@example.com"));

            // Startup records the accounts before anything is looked up: an account deleted after startup but
            // before its owner's first run is still known, and the owner resolver reports it as deleted.
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
                db.Users.Add(new AppUser
                {
                    Id = "u2", UserName = "lea@example.com", NormalizedUserName = "LEA@EXAMPLE.COM",
                    Email = "lea@example.com", NormalizedEmail = "LEA@EXAMPLE.COM",
                });
                db.SaveChanges();
            }
            var booted = new OwnerAccounts(scopes,
                Config(("Oidc:Enabled", "true"), ("Oidc:Authority", "https://id.example.com")), paths);
            booted.RememberLocalAccounts();
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
                db.Users.Remove(db.Users.Single(u => u.Id == "u2"));
                db.SaveChanges();
            }
            var f = AutomationMutationRunnerTests.Build();
            f.Whitelist.Allowed.Add("lea@example.com");
            f.Roles.Map["lea@example.com"] = AppRole.Admin;
            var resolver = new AutomationOwnerResolver(f.Roles, f.Whitelist, f.Keys, booted);
            Assert.Equal(OwnerState.AccountDeleted, resolver.Describe("user:lea@example.com").State);

            // A lost record starts over: owners without a local row are unverified again, not federated.
            File.Delete(paths.KnownLocalAccountsJson);
            var lost = new OwnerAccounts(scopes,
                Config(("Oidc:Enabled", "true"), ("Oidc:Authority", "https://id.example.com")), paths);
            Assert.Equal(OwnerAccountState.Unverified, lost.StateOf("federated@example.com"));
            Assert.Equal(OwnerState.Unverified, new AutomationOwnerResolver(f.Roles, f.Whitelist, f.Keys, lost)
                .Describe("user:federated@example.com").State);
            lost.RememberFederated("federated@example.com");

            // An unreadable record of local accounts fails closed for owners without a local row.
            File.WriteAllText(paths.KnownLocalAccountsJson, "{ not json");
            var broken = new OwnerAccounts(scopes,
                Config(("Oidc:Enabled", "true"), ("Oidc:Authority", "https://id.example.com")), paths);
            Assert.False(broken.Exists("federated@example.com"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch (IOException) { }
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
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
