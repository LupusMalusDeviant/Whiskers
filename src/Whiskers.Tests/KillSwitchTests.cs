using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.AutoPatch;
using Whiskers.Services.Cve;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;

namespace Whiskers.Tests;

/// <summary>
/// R1-01: an unattended run has nobody who could confirm it. Where the policy asks for a confirmation (the default
/// preset does for every write by an API key), the scheduler, AutoUpdate and AutoPatch are refused outright: no
/// mutation, no approval anyone could later grant, a denied audit row.
/// </summary>
public class KillSwitchTests
{
    private const string Key = "key:k1";

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

        // AutoUpdate: its actor and capability, as AutoUpdateService hands them to the runner.
        var updated = KeyOwnedFixture();
        var recreated = false;
        var update = await updated.Runner.RunAsync(new AutomationActor("autoupdate", "AutoUpdate", Key),
            "container.update", new { containerId = "web-1", name = "web", serverId = (string?)null },
            _ => { recreated = true; return Task.FromResult("new-id"); });

        // AutoPatch: a key-owned night through the real service.
        var patched = KeyOwnedFixture();
        var cves = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        cves.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        var owners = new MemoryAutomationOwners();
        await owners.SetAsync(IAutomationOwnerStore.AutoPatch, Key);
        var script = new AutoPatchTests.RecordingRunner();
        var autoPatch = AutoPatchTests.NewService(script, cves, patched.Runner, owners,
            new AutoPatchSettings { Enabled = true, DryRun = false }, AutoPatchTests.Compose("a"));
        await autoPatch.RunNightlyAsync(CancellationToken.None);

        Assert.False(taskOk);
        Assert.Empty(host.Commands);
        Assert.Equal(MutationStatus.Denied, update.Status);
        Assert.False(recreated);
        Assert.Empty(script.Calls);
        foreach (var f in new[] { scheduled, updated, patched })
        {
            Assert.Empty(f.Approvals.GetAll());
            Assert.Contains(f.Audit.Events, e => e == $"denied:{MutationStatus.Denied}");
            Assert.DoesNotContain(f.Audit.Events, e => e.StartsWith("complete:", StringComparison.Ordinal));
        }
    }
}
