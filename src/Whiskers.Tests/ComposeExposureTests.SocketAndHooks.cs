using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0017, part A: <c>use_api_socket</c> is a read-write bind mount of the Docker socket (A1), and a lifecycle
/// hook with <c>privileged: true</c> is a <see cref="ExposureKind.Privileged"/> exposure of its service (A2).
/// </summary>
public sealed partial class ComposeExposureTests
{
    private const string ProjectDirectory = "/opt/deployments/x";

    /// <summary>What a plan decides for one compose file: the model's reading united with
    /// <paramref name="config"/>, a <c>docker compose config</c> model for the file (<paramref name="yaml"/>), the way
    /// <see cref="ComposePlanner"/> unites them.</summary>
    private static ComposePlan Planned(string yaml, string config)
    {
        var prefixes = GuardrailPolicy.DefaultExposureLabelPrefixes;
        var env = new Dictionary<string, string?>();
        var files = new List<(string Path, string Content)> { ($"{ProjectDirectory}/docker-compose.yml", yaml) };
        var (_, _, services) = ComposeModel.MergeWithSource(files, env, ProjectDirectory);
        services = ComposeModel.WithEffects(services, ComposeConfigEffects.Parse(config, "x"));
        var (model, _) = ComposeModel.ExposuresOf(files, env, prefixes);
        var active = ComposeConfigEffects.ActiveServices(config, "x", prefixes);
        services = services.Select(s => s with
        {
            Active = true,
            Exposures = [.. model.GetValueOrDefault(s.Name) ?? [], .. active.Where(a => a.Name == s.Name).SelectMany(a => a.Exposures)],
        }).ToList();
        return new ComposePlan("srv1", "x", ProjectDirectory, [files[0].Path], [], null, false, false, services, []);
    }

    private static string Decided(ComposePlan plan) =>
        $"mounts [{string.Join(", ", plan.Mounts())}], exposures [{string.Join(", ", plan.Exposures().Select(e => e.Identity))}]";

    /// <summary>
    /// INV-59 ("the Docker socket … applies to every non-read call on every surface") and ComposePlan.Mounts ("a mount of
    /// the Docker socket … reaches the control plane"): <c>use_api_socket: true</c> (Compose spec) makes compose bind the
    /// engine's API socket into the container at <c>up</c>. <c>docker compose config</c> prints only the key, no volume,
    /// so the plan decides no mount — and the re-check after <c>up</c> reads no mounts either.
    /// </summary>
    [Fact]
    public void A_service_given_the_Docker_socket_by_use_api_socket_is_decided_on_that_mount()
    {
        // Control: the same socket as a written bind mount is decided.
        var written = Planned(
            "services:\n  web:\n    image: nginx:1.27\n    volumes:\n      - /var/run/docker.sock:/var/run/docker.sock\n",
            """{"name":"x","services":{"web":{"image":"nginx:1.27","networks":{"default":null},"volumes":[{"type":"bind","source":"/var/run/docker.sock","target":"/var/run/docker.sock","bind":{"create_host_path":true}}]}}}""");
        Assert.Contains("/var/run/docker.sock", written.Mounts());

        var plan = Planned(
            "services:\n  web:\n    image: nginx:1.27\n    use_api_socket: true\n",
            """{"name":"x","networks":{"default":{"name":"x_default","ipam":{}}},"services":{"web":{"command":null,"entrypoint":null,"image":"nginx:1.27","networks":{"default":null},"use_api_socket":true}}}""");

        Assert.True(plan.Mounts().Contains("/var/run/docker.sock"),
            $"use_api_socket hands the service the Docker socket, but the plan decides {Decided(plan)}");
        Assert.Contains(plan.Services.Single().Mounts, m => m is { Source: "/var/run/docker.sock", ApiSocket: true });
    }

    /// <summary>
    /// INV-62 ("a compose deployment that … takes the host's … privileges, capabilities … is decided on each of these"):
    /// a lifecycle hook with <c>privileged: true</c> runs its command in the container with every capability and without
    /// seccomp/AppArmor — what <c>privileged</c>, <c>cap_add</c> and <c>security_opt</c> are decided for. Neither the
    /// model nor the reading of <c>docker compose config</c> looks at <c>post_start</c> / <c>pre_stop</c>.
    /// </summary>
    [Theory]
    [InlineData("post_start")]
    [InlineData("pre_stop")]
    public void A_privileged_lifecycle_hook_is_decided_as_a_host_exposure(string hook)
    {
        // Control: privileged on the service itself is decided.
        var direct = Planned(
            "services:\n  web:\n    image: nginx:1.27\n    privileged: true\n",
            """{"name":"x","services":{"web":{"image":"nginx:1.27","networks":{"default":null},"privileged":true}}}""");
        Assert.Contains(direct.Exposures(), e => e.Kind == ExposureKind.Privileged);

        var plan = Planned(
            $"services:\n  web:\n    image: nginx:1.27\n    {hook}:\n      - command: [\"sh\", \"-c\", \"true\"]\n        privileged: true\n        user: root\n",
            """{"name":"x","services":{"web":{"HOOK":[{"command":["sh","-c","true"],"user":"root","privileged":true}],"command":null,"entrypoint":null,"image":"nginx:1.27","networks":{"default":null}}}}""".Replace("HOOK", hook));

        Assert.True(plan.Exposures().Count > 0,
            $"{hook} runs a privileged command as root in the container, but the plan decides {Decided(plan)}");
    }

    // ---------------------------------------------------------------- A1: the socket counts as a mount

    [Fact]
    public void A_plan_with_use_api_socket_has_a_mount_of_the_Docker_socket_from_the_model_and_from_compose()
    {
        var plan = Planned(
            "services:\n  web:\n    image: nginx:1.27\n    use_api_socket: true\n",
            """{"services":{"web":{"image":"nginx:1.27","use_api_socket":true}}}""");
        Assert.Equal(["/var/run/docker.sock"], plan.Mounts());
        Assert.Empty(plan.WithheldMounts());   // the model read it literally

        // compose's model alone (e.g. the key came through extends of another file) still decides it, withheld.
        var fromConfig = ComposeConfigEffects.Parse("""{"services":{"web":{"image":"x","use_api_socket":true}}}""", "x");
        Assert.Contains(fromConfig["web"], m => m.Source == "/var/run/docker.sock");
    }

    [Theory]
    [InlineData("false")]
    [InlineData("\"false\"")]
    public void Use_api_socket_false_mounts_nothing(string value)
    {
        var plan = Planned(
            $"services:\n  web:\n    image: nginx:1.27\n    use_api_socket: {value}\n",
            """{"services":{"web":{"image":"nginx:1.27","use_api_socket":false}}}""");
        Assert.Empty(plan.Mounts());
    }

    [Fact]
    public void Use_api_socket_from_a_variable_is_decided_but_withheld()
    {
        var files = new List<(string Path, string Content)>
        {
            ($"{ProjectDirectory}/docker-compose.yml", "services:\n  web:\n    image: nginx\n    use_api_socket: ${SOCK}\n"),
        };
        var (_, _, services) = ComposeModel.MergeWithSource(files, new Dictionary<string, string?> { ["SOCK"] = "true" }, ProjectDirectory);
        var mount = Assert.Single(Assert.Single(services).Mounts);
        Assert.Equal("/var/run/docker.sock", mount.Source);
        Assert.True(mount.FromVariable);
    }

    [Fact]
    public void Use_api_socket_is_a_socket_mount_exposure_of_class_host_in_the_model_and_in_config()
    {
        var exposure = Assert.Single(Of("services:\n  web:\n    image: nginx\n    use_api_socket: true\n"));
        Assert.Equal(ExposureKind.SocketMount, exposure.Kind);
        Assert.Equal("mount:/var/run/docker.sock", exposure.Key);
        Assert.Equal("/var/run/docker.sock", exposure.Value);
        Assert.False(exposure.Withheld);
        Assert.Equal(ExposureClasses.Host, ExposureClasses.Of(exposure.Kind, exposure.Key, Prefixes));
        Assert.Equal("web: socket mount mount:/var/run/docker.sock", ComposeExposures.Describe(exposure.Identity, false));

        using var config = JsonDocument.Parse("""{"image":"nginx","use_api_socket":true}""");
        Assert.Equal(exposure.Identity, Assert.Single(ComposeExposures.FromConfig("web", config.RootElement,
            new Dictionary<string, JsonElement>(), Prefixes)).Identity);
    }

    [Theory]
    [InlineData("x-base: &base\n  use_api_socket: true\n  post_start:\n    - command: [\"id\"]\n      privileged: true\nservices:\n  web:\n    <<: *base\n    image: nginx\n")]
    [InlineData("services:\n  base:\n    image: nginx\n    use_api_socket: true\n    post_start:\n      - command: [\"id\"]\n        privileged: true\n  web:\n    extends: base\n")]
    public void Use_api_socket_and_a_hooks_privileged_are_read_through_merges_and_extends(string yaml)
    {
        var exposures = Of(yaml);
        Assert.Contains(exposures, e => e is { Kind: ExposureKind.SocketMount, Key: "mount:/var/run/docker.sock" });
        Assert.Contains(exposures, e => e is { Kind: ExposureKind.Privileged, Key: "hook:post_start:0" });

        var (_, _, services) = ComposeModel.MergeWithSource([($"{ProjectDirectory}/docker-compose.yml", yaml)],
            new Dictionary<string, string?>(), ProjectDirectory);
        Assert.Contains(services.Single(s => s.Name == "web").Mounts, m => m.Source == "/var/run/docker.sock");
    }

    [Theory]
    [InlineData("use_api_socket: ${UNSET}")]
    [InlineData("post_start:\n      - command: [\"id\"]\n        privileged: ${UNSET}")]
    public void Use_api_socket_or_a_hooks_privileged_from_an_unset_variable_refuses_the_plan(string field) =>
        Assert.Throws<ComposePlanException>(() => Of($"services:\n  web:\n    image: nginx\n    {field}\n"));

    [Fact]
    public void Use_api_socket_from_an_unset_variable_refuses_the_mounts_too() =>
        Assert.Throws<ComposePlanException>(() => ComposeModel.MergeWithSource(
            [($"{ProjectDirectory}/docker-compose.yml", "services:\n  web:\n    image: nginx\n    use_api_socket: ${UNSET}\n")],
            new Dictionary<string, string?>(), ProjectDirectory));

    [Fact]
    public void Socket_mounts_are_read_from_a_containers_mounts()
    {
        using var doc = JsonDocument.Parse("""
            {"Mounts":[{"Type":"bind","Source":"/run/docker.sock","Destination":"/var/run/docker.sock","RW":true},
                       {"Type":"bind","Source":"/run/user/1000/docker.sock","Destination":"/rootless.sock","RW":true},
                       {"Type":"bind","Source":"/srv/app","Destination":"/app","RW":true},
                       {"Type":"volume","Name":"v","Source":"/var/lib/docker/volumes/v/_data"}]}
            """);
        var sockets = ComposeExposures.SocketMounts("web", doc.RootElement)!;
        Assert.Equal(["mount:/var/run/docker.sock=/run/docker.sock", "mount:/rootless.sock=/run/user/1000/docker.sock"],
            sockets.Select(s => $"{s.Key}={s.Value}"));
        Assert.All(sockets, s => Assert.Equal(ExposureKind.SocketMount, s.Kind));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Mounts":"x"}""")]
    [InlineData("""{"Mounts":[{"Type":"bind"}]}""")]
    [InlineData("""{"Mounts":[{"Source":"/run/docker.sock","Destination":"/s"}]}""")]               // no Type
    [InlineData("""{"Mounts":[{"Type":"weird","Source":"/run/docker.sock","Destination":"/s"}]}""")] // unknown Type
    [InlineData("""{"Mounts":["x"]}""")]
    public void Mounts_that_cannot_be_read_are_reported_as_unreadable(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Null(ComposeExposures.SocketMounts("web", doc.RootElement));
    }

    [Fact]
    public void A_mount_type_is_compared_case_insensitively()
    {
        using var doc = JsonDocument.Parse("""{"Mounts":[{"Type":"Bind","Source":"/var/run/docker.sock","Destination":"/s"},{"Type":"VOLUME","Name":"v"}]}""");
        Assert.Equal("mount:/s", Assert.Single(ComposeExposures.SocketMounts("web", doc.RootElement)!).Key);
    }

    // ---------------------------------------------------------------- A1: decided per service

    private static ComposeServicePlan Service(string name, params string[] mounts) =>
        new(name, null) { Mounts = mounts.Select(m => new ComposeMount(m, false)).ToList() };

    [Theory]
    [InlineData("/var/run/docker.sock", "/run/docker.sock", true)]    // the control plane's socket paths are one source
    [InlineData("/run/docker.sock", "/var/run/docker.sock", true)]
    [InlineData("/var/run/docker.sock", "/var/run/docker.sock", true)]
    [InlineData("/run/user/1000/docker.sock", "/run/user/1000/docker.sock", true)]
    [InlineData("/run/user/1000/docker.sock", "/var/run/docker.sock", false)]
    [InlineData("/srv/app", "/var/run/docker.sock", false)]
    public void A_socket_bind_is_decided_when_the_service_decided_the_same_source(string decided, string inspected, bool expected) =>
        Assert.Equal(expected, ComposeExposures.SocketDecided(Service("web", decided), inspected));

    [Fact]
    public void A_socket_decided_for_another_service_does_not_decide_this_one() =>
        Assert.False(ComposeExposures.SocketDecided(Service("web", "/srv/app"), "/var/run/docker.sock"));

    [Fact]
    public void With_use_api_socket_the_engines_socket_is_decided_whatever_its_path()
    {
        var web = new ComposeServicePlan("web", null)
        {
            Mounts = [new ComposeMount(ComposeModel.DockerSocket, false) { ApiSocket = true }],
        };
        Assert.True(ComposeExposures.SocketDecided(web, "/run/user/1000/docker.sock"));
        Assert.False(ComposeExposures.SocketDecided(web, "/srv/app"));
        // A written volume of the same path is not use_api_socket: only that source is decided.
        Assert.False(ComposeExposures.SocketDecided(Service("web", ComposeModel.DockerSocket), "/run/user/1000/docker.sock"));
    }

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/run/user/1000/docker.sock")]   // a rootless engine: the API socket is wherever the engine's is
    public void Volumes_from_a_service_with_use_api_socket_gives_the_receiver_the_decided_socket(string inspected)
    {
        // From the model alone and from compose's model alone: the API-socket fact survives volumes_from both ways.
        foreach (var config in (string[])[
                     """{"services":{"sock":{"image":"nginx","use_api_socket":true},"web":{"image":"nginx","volumes_from":["service:sock"]}}}""",
                     """{"services":{"sock":{"image":"nginx"},"web":{"image":"nginx"}}}"""])
        {
            var plan = Planned(
                "services:\n  sock:\n    image: nginx\n    use_api_socket: true\n  web:\n    image: nginx\n    volumes_from: [sock]\n",
                config);
            Assert.True(ComposeExposures.SocketDecided(plan.Services.Single(s => s.Name == "web"), inspected));
        }
        var configOnly = ComposeConfigEffects.Parse(
            """{"services":{"sock":{"image":"nginx","use_api_socket":true},"web":{"image":"nginx","volumes_from":["service:sock"]}}}""", "x");
        Assert.Contains(configOnly["web"], m => m.ApiSocket);
    }

    // ---------------------------------------------------------------- A2: privileged through << inside the hook entry

    [Fact]
    public void A_hooks_privileged_is_read_through_a_merge_inside_the_hook_entry()
    {
        var exposures = Of("x-root: &root\n  privileged: true\n  user: root\n" +
                           "services:\n  web:\n    image: nginx\n    pre_stop:\n      - <<: *root\n        command: [\"id\"]\n");
        Assert.Contains(exposures, e => e is { Kind: ExposureKind.Privileged, Key: "hook:pre_stop:0" });
    }

    // ---------------------------------------------------------------- A2: model and compose disagree on a hook

    [Fact]
    public void A_hook_key_compose_reads_differently_than_the_model_is_decided_on_its_own()
    {
        // An override merge can move a hook: compose's index is not the model's. Both are decided; compose's withheld.
        var plan = Planned(
            "services:\n  web:\n    image: nginx\n    post_start:\n      - command: [\"id\"]\n        privileged: true\n",
            """{"services":{"web":{"image":"nginx","post_start":[{"command":["true"]},{"command":["id"],"privileged":true}]}}}""");
        var hooks = plan.Exposures().Where(e => e.Kind == ExposureKind.Privileged).ToList();
        Assert.Contains(hooks, e => e is { Key: "hook:post_start:0", Withheld: false });
        Assert.Contains(hooks, e => e is { Key: "hook:post_start:1", Withheld: true });
    }

    // ---------------------------------------------------------------- A2: a privileged hook is an exposure

    [Theory]
    [InlineData("post_start")]
    [InlineData("pre_stop")]
    public void A_privileged_hook_is_keyed_by_its_kind_and_index_in_the_model_and_in_config(string hook)
    {
        var model = Of($"services:\n  web:\n    image: nginx\n    {hook}:\n      - command: [\"true\"]\n" +
                       $"      - command: [\"id\"]\n        privileged: true\n");
        var exposure = Assert.Single(model);
        Assert.Equal(ExposureKind.Privileged, exposure.Kind);
        Assert.Equal($"hook:{hook}:1", exposure.Key);
        Assert.Equal("true", exposure.Value);
        Assert.False(exposure.Withheld);
        Assert.Equal(ExposureClasses.Host, ExposureClasses.Of(exposure.Kind, exposure.Key, Prefixes));

        using var config = JsonDocument.Parse(
            $$"""{"image":"nginx","{{hook}}":[{"command":["true"]},{"command":["id"],"privileged":true}]}""");
        var fromConfig = Assert.Single(ComposeExposures.FromConfig("web", config.RootElement,
            new Dictionary<string, JsonElement>(), Prefixes));
        Assert.Equal(exposure.Identity, fromConfig.Identity);
    }

    [Fact]
    public void An_unprivileged_hook_is_no_exposure() =>
        Assert.Empty(Of("services:\n  web:\n    image: nginx\n    post_start:\n      - command: [\"true\"]\n        privileged: false\n"));
}
