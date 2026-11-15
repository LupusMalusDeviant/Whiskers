using Whiskers.Services.Docker.Operations;

namespace Whiskers.Tests;

/// <summary>1.0.0 review, Fleet HIGH 4: a <c>container.update</c> (or C12 rollback) whose new container is created but
/// fails to start or to join a network must leave the previous container running under its name. The new container
/// holds the name, so it is removed before the previous one is renamed back. A restore that fails is thrown with the
/// previous container's current name, never only logged.
/// <para>Driven through the <see cref="IContainerRestoreSteps"/> seam against a fake daemon that, like the real one,
/// gives a name to one container at a time: <c>ContainerLifecycleOperations</c> itself cannot run in this test project,
/// which loads Testcontainers' Docker.DotNet fork (a <see cref="MissingMethodException"/> on the real client). The
/// end-to-end repro over a fake HTTP daemon is <c>docs/review-1.0.0-repros/fleet-recreate-repro</c>.</para></summary>
public class ContainerRestoreTests
{
    private const string OldId = "aaaaaaaaaaaaaaaaaaaa";
    private const string NewId = "bbbbbbbbbbbbbbbbbbbb";

    private sealed class FakeDaemon : IContainerRestoreSteps
    {
        public readonly Dictionary<string, (string Name, bool Running)> Containers = new();
        public readonly List<string> Calls = [];
        public bool FailRemove, FailRename, FailStart;

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
            Containers[id] = (newName, Containers[id].Running);
            return Task.CompletedTask;
        }

        public Task StartAsync(string id)
        {
            Calls.Add($"start {id[..4]}");
            if (FailStart) throw new InvalidOperationException("start refused");
            Containers[id] = (Containers[id].Name, true);
            return Task.CompletedTask;
        }
    }

    /// <summary>The state right after a recreate whose new container did not start: the old one stopped and stashed, the
    /// new one created under the service's name.</summary>
    private static FakeDaemon AfterFailedStart()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web-old-1", false);
        daemon.Containers[NewId] = ("web", false);
        return daemon;
    }

    private static readonly Exception StartFailure = new InvalidOperationException("OCI runtime create failed");

    [Fact]
    public async Task Failed_start_of_the_new_container_restores_the_old_one_under_its_name()
    {
        var daemon = AfterFailedStart();

        await ContainerRestore.RestorePreviousAsync(daemon, NewId, OldId, "web", "web-old-1", StartFailure, "Update");

        Assert.Equal(["remove bbbb", "rename aaaa web", "start aaaa"], daemon.Calls);
        Assert.Equal(("web", true), Assert.Single(daemon.Containers).Value);
        Assert.Equal(OldId, daemon.Containers.Single().Key);
    }

    [Fact]
    public async Task Failed_create_has_nothing_to_remove_and_restores_the_old_one()
    {
        var daemon = new FakeDaemon();
        daemon.Containers[OldId] = ("web-old-1", false);

        await ContainerRestore.RestorePreviousAsync(daemon, null, OldId, "web", "web-old-1", StartFailure, "Update");

        Assert.Equal(["rename aaaa web", "start aaaa"], daemon.Calls);
        Assert.Equal(("web", true), daemon.Containers[OldId]);
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
        Assert.Equal(("web-old-1", true), daemon.Containers[OldId]);
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
}
