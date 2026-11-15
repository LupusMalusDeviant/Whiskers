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

    /// <summary>What a plan decides for one compose file: the model's reading united with what
    /// <c>docker compose config --format json</c> printed for it (<paramref name="config"/>: the output of Docker
    /// Compose v5.6.1 for exactly <paramref name="yaml"/>), the way <see cref="ComposePlanner"/> unites them.</summary>
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

        Assert.True(plan.Mounts().Any(m => m.EndsWith("docker.sock", StringComparison.Ordinal)) || plan.Exposures().Count > 0,
            $"use_api_socket hands the service the Docker socket, but the plan decides {Decided(plan)}");
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
    public void Socket_binds_are_read_from_a_containers_mounts()
    {
        using var doc = JsonDocument.Parse("""
            {"Mounts":[{"Type":"bind","Source":"/run/docker.sock","Destination":"/var/run/docker.sock","RW":true},
                       {"Type":"bind","Source":"/srv/app","Destination":"/app","RW":true},
                       {"Type":"volume","Name":"v","Source":"/var/lib/docker/volumes/v/_data"}]}
            """);
        Assert.Equal(["/run/docker.sock"], ComposeExposures.SocketBinds(doc.RootElement));
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
