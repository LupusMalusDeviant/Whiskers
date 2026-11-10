using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0013, D10–D13: after step 4 every image a compose run starts is pinned to what was inspected — its image id
/// (D10 as amended on 2026-11-10, never a repo digest) — verified on the host, written to
/// <c>.whiskers/pin.override.yml</c> and used by <c>up</c> and the fallback <c>stop</c> only. A failed pin refuses before
/// <c>up</c>; the planner never plans the pin file; the re-check flags a container that runs another image.
/// </summary>
public sealed class ComposePinTests
{
    private const string Dir = "/opt/deployments/shop";
    private const string PinPath = Dir + "/.whiskers/pin.override.yml";
    private static readonly string Nginx = "sha256:" + new string('1', 64);
    private static readonly string Built = "sha256:" + new string('2', 64);
    private static readonly string Other = "sha256:" + new string('3', 64);
    private static readonly string[] Confirm = [ExposureClasses.Public, ExposureClasses.Host];

    private readonly ProtectedResourceResolutionTests.FakeHost _host = new();
    private readonly FakeDocker _docker = new() { AllowMutations = true };

    /// <summary>What <c>up</c> creates: a container per service, running the image given here or (null) its pin.</summary>
    private readonly List<(string Id, string Service, string? RunsImage)> _upCreates = [];

    public ComposePinTests()
    {
        _host.Images["nginx"] = (Nginx, "{}");
        _host.Effect = command =>
        {
            if (!command.Contains(" up -d", StringComparison.Ordinal)) return null;
            foreach (var (id, service, runs) in _upCreates)
            {
                _docker.Add(new ContainerInfo
                {
                    Id = id, Name = $"shop-{service}-1", ServerId = "local", State = "running",
                    Labels = new() { [ComposeModel.ProjectLabel] = "shop", [ComposeModel.ServiceLabel] = service },
                });
                var image = runs ?? _host.PinnedImageId(service);
                _host.Inspect[id] = "{\"Id\":\"" + id + "\",\"Name\":\"/shop-" + service + "-1\"," +
                    (image is null ? "" : "\"Image\":\"" + image + "\",") +
                    "\"Config\":{\"Labels\":{\"com.docker.compose.project\":\"shop\",\"com.docker.compose.service\":\"" + service +
                    "\"}},\"HostConfig\":{\"NetworkMode\":\"shop_default\"}}";
            }
            return null;
        };
    }

    // ---------------------------------------------------------------- harness

    private ComposePlanner Planner() => new(_docker, _host);
    private ComposeRunner Runner() => new(_host, _docker);

    private Task<ComposePlan> PlanAsync(string yaml, string config, string? env = null)
    {
        _host.ComposeConfig = config;
        return Planner().PlanAsync(ComposeDeployment.FromContent("local", Dir, yaml) with { EnvFileContent = env });
    }

    private const string PlainYaml = "name: shop\nservices:\n  web:\n    image: nginx\n";
    private const string PlainConfig = """{"services":{"web":{"image":"nginx"}}}""";
    private const string BuiltYaml = "name: shop\nservices:\n  web:\n    image: nginx\n  app:\n    build: .\n";
    private const string BuiltConfig = """{"services":{"web":{"image":"nginx"},"app":{"build":{"context":"."}}}}""";

    private static MutationScope Decide(ComposePlan plan, bool agent)
    {
        var target = new MutationTarget("server", plan.ServerId, ServerId: plan.ServerId)
        {
            Exposures = plan.Exposures().Select(e => e.Identity).ToList(),
        };
        var principal = agent
            ? new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin)
            : new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User("admin@example.com"), "admin@example.com",
                McpPermissionLevels.Admin);
        var scope = MutationScope.Begin("compose.deploy", "op-" + Guid.NewGuid().ToString("N"), principal,
            new MutationRequest("compose.deploy", principal, MutationSurface.Mcp, target, new Dictionary<string, JsonElement>()));
        scope.SetDecision(agent, agent ? Confirm : []);
        return scope;
    }

    private Task<ComposeRunResult> RunAsync(ComposePlan plan, bool pull = true) =>
        Runner().RunAsync(plan, (c, t, k) => _host.ExecuteAsync("local", c, t, k), new ComposeRunOptions(pull, TimeSpan.FromMinutes(1)));

    private string? Up => _host.Executed.FirstOrDefault(c => c.Contains(" up -d", StringComparison.Ordinal));

    /// <summary>The pin file the run wrote, decoded.</summary>
    private string PinFile()
    {
        var write = Assert.Single(_host.Executed, c => c.Contains(ComposePins.FileName, StringComparison.Ordinal)
                                                       && c.Contains("base64 -d", StringComparison.Ordinal));
        return Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Match(write, "echo '([^']*)' \\| base64 -d").Groups[1].Value));
    }

    // ---------------------------------------------------------------- D10: where the pin comes from

    private static ComposeServicePlan Service(string name, string image, bool build = false) =>
        new(name, null) { Active = true, Image = image, Build = build };

    private static ComposeImage Image(string id) => new(id, new Dictionary<string, string>());

    [Fact]
    public void A_pulled_a_built_and_a_loaded_image_all_pin_to_their_image_id()
    {
        // D10 as amended on 2026-11-10: the id, never a repo digest (Docker 29's containerd store drops a repo digest
        // reference with a moved tag).
        var pins = ComposePins.Choose(
            [Service("web", "nginx:latest"), Service("app", "shop-app", build: true), Service("loaded", "mytool:1")],
            new Dictionary<string, ComposeImage?>
            {
                ["nginx:latest"] = Image(Nginx),
                ["shop-app"] = Image(Built),
                ["mytool:1"] = Image(Other),
            });

        Assert.Equal([Nginx, Built, Other], pins.Select(p => p.Pin));
        Assert.Equal([Nginx, Built, Other], pins.Select(p => p.ImageId));
        Assert.Equal(["nginx:latest", "shop-app", "mytool:1"], pins.Select(p => p.Reference));
    }

    [Theory]
    [InlineData("sha256:1")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha512:1111111111111111111111111111111111111111111111111111111111111111")]
    public void An_id_that_is_not_sha256_and_64_hex_refuses(string id) =>
        Assert.Throws<ComposePlanException>(() => ComposePins.Choose([Service("web", "nginx")],
            new Dictionary<string, ComposeImage?> { ["nginx"] = Image(id) }));

    [Fact]
    public void A_service_whose_image_was_not_inspected_refuses() =>
        Assert.Throws<ComposePlanException>(() => ComposePins.Choose([Service("web", "nginx")],
            new Dictionary<string, ComposeImage?> { ["nginx"] = null }));

    [Fact]
    public void The_pin_file_sets_the_pin_and_the_reference_label_and_lets_compose_interpolate_nothing()
    {
        var yaml = ComposePins.Yaml([new ComposePin("web", "registry/app:$TAG", "sha256:" + new string('4', 64), Nginx)]);

        Assert.Contains("  \"web\":\n    image: \"sha256:" + new string('4', 64) + "\"\n", yaml);
        Assert.Contains("      whiskers.image.ref: \"registry/app:$$TAG\"\n", yaml);
        var parsed = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);
        Assert.True(parsed.ContainsKey("services"));
    }

    // ---------------------------------------------------------------- D11: verify, write, use

    [Fact]
    public async Task A_run_pins_verifies_and_starts_up_with_the_pin_file_after_the_planned_files()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig, env: "A=1\n");
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, result.Error);
        Assert.Equal([Nginx], Assert.Single(_host.PinInspections));
        Assert.Contains("    image: \"" + Nginx + "\"", PinFile());
        Assert.Contains("whiskers.image.ref: \"nginx\"", PinFile());
        Assert.Contains($"-f '{Dir}/docker-compose.yml' -f '{PinPath}' --env-file '{Dir}/.env' up -d --pull never --no-build", Up);
        // Written before up, through the run's own delegate; never used by config, pull or build.
        Assert.InRange(_host.Executed.FindIndex(c => c.Contains(ComposePins.FileName, StringComparison.Ordinal)), 0,
            _host.Executed.IndexOf(Up!) - 1);
        Assert.All(_host.Executed.Where(c => c.Contains(" pull", StringComparison.Ordinal) && !c.Contains(" up -d", StringComparison.Ordinal)),
            c => Assert.DoesNotContain(ComposePins.FileName, c));
        Assert.All(_host.ConfigCommands, c => Assert.DoesNotContain(ComposePins.FileName, c));
        Assert.DoesNotContain(ComposePins.FileName, plan.UpCommand);   // only the run's pinned plan carries it
    }

    [Fact]
    public async Task A_pulled_and_a_built_service_each_get_their_pin()
    {
        var plan = await PlanAsync(BuiltYaml, BuiltConfig);
        _host.Images["shop-app"] = (Built, "{}");
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, result.Error);
        var pins = PinFile();
        Assert.Contains("  \"web\":\n    image: \"" + Nginx + "\"", pins);
        Assert.Contains("whiskers.image.ref: \"nginx\"", pins);
        Assert.Contains("  \"app\":\n    image: \"" + Built + "\"", pins);
        Assert.Contains("whiskers.image.ref: \"shop-app\"", pins);
        Assert.DoesNotContain(_host.Executed, c => c.Contains(" build --pull", StringComparison.Ordinal) && c.Contains(ComposePins.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pin_is_accepted_only_when_it_resolves_to_the_inspected_id()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        // The daemon answers another id for the pin than step 4 inspected.
        _host.PinAnswers[Nginx] = Other;
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("not the image that was inspected", result.Refusal);
        Assert.Null(Up);
    }

    public static TheoryData<string> PinFailures => new() { "inspect-fails", "missing", "different-id", "write-fails", "bad-id" };

    [Theory]
    [MemberData(nameof(PinFailures))]
    public async Task Every_failed_pin_refuses_before_up_and_puts_the_files_and_tags_back(string failure)
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        // The pull moves the tag, so a refusal has a tag to put back.
        var pulled = "sha256:" + new string('5', 64);
        var effect = _host.Effect;
        _host.Effect = command =>
        {
            if (command.Contains("docker compose", StringComparison.Ordinal) && command.Contains(" pull", StringComparison.Ordinal)
                && !command.Contains(" up -d", StringComparison.Ordinal))
                _host.Images["nginx"] = (failure == "bad-id" ? "sha256:5" : pulled, "{}");
            if (failure == "write-fails" && command.Contains(ComposePins.FileName, StringComparison.Ordinal))
                return new CommandResult { ExitCode = 1, Error = "disk full" };
            return effect?.Invoke(command);
        };
        switch (failure)
        {
            case "inspect-fails": _host.PinInspectFails = true; break;
            case "missing": _host.PinAnswers[pulled] = null; break;
            case "different-id": _host.PinAnswers[pulled] = Other; break;
        }
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.NotNull(result.Refusal);
        Assert.Contains("nothing was started", result.Refusal);
        Assert.Null(Up);
        Assert.Contains(_host.Executed, c => c.Contains("cp -p", StringComparison.Ordinal) && c.Contains("rm -f", StringComparison.Ordinal));
        Assert.Contains($"docker tag '{Nginx}' 'nginx'", _host.Executed);
    }

    [Fact]
    public async Task The_pin_file_is_replaced_in_one_step_so_a_failed_write_keeps_the_previous_one()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        var script = plan.PinScript("services: {}\n");

        Assert.Contains($"> '{PinPath}.tmp' && mv -f '{PinPath}.tmp' '{PinPath}'", script);
        Assert.Contains($"rm -f '{PinPath}.tmp'; false;", script);
        Assert.StartsWith($"mkdir -p '{Dir}/.whiskers' && ", script);
    }

    [Fact]
    public async Task The_fallback_stop_uses_the_pin_file()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        var effect = _host.Effect;
        _host.Effect = command =>
        {
            if (command.Contains(" up -d", StringComparison.Ordinal)) _docker.ListContainersThrows = true;
            return effect?.Invoke(command);
        };
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        var stop = Assert.Single(_host.Executed, c => c.EndsWith(" stop 2>&1", StringComparison.Ordinal));
        Assert.Contains($"-f '{PinPath}' stop", stop);
    }

    // ---------------------------------------------------------------- D12: the planner never plans the pin file

    [Fact]
    public async Task The_planner_drops_the_pin_file_from_the_recorded_config_files()
    {
        _host.Files[$"{Dir}/docker-compose.yml"] = PlainYaml;
        _host.ComposeConfig = PlainConfig;
        var container = new ContainerInfo
        {
            Id = "c1", Name = "shop-web-1", ServerId = "local",
            Labels = new()
            {
                [ComposeModel.ProjectLabel] = "shop",
                [ComposeModel.ServiceLabel] = "web",
                ["com.docker.compose.project.working_dir"] = Dir,
                ["com.docker.compose.project.config_files"] = $"{Dir}/docker-compose.yml,{PinPath}",
            },
        };

        var plan = await Planner().PlanEnvChangeAsync(container, "local", _ => "A=2\n");

        Assert.Equal([$"{Dir}/docker-compose.yml"], plan.Files);
        Assert.DoesNotContain(ComposePins.FileName, plan.PullCommand);
        Assert.All(_host.ConfigCommands, c => Assert.DoesNotContain(ComposePins.FileName, c));
    }

    [Fact]
    public async Task The_planner_drops_the_pin_file_named_in_compose_file()
    {
        _host.Files[$"{Dir}/docker-compose.yml"] = PlainYaml;
        _host.Files[$"{Dir}/.env"] = $"COMPOSE_FILE=docker-compose.yml:.whiskers/{ComposePins.FileName}\n";
        _host.ComposeConfig = PlainConfig;

        var plan = await Planner().PlanAsync(ComposeDeployment.FromDirectory("local", Dir));

        Assert.Equal([$"{Dir}/docker-compose.yml"], plan.Files);
    }

    [Fact]
    public async Task A_deployment_that_writes_the_pin_file_is_refused()
    {
        _host.ComposeConfig = PlainConfig;
        var ex = await Assert.ThrowsAsync<ComposePlanException>(() => Planner().PlanAsync(
            new ComposeDeployment("local", [new ComposeFileInput($"{Dir}/docker-compose.yml", PlainYaml), new ComposeFileInput(PinPath, "services: {}\n")])
            { ProjectDirectory = Dir }));
        Assert.Contains("pin file", ex.Message);
    }

    [Theory]
    [InlineData("/opt/x/.whiskers/pin.override.yml", true)]
    [InlineData(".whiskers/pin.override.yml", true)]
    [InlineData("/opt/x/pin.override.yml", false)]
    [InlineData("/opt/x/.whiskers/pin.override.yml.tmp", false)]
    [InlineData("/opt/x/my.whiskers/pin.override.yml", false)]
    public void Only_the_pin_file_is_recognised(string path, bool pin) => Assert.Equal(pin, ComposePins.IsPinFile(path));

    [Fact]
    public void A_git_deploy_checkout_excludes_the_pin_directory()
    {
        var command = Whiskers.Services.GitDeploy.GitDeployCommands.CloneOrUpdateCommand("abc123def456", "https://x/y.git", "main", withToken: false);
        Assert.EndsWith("&& cd /opt/whiskers-git/abc123def456/repo && { grep -qxF '.whiskers/' .git/info/exclude 2>/dev/null || " +
                        "{ mkdir -p .git/info && echo '.whiskers/' >> .git/info/exclude; }; }", command);
    }

    // ---------------------------------------------------------------- D13: the re-check compares the image id

    [Fact]
    public async Task A_container_that_runs_the_inspected_image_passes_the_re_check()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("web-new", "web", null));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Violations);
        Assert.Empty(_docker.Actions);
    }

    [Fact]
    public async Task A_container_that_runs_another_image_is_public_and_stopped_under_the_agent_rule_set()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("web-new", "web", Other));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, v => v.Contains("runs an image other than the one inspected", StringComparison.Ordinal)
                                                && v.Contains("class 'public'", StringComparison.Ordinal));
        Assert.Equal([("disable_restart", "web-new"), ("stop", "web-new")], _docker.Actions.Select(a => (a.Op, a.ContainerId)));
    }

    [Fact]
    public async Task A_container_without_a_readable_image_id_counts_as_another_image()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("web-new", "web", null));
        using var _ = Decide(plan, agent: true);
        var effect = _host.Effect;
        _host.Effect = command =>
        {
            var result = effect?.Invoke(command);
            if (command.Contains(" up -d", StringComparison.Ordinal))
                _host.Inspect["web-new"] = _host.Inspect["web-new"].Replace($"\"Image\":\"{Nginx}\",", "", StringComparison.Ordinal);
            return result;
        };

        var run = await RunAsync(plan);

        Assert.Contains(run.Violations, v => v.Contains("runs an image other than the one inspected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task For_persons_another_image_is_reported_and_keeps_running()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("web-new", "web", Other));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, v => v.Contains("runs an image other than the one inspected", StringComparison.Ordinal));
        Assert.Empty(_docker.Actions);
    }

    [Fact]
    public async Task A_re_tag_after_the_inspection_changes_nothing_up_starts_the_pin()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("web-new", "web", null));
        var effect = _host.Effect;
        _host.Effect = command =>
        {
            // Between the pin file and up, someone moves the tag to another image.
            if (command.Contains(ComposePins.FileName, StringComparison.Ordinal) && command.Contains("base64 -d", StringComparison.Ordinal))
            {
                _host.Images["nginx"] = (Other, "{}");
                _host.Images["nginx-old"] = (Nginx, "{}");
            }
            return effect?.Invoke(command);
        };
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, result.Error);
        Assert.Contains("\"Image\":\"" + Nginx + "\"", _host.Inspect["web-new"]);
        Assert.DoesNotContain(_host.Executed, c => c.StartsWith("docker tag", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pins_are_noted_by_service_and_image_id_only()
    {
        // The reference can come from an .env value: the note names the service and the id's first 12 digits.
        Assert.Equal("pinned images: web=" + new string('1', 12) + ", app=" + new string('2', 12), ComposePins.Note(
        [
            new ComposePin("web", "registry.example.org/secret-name:1", Nginx, Nginx),
            new ComposePin("app", "shop-app", Built, Built),
        ]));
    }
}
