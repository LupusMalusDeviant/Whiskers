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
    public void A_cleanup_removes_the_decided_containers_by_id_and_no_network_or_volume()
    {
        // A network the policy protects could not be decided on before the prune: the cleanup leaves networks alone.
        // A container is removed by the id it was decided on — no prune that picks up one that stopped since.
        var command = Whiskers.Services.Scheduler.TaskExecutor.CleanupCommandFor(["c1", "c2"]);
        Assert.DoesNotContain("prune -a", command);
        Assert.DoesNotContain("container prune", command);
        Assert.DoesNotContain("network", command);
        Assert.DoesNotContain("volume", command);
        Assert.StartsWith("docker rm 'c1' 'c2' 2>&1 && ", command);
        Assert.DoesNotContain("docker rm", Whiskers.Services.Scheduler.TaskExecutor.CleanupCommandFor([]));
    }

    [Fact]
    public async Task A_cleanup_of_listed_containers_decides_exactly_those()
    {
        var h = new ProtectionHarness();
        var db = h.Containers.Single(c => c.Name == "whiskers-db");
        var other = h.Containers.Single(c => c.Name == "web");
        db.State = "exited";
        other.State = "exited";

        // Listed before whiskers-db stopped: it is not decided and not removed.
        Assert.Equal(MutationStatus.Executed, (await h.RunAsync("host.prune",
            new { serverId = "local", containerIds = other.Id }, ProtectionHarness.AdminsTask(), MutationSurface.Automation)).Status);
        Assert.Equal(MutationStatus.Denied, (await h.RunAsync("host.prune",
            new { serverId = "local", containerIds = $"{other.Id}\n{db.Id}" }, ProtectionHarness.AdminsTask(),
            MutationSurface.Automation)).Status);
        // One that is gone meanwhile refuses the run.
        Assert.NotEqual(MutationStatus.Executed, (await h.RunAsync("host.prune",
            new { serverId = "local", containerIds = "gone0000" }, ProtectionHarness.AdminsTask(), MutationSurface.Automation)).Status);
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
        // A plan made again that finds it undecided says so without quoting it.
        var ex = Assert.Throws<ComposePlanException>(() => plan.EnsureDecided(null));
        Assert.DoesNotContain("canary", ex.Message);
        Assert.Contains("/srv/plain", ex.Message);
    }

    [Fact]
    public async Task A_whole_mount_from_a_variable_is_decided_as_what_it_expands_to()
    {
        _host.Files["/opt/deployments/tools/.env"] = "MOUNT_SPEC=/:/host\n";
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "services:\n  a:\n    image: x\n    volumes:\n      - ${MOUNT_SPEC}\n"));
        Assert.Equal(["/"], plan.Mounts());
        Assert.Equal(["/"], plan.WithheldMounts());
    }

    [Fact]
    public async Task A_relative_mount_is_decided_in_the_project_directory_the_deployment_runs_with()
    {
        var plan = await Planner().PlanAsync(new ComposeDeployment("local",
            [new ComposeFileInput("/srv/tools/compose.yml", "services:\n  a:\n    image: x\n    volumes:\n      - ./certs:/certs\n")])
        {
            ProjectDirectory = "/opt/dockerproxy",
        });
        Assert.Equal(["/opt/dockerproxy/certs"], plan.Mounts());
    }

    [Fact]
    public async Task A_relative_long_bind_mount_is_a_host_path()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "services:\n  a:\n    image: x\n    volumes:\n      - type: bind\n        source: data\n        target: /d\n" +
            "      - type: volume\n        source: cache\n        target: /c\n"));
        Assert.Contains("/opt/deployments/tools/data", plan.Mounts());
        Assert.Contains("cache", plan.Mounts());                         // a named volume stays one
        Assert.DoesNotContain("data", plan.Mounts());
    }

    [Fact]
    public async Task Mounts_merged_through_several_anchors_are_decided()
    {
        _host.Files["/opt/deployments/tools/.env"] = "SRC=/\n";
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "x-base: &base\n  volumes: [\"${SRC}:/host\"]\nx-mid: &mid\n  <<: *base\nservices:\n  tool:\n    <<: *mid\n    image: alpine\n"));
        Assert.Contains("/", plan.Mounts());
    }

    [Fact]
    public async Task File_backed_secrets_and_configs_are_decided_as_their_files()
    {
        _host.Files["/opt/deployments/tools/.env"] = "CA_FILE=/opt/dockerproxy/certs/key.pem\n";
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "services:\n  a:\n    image: x\n    secrets: [ca]\n    configs:\n      - source: app\n        target: /etc/app\n" +
            "secrets:\n  ca:\n    file: ${CA_FILE}\n  unused:\n    external: true\nconfigs:\n  app:\n    file: ./app.conf\n"));
        Assert.Contains("/opt/dockerproxy/certs/key.pem", plan.Mounts());
        Assert.Contains("/opt/deployments/tools/app.conf", plan.Mounts());
        Assert.Contains("/opt/dockerproxy/certs/key.pem", plan.WithheldMounts());
    }

    [Fact]
    public async Task Volumes_from_a_service_of_another_file_are_decided()
    {
        var plan = await Planner().PlanAsync(new ComposeDeployment("local",
        [
            new ComposeFileInput("/opt/deployments/tools/compose.yml", "services:\n  data:\n    image: x\n    volumes:\n      - /srv/data:/d\n"),
            new ComposeFileInput("/opt/deployments/tools/override.yml", "services:\n  app:\n    image: y\n    volumes_from: [data]\n"),
        ]));
        Assert.Equal(["/srv/data"], plan.Services.Single(s => s.Name == "app").Mounts.Select(m => m.Source));
    }

    [Fact]
    public async Task Volumes_taken_from_a_container_or_another_service_are_decided()
    {
        _host.Mounts["legacy-id"] = """[{"Type":"bind","Source":"/var/run/docker.sock"},{"Type":"volume","Name":"serverwatch-data","Source":"/var/lib/docker/volumes/serverwatch-data/_data"}]""";
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "services:\n  a:\n    image: x\n    volumes_from:\n      - container:legacy-proxy:rw\n      - b\n" +
            "  b:\n    image: y\n    volumes:\n      - /srv/b:/b\n"));
        Assert.Contains("legacy-proxy", plan.Mounts());
        Assert.Contains("legacy-id", plan.Mounts());
        Assert.Contains("/srv/b", plan.Mounts());
        // What the source container mounts, the new one gets too.
        Assert.Contains("/var/run/docker.sock", plan.Mounts());
        Assert.Contains("serverwatch-data", plan.Mounts());
    }

    [Fact]
    public async Task A_container_whose_mounts_cannot_be_read_refuses_the_plan() =>
        await Assert.ThrowsAsync<ComposePlanException>(() => Planner().PlanAsync(ComposeDeployment.FromContent("local",
            "/opt/deployments/tools", "services:\n  a:\n    image: x\n    volumes_from: [\"container:legacy-proxy\"]\n")));

    [Fact]
    public async Task A_mount_only_compose_itself_resolves_is_decided_and_withheld()
    {
        // e.g. through extends or an anchor form the model does not follow: compose's own model counts.
        _host.ComposeConfig = """
            {"services":{"a":{"volumes":[{"type":"bind","source":"/opt/dockerproxy/certs","target":"/c"},
              {"type":"volume","source":"data","target":"/d"},{"type":"tmpfs","target":"/t"}]}},
             "volumes":{"data":{"name":"serverwatch-data"}}}
            """;
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/tools",
            "services:\n  a:\n    image: x\n    volumes:\n      - /srv/a:/a\n"));
        Assert.Contains("/srv/a", plan.Mounts());
        Assert.Contains("/opt/dockerproxy/certs", plan.WithheldMounts());
        Assert.Contains("serverwatch-data", plan.WithheldMounts());
        Assert.DoesNotContain("/srv/a", plan.WithheldMounts());
        // The planned file reaches compose encoded, never as text on the command line.
        Assert.DoesNotContain("/srv/a", Assert.Single(_host.ConfigCommands));
    }

    [Theory]
    [InlineData(null)]                                                       // compose fails
    [InlineData("not json")]
    [InlineData("""{"volumes":{}}""")]                                       // no services
    [InlineData("""{"services":{"ghost":{}}}""")]                            // a service the files do not show
    [InlineData("""{"services":{"a":{"volumes":[{"type":"cluster","source":"x"}]}}}""")]   // an unknown mount kind
    [InlineData("""{"services":{"a":{"volumes_from":["nowhere"]}}}""")]      // volumes_from an undefined service
    public async Task A_deployment_compose_cannot_resolve_refuses_the_plan(string? config)
    {
        _host.ComposeConfig = config;
        await Assert.ThrowsAsync<ComposePlanException>(() => Planner().PlanAsync(ComposeDeployment.FromContent("local",
            "/opt/deployments/tools", "services:\n  a:\n    image: x\n")));
    }

    [Fact]
    public void Composes_model_yields_every_source_a_service_mounts()
    {
        var effects = ComposeConfigEffects.Parse("""
            {"services":{
               "a":{"volumes":[{"type":"volume","source":"cache"},{"type":"volume","source":"ext"},{"type":"volume","target":"/anon"}],
                    "volumes_from":["b","container:legacy:ro"],"secrets":[{"source":"ca"}],"configs":["app"]},
               "b":{"volumes":[{"type":"bind","source":"/srv/b/../b"}]}},
             "volumes":{"cache":{"driver_opts":{"device":"/opt/dockerproxy","o":"bind"}}},
             "secrets":{"ca":{"file":"/etc/ssl/ca.pem"}},"configs":{"app":{"file":"/opt/app.conf"}}}
            """, "tools");
        var a = effects["a"];
        Assert.Equal(["tools_cache", "/opt/dockerproxy", "ext", "/srv/b", "legacy", "/etc/ssl/ca.pem", "/opt/app.conf"],
            a.Select(m => m.Source));
        Assert.True(a.Single(m => m.Source == "legacy").Container);
        Assert.All(a, m => Assert.True(m.FromVariable));
    }

    [Fact]
    public void Composes_model_is_built_from_the_planned_files_and_env()
    {
        var plan = new ComposePlan("local", "tools", "/opt/t", ["/opt/t/compose.yml", "/opt/t/extra.yml"],
            [new ComposeFileInput("/opt/t/compose.yml", "services: {}")], "SECRET=canary-secret", HasEnvFile: true, false, [], []);
        var command = ComposeConfigEffects.Command(plan);
        Assert.DoesNotContain("canary", command);
        Assert.Contains("-f \"$d/0.yml\" -f '/opt/t/extra.yml' --env-file \"$d/.env\"", command);
        Assert.Contains("-p 'tools' --project-directory '/opt/t'", command);
        Assert.StartsWith("d=$(mktemp -d) && trap", command);
    }

    [Theory]
    [InlineData("container:c0")]                     // a short id prefix
    [InlineData("container:${SOURCE}")]              // an id from the .env
    public async Task A_container_volumes_are_taken_from_is_decided_as_the_container_it_is(string reference)
    {
        var docker = new FakeDocker(new ContainerInfo { Id = "c0ffee000000whiskers", Name = "serverwatch", ServerId = "local" });
        _host.Files["/opt/deployments/tools/.env"] = "SOURCE=c0ffee000000whiskers\n";
        _host.Mounts["c0ffee000000whiskers"] = "null";
        var plan = await new ComposePlanner(docker, _host).PlanAsync(ComposeDeployment.FromContent("local",
            "/opt/deployments/tools", $"services:\n  a:\n    image: x\n    volumes_from:\n      - \"{reference}:rw\"\n"));
        Assert.Contains("serverwatch", plan.Mounts());
        Assert.Contains("c0ffee000000whiskers", plan.Mounts());
    }

    [Fact]
    public async Task A_container_volumes_are_taken_from_that_does_not_exist_refuses_the_plan_without_naming_it()
    {
        _host.Files["/opt/deployments/tools/.env"] = "SOURCE=canary-secret\n";
        var ex = await Assert.ThrowsAsync<ComposePlanException>(() => Planner().PlanAsync(ComposeDeployment.FromContent(
            "local", "/opt/deployments/tools", "services:\n  a:\n    image: x\n    volumes_from:\n      - container:${SOURCE}\n      - ${SOURCE}\n")));
        Assert.DoesNotContain("canary", ex.Message);
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
