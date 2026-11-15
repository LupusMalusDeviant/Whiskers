using System.Net;
using Docker.DotNet;
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
    private const string Image = "nginx:1";

    private sealed class FakeDaemon : IContainerRestoreSteps
    {
        public readonly Dictionary<string, (string Name, string State, string Image)> Containers = new();
        public readonly List<string> Calls = [];
        public bool FailRemove, FailRename, FailStart, FailFind, FailState;

        public Task<RestoreContainer?> FindByNameAsync(string name)
        {
            Calls.Add($"find {name}");
            if (FailFind) throw new InvalidOperationException("list refused");
            foreach (var (id, c) in Containers)
                if (c.Name == name)
                    return Task.FromResult<RestoreContainer?>(new RestoreContainer(id, c.State, c.Image));
            return Task.FromResult<RestoreContainer?>(null);
        }

        public Task<string> GetStateAsync(string id)
        {
            Calls.Add($"state {id[..4]}");
            if (FailState) throw new InvalidOperationException("inspect refused");
            return Task.FromResult(Containers[id].State);
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
            Containers[id] = Containers[id] with { Name = newName };
            return Task.CompletedTask;
        }

        public Task StartAsync(string id)
        {
            Calls.Add($"start {id[..4]}");
            if (FailStart) throw new InvalidOperationException("start refused");
            Containers[id] = Containers[id] with { State = "running" };
            return Task.CompletedTask;
        }
    }

    /// <summary>The state right after a recreate whose new container did not start: the old one stopped and stashed, the
    /// new one created under the service's name.</summary>
    private static FakeDaemon AfterFailedStart()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web-old-1", "exited", "sha256:old");
        daemon.Containers[NewId] = ("web", "created", Image);
        return daemon;
    }

    private static readonly Exception StartFailure = new InvalidOperationException("OCI runtime create failed");
    // A create whose answer never arrived: the daemon may or may not have made the container.
    private static readonly Exception LostAnswer = new TimeoutException("the create request timed out");

    private static bool NoRemove(FakeDaemon daemon) => !daemon.Calls.Any(c => c.StartsWith("remove", StringComparison.Ordinal));

    [Fact]
    public async Task Failed_start_of_the_new_container_restores_the_old_one_under_its_name()
    {
        var daemon = AfterFailedStart();

        await ContainerRestore.RestorePreviousAsync(daemon, NewId, OldId, "web", "web-old-1", Image, StartFailure, "Update");

        Assert.Equal(["remove bbbb", "rename aaaa web", "start aaaa"], daemon.Calls);
        Assert.Equal(("web", "running", "sha256:old"), Assert.Single(daemon.Containers).Value);
        Assert.Equal(OldId, daemon.Containers.Single().Key);
    }

    [Fact]
    public async Task Failed_create_has_nothing_to_remove_and_restores_the_old_one()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web-old-1", "exited", "sha256:old");

        await ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", Image, LostAnswer, "Update");

        Assert.Equal(["find web", "rename aaaa web", "start aaaa"], daemon.Calls);
        Assert.Equal("running", daemon.Containers[OldId].State);
    }

    [Fact]
    public async Task Lost_create_answer_the_never_started_container_holding_the_name_is_removed()
    {
        // The create reached the daemon but its answer was lost: the id is unknown, the container holds the name.
        var daemon = AfterFailedStart();

        await ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", Image, LostAnswer, "Update");

        Assert.Equal(["find web", "remove bbbb", "rename aaaa web", "start aaaa"], daemon.Calls);
        Assert.Equal(("web", "running", "sha256:old"), Assert.Single(daemon.Containers).Value);
    }

    [Fact]
    public async Task Definite_rejection_of_the_create_looks_nothing_up_and_removes_nothing()
    {
        // 409: someone else holds the name — their container, even a never-started one from the same image, stays.
        var daemon = AfterFailedStart();
        var conflict = new DockerApiException(HttpStatusCode.Conflict, "Conflict. The container name is already in use");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", Image, conflict, "Update"));

        Assert.DoesNotContain(daemon.Calls, c => c.StartsWith("find", StringComparison.Ordinal));
        Assert.True(NoRemove(daemon));
        Assert.True(daemon.Containers.ContainsKey(NewId));
        Assert.Contains("now named 'web-old-1'", ex.Message);
    }

    [Fact]
    public void Only_a_4xx_docker_answer_is_a_definite_rejection()
    {
        Assert.True(ContainerRestore.IsDefiniteRejection(new DockerApiException(HttpStatusCode.Conflict, "")));
        Assert.True(ContainerRestore.IsDefiniteRejection(new DockerApiException(HttpStatusCode.BadRequest, "")));
        Assert.False(ContainerRestore.IsDefiniteRejection(new DockerApiException(HttpStatusCode.InternalServerError, "")));
        Assert.False(ContainerRestore.IsDefiniteRejection(LostAnswer));
    }

    [Fact]
    public async Task Lost_create_lookup_never_removes_a_holder_from_another_image()
    {
        var daemon = AfterFailedStart();
        daemon.Containers[NewId] = ("web", "created", "someone/else:2");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", Image, LostAnswer, "Update"));

        Assert.True(NoRemove(daemon));
        Assert.True(daemon.Containers.ContainsKey(NewId));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("exited")]
    [InlineData("paused")]
    public async Task Lost_create_lookup_never_removes_a_holder_that_was_started(string state)
    {
        var daemon = AfterFailedStart();
        daemon.Containers[NewId] = ("web", state, Image);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", Image, LostAnswer, "Update"));

        Assert.True(NoRemove(daemon));
        Assert.True(daemon.Containers.ContainsKey(NewId));
        Assert.Contains("now named 'web-old-1'", ex.Message);
    }

    [Fact]
    public async Task Lost_create_lookup_never_removes_the_previous_container()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web", "created", Image);  // the stash rename never took effect

        await ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", Image, LostAnswer, "Update");

        Assert.True(NoRemove(daemon));
        Assert.Equal(("web", "running", Image), daemon.Containers[OldId]);
    }

    [Fact]
    public async Task Failed_lookup_is_left_to_the_rename_to_report()
    {
        var daemon = AfterFailedStart();
        daemon.FailFind = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", Image, LostAnswer, "Update"));

        Assert.Contains("now named 'web-old-1'", ex.Message);
        Assert.Contains("already in use", ex.Message);
    }

    [Fact]
    public async Task New_container_that_cannot_be_removed_is_a_thrown_rollback_failure_naming_the_old_containers_name()
    {
        var daemon = AfterFailedStart();
        daemon.FailRemove = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, NewId, OldId, "web", "web-old-1", Image, StartFailure, "Update"));

        Assert.Contains("rollback failed", ex.Message);
        Assert.Contains("now named 'web-old-1'", ex.Message);
        Assert.Contains("still holds the name 'web'", ex.Message);
        // No rename into the name the new container still holds; the old one is started under its stashed name.
        Assert.DoesNotContain(daemon.Calls, c => c.StartsWith("rename", StringComparison.Ordinal));
        Assert.Equal(("web-old-1", "running", "sha256:old"), daemon.Containers[OldId]);
        var inner = Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Contains(StartFailure, inner.InnerExceptions);
    }

    [Fact]
    public async Task Failed_rename_back_is_thrown_with_the_stashed_name()
    {
        var daemon = AfterFailedStart();
        daemon.FailRename = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RestorePreviousAsync(daemon, NewId, OldId, "web", "web-old-1", Image, StartFailure, "Update"));

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
            ContainerRestore.RestorePreviousAsync(daemon, NewId, OldId, "web", "web-old-1", Image, StartFailure, "Rollback"));

        Assert.Contains("Rollback of 'web' failed", ex.Message);
        Assert.Contains("now named 'web' and is not running", ex.Message);
    }

    // ── The stash rename failed (the container may already be stopped) ──

    private static readonly Exception RenameFailure = new InvalidOperationException("rename timed out");

    [Fact]
    public async Task Failed_stash_rename_of_a_container_that_ran_starts_it_again_and_throws()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web", "exited", Image);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RecoverFailedStashAsync(daemon, OldId, "web", "web-old-1", wasRunning: true, RenameFailure, "Update"));

        Assert.Equal("start aaaa", daemon.Calls[0]);
        Assert.Equal(("web", "running", Image), daemon.Containers[OldId]);
        Assert.Contains("Update of 'web' did not happen", ex.Message);
        Assert.Contains("rename timed out", ex.Message);
        Assert.Contains("It is named 'web'; it was started again", ex.Message);
        Assert.Same(RenameFailure, ex.InnerException);
    }

    [Fact]
    public async Task Failed_stash_rename_that_took_effect_reports_the_stashed_name()
    {
        // The rename answered with an error but the daemon did rename it.
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web-old-1", "exited", Image);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RecoverFailedStashAsync(daemon, OldId, "web", "web-old-1", wasRunning: true, RenameFailure, "Update"));

        Assert.Contains("It is named 'web-old-1'", ex.Message);
    }

    [Fact]
    public async Task Failed_stash_rename_with_an_unreadable_name_says_so()
    {
        var daemon = new FakeDaemon { FailFind = true };
        daemon.Containers[OldId] = ("web", "exited", Image);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RecoverFailedStashAsync(daemon, OldId, "web", "web-old-1", wasRunning: true, RenameFailure, "Update"));

        Assert.Contains("current name could not be determined", ex.Message);
    }

    [Fact]
    public async Task Failed_stash_rename_of_a_container_that_was_not_running_leaves_it_stopped()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web", "exited", Image);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RecoverFailedStashAsync(daemon, OldId, "web", "web-old-1", wasRunning: false, RenameFailure, "Update"));

        Assert.DoesNotContain(daemon.Calls, c => c.StartsWith("start", StringComparison.Ordinal));
        Assert.Contains("was not running before", ex.Message);
    }

    [Fact]
    public async Task Failed_stash_rename_and_failed_restart_says_the_container_is_stopped()
    {
        var daemon = new FakeDaemon { FailStart = true };
        daemon.Containers[OldId] = ("web", "exited", Image);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RecoverFailedStashAsync(daemon, OldId, "web", "web-rollbackfail-1", wasRunning: true, RenameFailure, "Rollback"));

        Assert.Contains("starting it again failed too (start refused), it is stopped", ex.Message);
        Assert.IsType<AggregateException>(ex.InnerException);
    }

    // ── C12 rollback with no previous container ──

    [Fact]
    public async Task Rollback_without_a_previous_container_removes_the_rollback_container_it_created()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[NewId] = ("web", "created", Image);

        await ContainerRestore.RemoveCreatedAsync(daemon, NewId, "web", Image, StartFailure, "Rollback");

        Assert.Equal(["state bbbb", "remove bbbb"], daemon.Calls);
        Assert.Empty(daemon.Containers);
    }

    [Fact]
    public async Task Rollback_container_that_runs_after_all_is_kept()
    {
        // The start reported a failure but took effect.
        var daemon = new FakeDaemon();
        daemon.Containers[NewId] = ("web", "running", Image);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RemoveCreatedAsync(daemon, NewId, "web", Image, StartFailure, "Rollback"));

        Assert.True(NoRemove(daemon));
        Assert.Contains("is running; it was kept", ex.Message);
    }

    [Fact]
    public async Task Rollback_container_whose_state_cannot_be_read_is_kept()
    {
        var daemon = new FakeDaemon { FailState = true };
        daemon.Containers[NewId] = ("web", "created", Image);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RemoveCreatedAsync(daemon, NewId, "web", Image, StartFailure, "Rollback"));

        Assert.True(NoRemove(daemon));
        Assert.Contains("was kept because its state could not be read", ex.Message);
    }

    [Fact]
    public async Task Rollback_container_that_cannot_be_removed_is_thrown()
    {
        var daemon = new FakeDaemon { FailRemove = true };
        daemon.Containers[NewId] = ("web", "created", Image);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContainerRestore.RemoveCreatedAsync(daemon, NewId, "web", Image, StartFailure, "Rollback"));

        Assert.Contains("could not be removed", ex.Message);
        Assert.Contains("holds the name 'web' and is created", ex.Message);
    }

    [Fact]
    public async Task Rollback_whose_create_answer_was_lost_removes_its_never_started_container()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[NewId] = ("web", "created", Image);

        await ContainerRestore.RemoveCreatedAsync(daemon, null, "web", Image, LostAnswer, "Rollback");

        Assert.Equal(["find web", "state bbbb", "remove bbbb"], daemon.Calls);
        Assert.Empty(daemon.Containers);
    }

    [Fact]
    public async Task Rollback_whose_create_was_rejected_removes_nothing()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[NewId] = ("web", "created", Image);  // someone else's

        await ContainerRestore.RemoveCreatedAsync(daemon, null, "web", Image,
            new DockerApiException(HttpStatusCode.Conflict, "name in use"), "Rollback");

        Assert.Empty(daemon.Calls);
        Assert.True(daemon.Containers.ContainsKey(NewId));
    }

    [Theory]
    [InlineData("exited", Image)]
    [InlineData("created", "someone/else:2")]
    public async Task Rollback_lost_create_lookup_leaves_a_started_or_foreign_holder(string state, string image)
    {
        var daemon = new FakeDaemon();
        daemon.Containers[NewId] = ("web", state, image);

        await ContainerRestore.RemoveCreatedAsync(daemon, null, "web", Image, LostAnswer, "Rollback");

        Assert.True(NoRemove(daemon));
        Assert.True(daemon.Containers.ContainsKey(NewId));
    }
}
