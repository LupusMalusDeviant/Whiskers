using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// R1-16 (INV-24): what an operation touches is found beyond its target argument — in commands, SQL and compose
/// files, by alternate ids and names, and in what a network change or a cleanup cuts off — and decided on a protected
/// resource of the policy as on the control plane. Free text that builds its target at run time is treated as
/// touching one.
/// </summary>
public sealed partial class ProtectedResourceResolutionTests
{
    private static ProtectionHarness Protecting(params string[] patterns) =>
        new(p => p.ProtectedResources = [.. patterns]);

    private static Task<MutationOutcome<int>> AsKey(ProtectionHarness h, string capability, object arguments) =>
        h.RunAsync(capability, arguments, ProtectionHarness.AdminKey, MutationSurface.Mcp);

    [Theory]
    [InlineData("host.exec", "command", "docker exec web pg_dump shop > /tmp/x")]               // a command
    [InlineData("host.exec", "command", "docker restart aaaa99887766")]                         // by id
    [InlineData("db.query", "query", "TRUNCATE web.orders")]                                    // SQL
    [InlineData("compose.deploy", "yaml", "services:\n  web:\n    image: nginx\n")]              // compose
    public async Task A_policy_protected_name_in_free_text_is_decided(string capability, string key, string text)
    {
        var h = Protecting("web");
        var args = new Dictionary<string, object> { ["serverId"] = "local", [key] = text };
        if (capability == "db.query") args["containerId"] = "whiskers-db-other";

        var outcome = await AsKey(h, capability, args);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public async Task A_protected_container_is_found_by_any_of_its_names()
    {
        var h = Protecting("web");
        foreach (var name in new[] { "web", "aaaa99887766web", "aaaa99" })
            Assert.Equal(MutationStatus.Denied,
                (await AsKey(h, "container.restart", new { containerId = name, serverId = "local" })).Status);
        // By id when only the id is protected.
        var byId = Protecting("aaaa99887766web");
        Assert.Equal(MutationStatus.Denied,
            (await AsKey(byId, "container.restart", new { containerId = "web", serverId = "local" })).Status);
    }

    [Fact]
    public async Task Disconnecting_a_protected_container_from_a_network_is_decided_on_it()
    {
        var h = new ProtectionHarness();
        h.Docker.Networks.Add(new NetworkInfo
        {
            Id = "n1", Name = "edge", ServerId = "local",
            Containers = [new() { ContainerId = "c0ffee123456whiskers", Name = "whiskers" }],
        });

        var disconnect = await AsKey(h, "network.disconnect", new { networkId = "edge", containerId = "c0ffee", serverId = "local" });
        var remove = await AsKey(h, "network.remove", new { networkId = "edge", serverId = "local" });

        Assert.Equal(MutationStatus.Denied, disconnect.Status);
        Assert.Equal(MutationStatus.Denied, remove.Status);
    }

    [Fact]
    public async Task A_cleanup_is_decided_on_every_stopped_container_it_removes()
    {
        var h = new ProtectionHarness();
        Assert.Equal(MutationStatus.Executed,
            (await h.RunAsync("host.prune", new { serverId = "local" }, ProtectionHarness.AdminsTask(), MutationSurface.Automation)).Status);

        h.Containers.Single(c => c.Name == "whiskers-db").State = "exited";
        var outcome = await h.RunAsync("host.prune", new { serverId = "local" }, ProtectionHarness.AdminsTask(),
            MutationSurface.Automation);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(1, h.Runs);
    }

    [Fact]
    public async Task A_cleanup_whose_containers_cannot_be_listed_is_refused()
    {
        var h = new ProtectionHarness();
        h.Docker.ListContainersThrows = true;

        var outcome = await h.RunAsync("host.prune", new { serverId = "local" }, ProtectionHarness.AdminsTask(),
            MutationSurface.Automation);

        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public async Task A_command_naming_a_container_id_that_cannot_be_checked_is_refused()
    {
        var h = new ProtectionHarness();
        h.Docker.ListContainersThrows = true;

        var outcome = await AsKey(h, "host.exec", new { serverId = "local", command = "docker rm -f 0123abcd" });

        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.Equal(0, h.Runs);
    }

    [Theory]
    [InlineData("docker stop $(docker ps -q)")]
    [InlineData("echo d2hpc2tlcnM= | base64 -d | xargs docker stop")]
    [InlineData("eval \"$CMD\"")]
    public async Task Free_text_that_builds_its_target_at_run_time_is_refused_and_an_Admin_must_approve_it(string command)
    {
        var h = new ProtectionHarness();
        var args = new { serverId = "local", command };

        Assert.Equal(MutationStatus.Denied, (await AsKey(h, "host.exec", args)).Status);
        Assert.Equal(MutationStatus.PendingApproval,
            (await h.RunAsync("host.exec", args, ProtectionHarness.UiAdmin, MutationSurface.Ui)).Status);
        Assert.Equal(McpPermissionLevels.Admin, Assert.Single(h.Approvals.GetPending()).Level);
    }
}
