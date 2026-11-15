using Whiskers.Services.Docker.Operations;

namespace Whiskers.Tests;

/// <summary>1.0.0 review, Fleet HIGH 4: a <c>container.update</c> (or C12 rollback) whose new container is created but
/// fails to start or to join a network must leave the previous container running under its name. The new container
/// holds the name, so it is removed before the previous one is renamed back. A restore that fails is thrown with the
/// previous container's current name, never only logged.
/// <para>Driven through the <see cref="IContainerRestoreSteps"/> seam against a fake daemon that, like the real one,
/// gives a name to one container at a time. The wiring inside <c>ContainerLifecycleOperations</c> (which step calls which
/// restore with which ids) is not pinned by an in-process test: this test project loads Testcontainers' fork of
/// Docker.DotNet, whose <c>DockerClient</c> lacks methods the Whiskers assembly was compiled against, so driving
/// <c>ContainerLifecycleOperations</c> over a real client here ends in a <see cref="MissingMethodException"/>. That only
/// it calls the restore is pinned by <c>MutationGatewayCoverageTests.Only_the_update_and_rollback_restore_drive_the_restore_steps</c>.</para></summary>
public class ContainerRestoreTests
{
    private const string OldId = "aaaaaaaaaaaaaaaaaaaa";
    private const string NewId = "bbbbbbbbbbbbbbbbbbbb";

    private sealed class FakeDaemon : IContainerRestoreSteps
    {
        public readonly Dictionary<string, (string Name, string State)> Containers = new();
        public readonly List<string> Calls = [];
        public bool FailRemove, FailRename, FailStart, FailFind;

        public Task<(string Id, string State)?> FindByNameAsync(string name)
        {
            Calls.Add($"find {name}");
            if (FailFind) throw new InvalidOperationException("list refused");
            foreach (var (id, c) in Containers)
                if (c.Name == name)
                    return Task.FromResult<(string, string)?>((id, c.State));
            return Task.FromResult<(string, string)?>(null);
        }

        public Task RemoveAsync(string id)
        {
            Calls.Add($"remove {id[..4]}");
            if (FailRemove) throw new InvalidOperationException("remove refused");
            Containers.Remove(id);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string id, string newName)
        {
            Calls.Add($"rename {id[..4]} {newName}");
            if (FailRename) throw new InvalidOperationException("rename refused");
            if (Containers.Any(c => c.Key != id && c.Value.Name == newName))
                throw new InvalidOperationException("Conflict. The container name is already in use");
            Containers[id] = (newName, Containers[id].State);
            return Task.CompletedTask;
        }

        public Task StartAsync(string id)
        {
            Calls.Add($"start {id[..4]}");
            if (FailStart) throw new InvalidOperationException("start refused");
            Containers[id] = (Containers[id].Name, "running");
            return Task.CompletedTask;
        }
    }

    /// <summary>The state right after a recreate whose new container did not start: the old one stopped and stashed, the
    /// new one created under the service's name.</summary>
    private static FakeDaemon AfterFailedStart()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web-old-1", "exited");
        daemon.Containers[NewId] = ("web", "created");
        return daemon;
    }

    private static readonly Exception StartFailure = new InvalidOperationException("OCI runtime create failed");

    [Fact]
    public async Task Failed_start_of_the_new_container_restores_the_old_one_under_its_name()
    {
        var daemon = AfterFailedStart();

        await ContainerRestore.RestorePreviousAsync(daemon, NewId, OldId, "web", "web-old-1", StartFailure, "Update");

        Assert.Equal(["remove bbbb", "rename aaaa web", "start aaaa"], daemon.Calls);
        Assert.Equal(("web", "running"), Assert.Single(daemon.Containers).Value);
        Assert.Equal(OldId, daemon.Containers.Single().Key);
    }

    [Fact]
    public async Task Failed_create_has_nothing_to_remove_and_restores_the_old_one()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web-old-1", "exited");

        await ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", StartFailure, "Update");

        Assert.Equal(["find web", "rename aaaa web", "start aaaa"], daemon.Calls);
        Assert.Equal(("web", "running"), daemon.Containers[OldId]);
    }

    [Fact]
    public async Task Lost_create_answer_the_never_started_container_holding_the_name_is_removed()
    {
        // The create reached the daemon but its answer was lost: the id is unknown, the container holds the name.
        var daemon = AfterFailedStart();

        await ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", StartFailure, "Update");

        Assert.Equal(["find web", "remove bbbb", "rename aaaa web", "start aaaa"], daemon.Calls);
        Assert.Equal(("web", "running"), Assert.Single(daemon.Containers).Value);
    }

    [Fact]
    public async Task Lost_create_lookup_never_removes_a_running_container_holding_the_name()
    {
        var daemon = AfterFailedStart();
        daemon.Containers[NewId] = ("web", "running");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", StartFailure, "Update"));

        Assert.DoesNotContain(daemon.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
        Assert.True(daemon.Containers.ContainsKey(NewId));
        Assert.Contains("now named 'web-old-1'", ex.Message);
    }

    [Fact]
    public async Task Lost_create_lookup_never_removes_the_previous_container()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web", "created");  // the stash rename never took effect

        await ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", StartFailure, "Update");

        Assert.DoesNotContain(daemon.Calls, c => c.StartsWith("remove", StringComparison.Ordinal));
        Assert.Equal(("web", "running"), daemon.Containers[OldId]);
    }

    [Fact]
    public async Task Failed_lookup_is_left_to_the_rename_to_report()
    {
        var daemon = AfterFailedStart();
        daemon.FailFind = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", StartFailure, "Update"));

        Assert.Contains("now named 'web-old-1'", ex.Message);
        Assert.Contains("already in use", ex.Message);
    }

    [Fact]
    public async Task New_container_that_cannot_be_removed_is_a_thrown_rollback_failure_naming_the_old_containers_name()
    {
        var daemon = AfterFailedStart();
        daemon.FailRemove = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, NewId, OldId, "web", "web-old-1", StartFailure, "Update"));

        Assert.Contains("rollback failed", ex.Message);
        Assert.Contains("now named 'web-old-1'", ex.Message);
        Assert.Contains("still holds the name 'web'", ex.Message);
        // No rename into the name the new container still holds; the old one is started under its stashed name.
        Assert.DoesNotContain(daemon.Calls, c => c.StartsWith("rename", StringComparison.Ordinal));
        Assert.Equal(("web-old-1", "running"), daemon.Containers[OldId]);
        var inner = Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Contains(StartFailure, inner.InnerExceptions);
    }

    [Fact]
    public async Task Failed_rename_back_is_thrown_with_the_stashed_name()
    {
        var daemon = AfterFailedStart();
        daemon.FailRename = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, NewId, OldId, "web", "web-old-1", StartFailure, "Update"));

        Assert.Contains("Update of 'web' failed (OCI runtime create failed) and the rollback failed", ex.Message);
        Assert.Contains("now named 'web-old-1'", ex.Message);
        Assert.Contains("rename refused", ex.Message);
    }

    [Fact]
    public async Task Failed_start_of_the_old_container_is_thrown_as_not_running()
    {
        var daemon = AfterFailedStart();
        daemon.FailStart = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, NewId, OldId, "web", "web-old-1", StartFailure, "Rollback"));

        Assert.Contains("Rollback of 'web' failed", ex.Message);
        Assert.Contains("now named 'web' and is not running", ex.Message);
    }

    // ── The stash rename failed after the stop (MINOR 3) ──

    private static readonly Exception RenameFailure = new InvalidOperationException("rename timed out");

    [Fact]
    public async Task Failed_stash_rename_after_a_stop_starts_the_container_again_and_throws()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web", "exited");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RecoverFailedStashAsync(daemon, OldId, "web", "web-old-1", wasStopped: true, RenameFailure, "Update"));

        Assert.Equal(["start aaaa"], daemon.Calls);
        Assert.Equal(("web", "running"), daemon.Containers[OldId]);
        Assert.Contains("Update of 'web' did not happen", ex.Message);
        Assert.Contains("rename timed out", ex.Message);
        Assert.Contains("it was started again", ex.Message);
        Assert.Same(RenameFailure, ex.InnerException);
    }

    [Fact]
    public async Task Failed_stash_rename_of_a_container_that_was_not_running_leaves_it_stopped()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web", "exited");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RecoverFailedStashAsync(daemon, OldId, "web", "web-old-1", wasStopped: false, RenameFailure, "Update"));

        Assert.Empty(daemon.Calls);
        Assert.Contains("was not running before", ex.Message);
    }

    [Fact]
    public async Task Failed_stash_rename_and_failed_restart_says_the_container_is_stopped()
    {
        var daemon = new FakeDaemon { FailStart = true };
        daemon.Containers[OldId] = ("web", "exited");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RecoverFailedStashAsync(daemon, OldId, "web", "web-rollbackfail-1", wasStopped: true, RenameFailure, "Rollback"));

        Assert.Contains("starting it again failed too (start refused), it is stopped", ex.Message);
        Assert.IsType<AggregateException>(ex.InnerException);
    }

    // ── C12 rollback with no previous container (MINOR 4) ──

    [Fact]
    public async Task Rollback_without_a_previous_container_removes_the_rollback_container_it_created()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[NewId] = ("web", "created");

        await ContainerRestore.RemoveCreatedAsync(daemon, NewId, "web", StartFailure, "Rollback");

        Assert.Equal(["remove bbbb"], daemon.Calls);
        Assert.Empty(daemon.Containers);
    }

    [Fact]
    public async Task Rollback_container_that_cannot_be_removed_is_thrown()
    {
        var daemon = new FakeDaemon { FailRemove = true };
        daemon.Containers[NewId] = ("web", "created");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RemoveCreatedAsync(daemon, NewId, "web", StartFailure, "Rollback"));

        Assert.Contains("could not be removed", ex.Message);
        Assert.Contains("holds the name 'web'", ex.Message);
    }

    [Fact]
    public async Task Rollback_whose_create_failed_has_nothing_to_remove()
    {
        var daemon = new FakeDaemon();

        await ContainerRestore.RemoveCreatedAsync(daemon, null, "web", StartFailure, "Rollback");

        Assert.Empty(daemon.Calls);
    }
}
