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
    [InlineData("db.query", "query", "DROP DATABASE web;")]                                     // SQL
    [InlineData("compose.deploy", "yaml", "services:\n  web:\n    image: nginx\n")]              // compose
    public async Task A_policy_protected_name_in_free_text_is_decided(string capability, string key, string text)
    {
        var h = Protecting("web");
        var args = new Dictionary<string, object> { ["serverId"] = "local", [key] = text };
        if (capability == "db.query") args["containerId"] = "billing";

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
    public void A_cleanup_removes_no_network_and_no_volume()
    {
        // A network the policy protects could not be decided on before the prune: the cleanup leaves networks alone.
        var command = Whiskers.Services.Scheduler.TaskExecutor.CleanupCommand;
        Assert.DoesNotContain("system prune", command);
        Assert.DoesNotContain("network", command);
        Assert.DoesNotContain("volume", command);
        Assert.Contains("docker container prune", command);
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

    // ---------------------------------------------------------------- mounts

    [Fact]
    public async Task A_plan_lists_what_its_services_mount()
    {
        _host.Files["/srv/m/.env"] = "DATA=/app/data\n";
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/srv/m", """
            name: m
            services:
              a:
                image: x
                volumes:
                  - ${DATA}:/d
                  - cache:/c
                  - shared:/s
                  - type: bind
                    source: /var/run/docker.sock
                    target: /s.sock
                  - /anonymous
            volumes:
              cache: {}
              shared:
                name: serverwatch-data
                external: true
            """));

        Assert.Equal(["/app/data", "cache", "m_cache", "serverwatch-data", "/var/run/docker.sock"], plan.Mounts());
    }

    [Fact]
    public async Task A_volume_bound_to_a_host_device_is_decided_as_that_path()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "services:\n  a:\n    image: x\n    volumes:\n      - host:/host\n      - nfs:/n\n" +
            "volumes:\n  host:\n    driver: local\n    driver_opts:\n      type: none\n      o: bind\n      device: /\n" +
            "  nfs:\n    driver_opts:\n      type: nfs\n      device: \":/export\"\n"));
        Assert.Contains("/", plan.Mounts());
        Assert.DoesNotContain(":/export", plan.Mounts());
    }

    [Fact]
    public async Task A_mount_from_an_env_value_is_decided_but_withheld_from_records()
    {
        _host.Files["/opt/deployments/tools/.env"] = "DB_PASSWORD=canary-secret\n";
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "services:\n  a:\n    image: x\n    volumes:\n      - /srv/${DB_PASSWORD}:/data\n      - /srv/plain:/p\n"));
        Assert.Contains("/srv/canary-secret", plan.Mounts());
        Assert.Equal(["/srv/canary-secret"], plan.WithheldMounts());
    }

    [Fact]
    public async Task Volumes_taken_from_a_container_or_another_service_are_decided()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "services:\n  a:\n    image: x\n    volumes_from:\n      - container:serverwatch:rw\n      - b\n" +
            "  b:\n    image: y\n    volumes:\n      - /srv/b:/b\n"));
        Assert.Contains("serverwatch", plan.Mounts());
        Assert.Contains("/srv/b", plan.Mounts());
    }

    [Fact]
    public async Task A_relative_mount_is_decided_where_it_lands()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "services:\n  a:\n    image: x\n    volumes:\n      - ../../..:/host\n      - ./conf:/c\n"));
        Assert.Equal(["/", "/opt/deployments/tools/conf"], plan.Mounts());
    }

    [Fact]
    public async Task A_mount_from_a_variable_whiskers_cannot_see_refuses_the_plan() =>
        await Assert.ThrowsAsync<ComposePlanException>(() => Planner().PlanAsync(ComposeDeployment.FromContent("local",
            "/srv/n", "services:\n  a:\n    image: x\n    volumes:\n      - ${NOWHERE}:/d\n")));

    [Fact]
    public async Task A_deployment_mounting_the_control_plane_is_refused_for_a_key_and_needs_an_Admin_approval_in_the_ui()
    {
        var h = new ProtectionHarness(planner: docker => new ComposePlanner(docker, _host));
        var args = new
        {
            serverId = "local", projectName = "tools",
            composeContent = "services:\n  t:\n    image: x\n    volumes:\n      - /var/run/docker.sock:/var/run/docker.sock\n",
        };

        Assert.Equal(MutationStatus.Denied, (await AsKey(h, "compose.deploy", args)).Status);
        Assert.Equal(MutationStatus.PendingApproval,
            (await h.RunAsync("compose.deploy", args, ProtectionHarness.UiAdmin, MutationSurface.Ui)).Status);
    }

    [Fact]
    public async Task The_mounts_decision_of_a_planned_deployment_covers_each_mount()
    {
        var h = new ProtectionHarness();
        var refused = await AsKey(h, "compose.deploy", new { serverId = "local", mounts = "/srv/a\nserverwatch-data" });
        var allowed = await AsKey(h, "compose.deploy", new { serverId = "local", mounts = "/srv/a\nweb-cache" });

        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Equal(MutationStatus.Executed, allowed.Status);
    }

    [Fact]
    public async Task A_plan_whose_mounts_were_not_decided_does_not_run()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/srv/o",
            "services:\n  a:\n    image: x\n    volumes:\n      - /etc:/e\n"));
        Assert.Throws<ComposePlanException>(() => plan.EnsureDecided(null));
    }
}
