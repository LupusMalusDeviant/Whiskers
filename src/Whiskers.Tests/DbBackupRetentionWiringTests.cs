using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Database;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>INV-19 review round 4 (m3): a scheduled DB backup's retention, driven through
/// <c>TaskExecutor.ExecuteDbBackup</c> → <c>ApplyRetention</c>. The listing is a read (<c>ExecuteAsync</c>), only the
/// <c>rm</c> runs on the primitive, only this container's dumps of this database reach it, the container key reaches the
/// decision as <c>dumpContainer</c>, and a truncated listing removes nothing and fails the decision.</summary>
public class DbBackupRetentionWiringTests
{
    private static readonly string Dir = DataPathOptions.Default.BackupsDir;
    private static readonly string Listing = $"ls -1Aq -- '{Dir}'";

    private static string Dump(string container, string stamp) => $"{container}@app_{stamp}_0a1b2c3d.sql";

    private static readonly string Directory = string.Join('\n',
        Dump("shop-db", "20261107_030303000"),
        Dump("blog-db", "20261107_020202000"),
        Dump("shop-db", "20261106_030303000"),
        Dump("blog-db", "20261106_020202000"),
        Dump("shop-db", "20261105_030303000"),
        Dump("blog-db", "20261105_020202000"),
        "app_20261001_010101.sql",          // legacy: never pruned
        "");

    [Fact]
    public async Task The_retention_lists_with_a_read_and_removes_only_this_containers_dumps_on_the_primitive()
    {
        var (executor, runner, task) = Arrange(new CommandResult { ExitCode = 0, Output = Directory });

        var (ok, _) = await task.Executor.ExecuteAsync(task.Entity);

        Assert.True(ok);
        Assert.Equal([Listing], executor.Reads);
        var rm = Assert.Single(executor.Mutations);
        Assert.Equal($"rm -f -- '{Dir}/{Dump("shop-db", "20261106_030303000")}' '{Dir}/{Dump("shop-db", "20261105_030303000")}'", rm);

        var decision = Assert.Single(runner.Decisions, d => d.Capability == "db.backup_delete");
        Assert.Equal(MutationStatus.Executed, decision.Status);
        Assert.Equal("shop-db", Arg(decision.Arguments, "dumpContainer"));
        Assert.Equal("app", Arg(decision.Arguments, "database"));
        Assert.Equal("1", Arg(decision.Arguments, "keep"));
    }

    [Fact]
    public async Task A_truncated_listing_removes_nothing_and_fails_the_decision()
    {
        var (executor, runner, task) = Arrange(new CommandResult { ExitCode = 0, Output = Directory, OutputTruncated = true });

        await task.Executor.ExecuteAsync(task.Entity);

        Assert.Equal([Listing], executor.Reads);
        Assert.Empty(executor.Mutations);
        var decision = Assert.Single(runner.Decisions, d => d.Capability == "db.backup_delete");
        Assert.Equal(MutationStatus.Failed, decision.Status);
    }

    private static string? Arg(object arguments, string name) =>
        arguments.GetType().GetProperty(name)?.GetValue(arguments)?.ToString();

    private static (RecordingExecutor, RecordingRunner, (TaskExecutor Executor, ScheduledTaskEntity Entity)) Arrange(
        CommandResult listing)
    {
        var container = new ContainerInfo { Id = "0123456789abcdef0123", Name = "shop-db", Image = "postgres:16" };
        var (docker, dockerScript) = ReadBackVerificationTests.Scripted<IDockerService>.Create();
        dockerScript.On[nameof(IDockerService.ListContainersAsync)] = _ => Task.FromResult<IList<ContainerInfo>>([container]);
        dockerScript.On[nameof(IDockerService.GetContainerEnvAsync)] = _ => Task.FromResult(new List<KeyValuePair<string, string>>());
        var (db, dbScript) = ReadBackVerificationTests.Scripted<IDatabaseService>.Create();
        dbScript.On[nameof(IDatabaseService.BackupDatabaseAsync)] = args =>
        {
            Assert.Equal("shop-db", args[5]);   // the container's name keys the dump
            return Task.FromResult((true, $"{Dir}/{Dump("shop-db", "20261107_030303000")}", 1024L, ""));
        };

        var executor = new RecordingExecutor(listing);
        var runner = new RecordingRunner();
        var taskExecutor = new TaskExecutor(docker, db, null!, null!, executor, runner, NullLogger<TaskExecutor>.Instance,
            DataPathOptions.Default, notify: new FakeNotifications());
        var entity = new ScheduledTaskEntity
        {
            TaskId = "t-db", Name = "nightly dump", TaskType = ScheduledTaskType.DbBackup, TargetId = "shop-db",
            CreatedBy = "user:admin@example.com", Config = """{"database":"app","maxBackups":"1"}""",
        };
        return (executor, runner, (taskExecutor, entity));
    }

    private sealed class RecordingExecutor(CommandResult listing) : IHostCommandExecutor
    {
        public List<string> Reads { get; } = [];
        public List<string> Mutations { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Reads.Add(command);
            return Task.FromResult(command == Listing ? listing : new CommandResult { ExitCode = -1, Error = "unscripted" });
        }

        public Task<CommandResult> ExecuteMutationAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Mutations.Add(command);
            return Task.FromResult(new CommandResult { ExitCode = 0 });
        }
    }

    /// <summary>Runs every action as decided and records each decision the way the gateway judges its result.</summary>
    private sealed class RecordingRunner : IAutomationMutationRunner
    {
        public List<(string Capability, object Arguments, MutationStatus Status)> Decisions { get; } = [];

        public async Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            var value = await action(ct);
            var failure = MutationResults.FailureOf(value);
            var status = failure is null ? MutationStatus.Executed : MutationStatus.Failed;
            Decisions.Add((capabilityId, arguments, status));
            return new MutationOutcome<T>("op-" + Decisions.Count, status, value, failure, VerificationStatus.NotVerifiable);
        }

        public async Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task> action, CancellationToken ct = default)
        {
            await action(ct);
            Decisions.Add((capabilityId, arguments, MutationStatus.Executed));
            return new MutationOutcome<bool>("op-" + Decisions.Count, MutationStatus.Executed, true, null, VerificationStatus.NotVerifiable);
        }

    }
}
