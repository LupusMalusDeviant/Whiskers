using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Server;
using Whiskers.Utils;
using static Whiskers.Tests.AutomationMutationRunnerTests;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0006: the nested exposure decision on every path, the run order of <see cref="ComposeRunner"/> (write, pull,
/// build, inspect the images, up) and the re-check of what a run created — stopping under the agent rule set, reporting
/// for persons — with no <c>.env</c> value in any sink.
/// </summary>
public sealed partial class ComposeImpactExposureTests
{
    private const string Admin = "admin@example.com";
    private const string Dir = "/opt/deployments/shop";
    private static readonly string[] Confirm = [ExposureClasses.Public, ExposureClasses.Host];

    private readonly ProtectedResourceResolutionTests.FakeHost _host = new();
    private readonly FakeDocker _docker = new() { AllowMutations = true };
    private readonly Notifications _notifications = new();
    private readonly FakeServerConfig _servers = new(new ServerConfig { Id = "local", Name = "Local", IsDefault = true });

    /// <summary>What <c>up</c> creates: one container per entry, with its inspect document.</summary>
    private readonly List<(string Id, string Service, string HostConfig, string Labels)> _upCreates = [];

    /// <summary>What a pull / a build leaves behind: the image reference with its id and labels.</summary>
    private readonly Dictionary<string, (string Id, string Labels)> _pulls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Id, string Labels)> _builds = new(StringComparer.Ordinal);
    private bool _pullFails;

    public ComposeImpactExposureTests()
    {
        _host.Images["nginx"] = ("sha256:1111111111111111111111111111111111111111111111111111111111111111", "{}");
        _host.Effect = command =>
        {
            if (command.Contains(" pull", StringComparison.Ordinal) && command.Contains("docker compose", StringComparison.Ordinal))
            {
                if (_pullFails) return new CommandResult { ExitCode = 1, Error = "pull failed" };
                foreach (var (image, result) in _pulls) _host.Images[image] = result;
            }
            if (command.Contains(" build", StringComparison.Ordinal) && command.Contains("docker compose", StringComparison.Ordinal))
                foreach (var (image, result) in _builds) _host.Images[image] = result;
            if (command.Contains(" up -d", StringComparison.Ordinal))
                foreach (var (id, service, hostConfig, labels) in _upCreates)
                {
                    _docker.Add(new ContainerInfo
                    {
                        Id = id, Name = $"shop-{service}-1", ServerId = "local", State = "running",
                        Labels = new() { [ComposeModel.ProjectLabel] = "shop", [ComposeModel.ServiceLabel] = service },
                    });
                    _host.Inspect[id] = "{\"Id\":\"" + id + "\",\"Name\":\"/shop-" + service + "-1\"," +
                        Image(service) +
                        "\"Config\":{\"Labels\":{\"com.docker.compose.project\":\"shop\",\"com.docker.compose.service\":\"" +
                        service + "\"" + labels + "}},\"HostConfig\":" + hostConfig + "}";
                }
            return null;
        };
    }

    // ---------------------------------------------------------------- harness

    /// <summary>Contract 0013: a created container runs the image its service was pinned to (the inspect field <c>Image</c>).</summary>
    private string Image(string service) =>
        _host.PinnedImageId(service) is { } id ? "\"Image\":\"" + id + "\"," : "";


    private ComposePlanner Planner() => new(_docker, _host);
    private ComposeRunner Runner() => new(_host, _docker, _notifications);

    private Task<ComposePlan> PlanAsync(string yaml, string config, string? env = null)
    {
        _host.ComposeConfig = config;
        return Planner().PlanAsync(ComposeDeployment.FromContent("local", Dir, yaml) with { EnvFileContent = env });
    }

    private const string FunnelYaml = "name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n      docktail.funnel.enable: \"true\"\n";
    private const string FunnelConfig = """{"services":{"web":{"image":"nginx","labels":{"docktail.funnel.enable":"true"}}}}""";
    private const string PlainYaml = "name: shop\nservices:\n  web:\n    image: nginx\n";
    private const string PlainConfig = """{"services":{"web":{"image":"nginx"}}}""";

    /// <summary>A decision of the plan as the gateway makes it: its exposures and shared containers decided, under the
    /// agent rule set or a person's.</summary>
    private static MutationScope Decide(ComposePlan plan, bool agent, string capability = "compose.deploy")
    {
        var exposures = plan.Exposures().Select(e => e.Identity).ToList();
        var target = new MutationTarget("server", plan.ServerId, ServerId: plan.ServerId)
        {
            Related = plan.SharedIdentities().ToList(),
            Exposures = exposures,
        };
        var principal = agent
            ? new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin)
            : new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(Admin), Admin, McpPermissionLevels.Admin);
        var scope = MutationScope.Begin(capability, "op-" + Guid.NewGuid().ToString("N"), principal,
            new MutationRequest(capability, principal, MutationSurface.Mcp, target, new Dictionary<string, JsonElement>()));
        scope.SetDecision(agent, agent ? Confirm : []);
        return scope;
    }

    private Task<ComposeRunResult> RunAsync(ComposePlan plan, bool pull = true) =>
        Runner().RunAsync(plan, (c, t, k) => _host.ExecuteAsync("local", c, t, k), new ComposeRunOptions(pull, TimeSpan.FromMinutes(1)));

    private int IndexOf(string fragment) => _host.Executed.FindIndex(c => c.Contains(fragment, StringComparison.Ordinal));

    // ---------------------------------------------------------------- run order

    [Fact]
    public async Task A_run_writes_pulls_inspects_the_images_and_then_starts_without_pulling_or_building()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, result.Error);
        var pull = IndexOf("pull --ignore-buildable");
        var up = IndexOf(" up -d --pull never --no-build");
        Assert.InRange(pull, 0, up - 1);
        Assert.DoesNotContain(_host.Executed, c => c.Contains(" build", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_redeploy_with_a_build_service_rebuilds_with_pull()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    build: .\n",
            """{"services":{"web":{"build":{"context":"/opt/deployments/shop"}}}}""");
        _host.Images["shop-web"] = ("sha256:2222222222222222222222222222222222222222222222222222222222222222", "{}");
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, result.Error);
        Assert.InRange(IndexOf("build --pull"), IndexOf("pull --ignore-buildable") + 1, IndexOf(" up -d") - 1);
    }

    [Fact]
    public async Task An_env_path_with_its_images_present_runs_no_pull_and_no_build()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    build: .\n  db:\n    image: nginx\n",
            """{"services":{"web":{"build":{"context":"."}},"db":{"image":"nginx"}}}""");
        _host.Images["shop-web"] = ("sha256:2222222222222222222222222222222222222222222222222222222222222222", "{}");
        using var _ = Decide(plan, agent: true, "container.env_set");

        var result = await RunAsync(plan, pull: false);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(_host.Executed, c => c.Contains(" pull", StringComparison.Ordinal) && c.Contains("compose", StringComparison.Ordinal) && !c.Contains("--pull never", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Executed, c => c.Contains(" build", StringComparison.Ordinal) && !c.Contains("--no-build", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_env_path_builds_a_missing_image_without_pull_and_pulls_only_what_is_missing()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    build: .\n  db:\n    image: redis\n",
            """{"services":{"web":{"build":{"context":"."}},"db":{"image":"redis"}}}""");
        _builds["shop-web"] = ("sha256:2222222222222222222222222222222222222222222222222222222222222222", "{}");
        _pulls["redis"] = ("sha256:3333333333333333333333333333333333333333333333333333333333333333", "{}");
        using var _ = Decide(plan, agent: true, "container.env_set");

        var result = await RunAsync(plan, pull: false);

        Assert.True(result.Success, result.Error);
        var build = Assert.Single(_host.Executed, c => c.Contains(" build ", StringComparison.Ordinal));
        Assert.DoesNotContain("--pull", build);
        Assert.Contains("'web'", build);
        var pull = Assert.Single(_host.Executed, c => c.Contains(" pull ", StringComparison.Ordinal));
        Assert.Contains("'db'", pull);
        Assert.DoesNotContain("--ignore-buildable", pull);
        Assert.Contains(_host.Executed, c => c.Contains(" up -d --pull never --no-build", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_image_that_is_still_missing_refuses_the_run_before_up()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: redis\n", """{"services":{"web":{"image":"redis"}}}""");
        using var _ = Decide(plan, agent: true, "container.env_set");

        var result = await RunAsync(plan, pull: false);

        Assert.False(result.Success);
        Assert.NotNull(result.Refusal);
        Assert.Equal(-1, IndexOf(" up -d"));
    }

    // ---------------------------------------------------------------- image labels (step 4)

    [Fact]
    public async Task A_pull_that_brings_a_funnel_label_is_refused_before_up_and_the_tag_is_put_back()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _pulls["nginx"] = ("sha256:9999999999999999999999999999999999999999999999999999999999999999", """{"docktail.funnel.enable":"true"}""");
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("docktail.funnel.enable", result.Refusal);
        Assert.Equal(-1, IndexOf(" up -d"));
        Assert.Contains("docker tag 'sha256:1111111111111111111111111111111111111111111111111111111111111111' 'nginx'", _host.Executed);
    }

    [Fact]
    public async Task A_build_whose_dockerfile_sets_a_funnel_label_is_refused_under_the_agent_rule_set_before_up()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    build:\n      context: .\n      dockerfile_inline: |\n" +
                                   "        FROM nginx\n        LABEL docktail.funnel.enable=true\n",
            """{"services":{"web":{"build":{"context":".","dockerfile_inline":"FROM nginx"}}}}""");
        _builds["shop-web"] = ("sha256:7777777777777777777777777777777777777777777777777777777777777777", """{"docktail.funnel.enable":"true"}""");
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.NotNull(result.Refusal);
        Assert.Equal(-1, IndexOf(" up -d"));
        Assert.Contains("docker image rm --no-prune 'shop-web'", _host.Executed);   // the tag was absent before
    }

    [Fact]
    public async Task For_persons_an_image_label_is_noted_and_announced_not_a_violation()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _pulls["nginx"] = ("sha256:9999999999999999999999999999999999999999999999999999999999999999", """{"docktail.funnel.enable":"true"}""");
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Violations);
        var sent = Assert.Single(_notifications.Sent);
        Assert.Equal(NotificationEventTypes.ExposureChange, sent.EventType);
        Assert.Contains("docktail.funnel.enable", sent.ImageInfo);
    }

    [Fact]
    public async Task A_concurrent_re_tag_by_another_run_is_not_undone()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _pulls["nginx"] = ("sha256:9999999999999999999999999999999999999999999999999999999999999999", """{"docktail.funnel.enable":"true"}""");
        var inspections = 0;
        _host.ImageInspected = () =>
        {
            // After the inspection of step 4, another run moves the tag on.
            if (++inspections == 2) _host.Images["nginx"] = ("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "{}");
        };
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.NotNull(result.Refusal);
        Assert.DoesNotContain(_host.Executed, c => c.StartsWith("docker tag", StringComparison.Ordinal));
        Assert.Equal("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", _host.Images["nginx"].Id);
    }

    [Fact]
    public async Task A_pull_failure_that_moved_no_tag_reports_no_violation()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _pullFails = true;
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Empty(result.Violations);
        Assert.DoesNotContain(_host.Executed, c => c.StartsWith("docker tag", StringComparison.Ordinal));
        Assert.Empty(_notifications.Sent);
    }

    [Fact]
    public async Task A_refused_run_puts_the_written_files_back()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _pulls["nginx"] = ("sha256:9999999999999999999999999999999999999999999999999999999999999999", """{"docktail.funnel.enable":"true"}""");
        using var _ = Decide(plan, agent: true);

        await RunAsync(plan);

        Assert.Contains(_host.Executed, c => c.Contains("cp -p '/tmp/whiskers-compose-", StringComparison.Ordinal)
                                             && c.Contains("rm -rf", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- re-check after up

    private const string NoHost = """{"NetworkMode":"shop_default","PortBindings":{}}""";

    [Fact]
    public async Task A_decided_exposure_passes_the_re_check()
    {
        var plan = await PlanAsync(FunnelYaml, FunnelConfig);
        _upCreates.Add(("new-web", "web", NoHost, ""","docktail.funnel.enable":"true" """));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, string.Join("; ", result.Violations));
        Assert.Empty(_docker.Actions);
    }

    [Fact]
    public async Task Under_the_agent_rule_set_an_undecided_exposure_is_stopped_and_stays_stopped()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", """{"NetworkMode":"host"}""", ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, v => v.Contains("host network", StringComparison.Ordinal));
        Assert.Equal([("disable_restart", "new-web"), ("stop", "new-web")], _docker.Actions.Select(a => (a.Op, a.ContainerId)));
        Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureViolation);
    }

    [Fact]
    public async Task For_persons_an_undecided_exposure_keeps_running_and_is_reported()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", """{"NetworkMode":"host"}""", ""));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Violations);
        Assert.Empty(_docker.Actions);
        Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureViolation);
    }

    [Fact]
    public async Task The_same_key_with_another_value_is_a_violation()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    ports: [\"8080:80\"]\n",
            """{"services":{"web":{"image":"nginx","ports":[{"target":80,"published":"8080"}]}}}""");
        _upCreates.Add(("new-web", "web", """{"PortBindings":{"80/tcp":[{"HostIp":"","HostPort":"9090"}]}}""", ""));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("80/tcp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_stop_is_retried_then_reported_as_still_running()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", """{"Privileged":true}""", ""));
        _docker.StopFailures = 2;
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("container still running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_container_that_cannot_be_checked_counts_as_public_and_is_stopped()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", NoHost, ""));
        _host.Effect = WithoutInspect(_host.Effect!);
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("could not be checked", StringComparison.Ordinal));
        Assert.Contains(_docker.Actions, a => a is { Op: "stop", ContainerId: "new-web" });
    }

    private Func<string, CommandResult?> WithoutInspect(Func<string, CommandResult?> effect) => command =>
    {
        var result = effect(command);
        _host.Inspect.Clear();
        return result;
    };

    [Fact]
    public async Task A_run_whose_containers_cannot_be_listed_falls_back_to_stopping_the_project()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        using var _ = Decide(plan, agent: true);
        var inner = _host.Effect!;
        _host.Effect = command =>
        {
            if (command.Contains(" up -d", StringComparison.Ordinal)) _docker.ListContainersThrows = true;
            return inner(command);
        };

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("could not be listed", StringComparison.Ordinal));
        Assert.Contains(_host.Executed, c => c.Contains(" stop", StringComparison.Ordinal) && c.Contains("-p 'shop'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_namespace_shared_at_run_time_with_an_undecided_container_is_a_violation()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", """{"PidMode":"container:helper-id"}""", ""));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("pid namespace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_service_namespace_this_run_recreates_passes_the_re_check_under_both_rule_sets()
    {
        foreach (var agent in new[] { true, false })
        {
            _docker.Actions.Clear();
            _upCreates.Clear();
            var plan = await PlanAsync("name: shop\nservices:\n  vpn:\n    image: nginx\n  web:\n    image: nginx\n    network_mode: \"service:vpn\"\n",
                """{"services":{"vpn":{"image":"nginx"},"web":{"image":"nginx","network_mode":"service:vpn"}}}""");
            var vpn = $"vpn-{agent}";
            _upCreates.Add((vpn, "vpn", NoHost, ""));
            _upCreates.Add(($"web-{agent}", "web", $$"""{"NetworkMode":"container:{{vpn}}"}""", ""));
            using var _ = Decide(plan, agent);

            var result = await RunAsync(plan);

            Assert.True(result.Success, string.Join("; ", result.Violations));
            Assert.Empty(_docker.Actions);
        }
    }

    // ---------------------------------------------------------------- the stop set

    [Fact]
    public void Only_the_two_stop_operations_reach_a_container_the_run_created()
    {
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin);
        var target = new MutationTarget("server", "local", ServerId: "local");
        using var deploy = MutationScope.Begin("compose.deploy", "op-1", principal,
            new MutationRequest("compose.deploy", principal, MutationSurface.Mcp, target, new Dictionary<string, JsonElement>()));
        deploy.AddCreatedByRun("local", ["new-web"]);
        string? Canonical(string s) => s;

        Assert.True(MutationScope.AuthorizesTarget("container.stop", "new-web", true, "local", "local", Canonical));
        Assert.True(MutationScope.AuthorizesTarget("container.disable_restart", "new-web", true, "local", "local", Canonical));
        Assert.False(MutationScope.AuthorizesTarget("container.stop", "other-project-web", true, "local", "local", Canonical));
        Assert.False(MutationScope.AuthorizesTarget("container.remove", "new-web", true, "local", "local", Canonical));
        Assert.False(MutationScope.AuthorizesTarget("container.stop", "new-web", true, "edge", "edge", Canonical));
        // The stop set decides nothing else: Covers never reads it.
        Assert.False(deploy.Covers("local", "new-web"));
    }

    // ---------------------------------------------------------------- decisions on every path

    private static GuardrailPolicy AdminPolicy()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        return policy;
    }

    private MutationGateway Gateway(GuardrailPolicy policy, IMutationAuditSink? audit = null)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        return new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            audit ?? new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            targets: new MutationTargetResolver(new TestServices(_docker, _servers, Planner())),
            approvals: new ApprovalGate(new ApprovalStore(), store));
    }

    private static Dictionary<string, JsonElement> Args(object arguments) =>
        JsonSerializer.SerializeToElement(arguments).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private async Task<(MutationOutcome<int> Outcome, MutationTarget? Seen)> McpAsync(object arguments, GuardrailPolicy? policy = null,
        string capability = "compose.deploy")
    {
        var catalog = new CapabilityCatalog();
        var args = Args(arguments);
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin);
        MutationTarget? seen = null;
        var outcome = await Gateway(policy ?? AdminPolicy()).ExecuteAsync(
            new MutationRequest(capability, principal, MutationSurface.Mcp,
                MutationTargets.FromArguments(catalog.Find(capability)!, args), args),
            (ctx, _) => { seen = ctx.Request.Target; return Task.FromResult(0); });
        return (outcome, seen);
    }

    [Fact]
    public async Task Funnel_over_mcp_needs_a_confirmation_even_when_admin_is_autonomous()
    {
        _host.ComposeConfig = FunnelConfig;

        var (outcome, _) = await McpAsync(new { serverId = "local", projectName = "shop", composeContent = FunnelYaml });

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.Contains("Tailscale Funnel", outcome.Reason);
    }

    [Fact]
    public async Task A_deployment_without_exposures_runs_as_before()
    {
        _host.ComposeConfig = PlainConfig;

        var (outcome, _) = await McpAsync(new { serverId = "local", projectName = "shop", composeContent = PlainYaml });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
    }

    [Fact]
    public async Task A_local_image_label_found_while_planning_needs_a_confirmation_over_mcp()
    {
        _host.Images["nginx"] = ("sha256:1111111111111111111111111111111111111111111111111111111111111111", """{"traefik.http.routers.web.rule":"Host(`shop.example.org`)"}""");
        _host.ComposeConfig = PlainConfig;

        var (outcome, _) = await McpAsync(new { serverId = "local", projectName = "shop", composeContent = PlainYaml });

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
    }

    [Fact]
    public async Task A_shared_namespace_with_whiskers_needs_a_decision_and_the_target_names_it()
    {
        _docker.Add(new ContainerInfo { Id = "whiskers-id", Name = "whiskers", ServerId = "local" });
        _host.Inspect["whiskers-id"] = """{"Id":"whiskers-id","Name":"/whiskers","Config":{"Labels":{}},"HostConfig":{"NetworkMode":"bridge"}}""";
        _host.ComposeConfig = """{"services":{"web":{"image":"nginx","network_mode":"container:whiskers"}}}""";

        var (outcome, seen) = await McpAsync(new
        {
            serverId = "local", projectName = "shop",
            composeContent = "name: shop\nservices:\n  web:\n    image: nginx\n    network_mode: \"container:whiskers\"\n",
        });

        // Whiskers' own container is protected: the agent may not join its namespace.
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("whiskers", outcome.Reason);
        Assert.Null(seen);
    }

    [Fact]
    public async Task A_shared_namespace_with_whiskers_is_confirmed_by_an_interactive_admin_not_refused()
    {
        _docker.Add(new ContainerInfo { Id = "whiskers-id", Name = "whiskers", ServerId = "local" });
        _host.Inspect["whiskers-id"] = """{"Id":"whiskers-id","Name":"/whiskers","Config":{"Labels":{}},"HostConfig":{"NetworkMode":"bridge"}}""";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    network_mode: \"container:whiskers\"\n",
            """{"services":{"web":{"image":"nginx","network_mode":"container:whiskers"}}}""");

        // The plan decides the container it shares a namespace with, by name (Amendment 4).
        Assert.True(ComposeImpact.NeedsExposureDecision(plan));
        var args = Args(ComposeImpact.ExposureArguments(plan));
        Assert.Contains("whiskers", args["related"].GetString());
        var catalog = new CapabilityCatalog();
        var person = new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(Admin), Admin, McpPermissionLevels.Admin);
        var outcome = await Gateway(AdminPolicy()).ExecuteAsync(
            new MutationRequest("compose.deploy", person, MutationSurface.Ui,
                MutationTargets.FromArguments(catalog.Find("compose.deploy")!, args), args),
            (_, _) => Task.FromResult(0));
        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
    }

    [Fact]
    public async Task The_target_binds_the_exposures_so_a_changed_env_needs_a_new_approval()
    {
        var catalog = new CapabilityCatalog();
        var capability = catalog.Find("compose.deploy")!;
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin);
        var args = Args(new { serverId = "local" });
        var target = new MutationTarget("server", "local", ServerId: "local");
        MutationRequest With(string port) => new("compose.deploy", principal, MutationSurface.Mcp,
            target with { Exposures = [ComposeExposures.Identity("web", ExposureKind.PublishedPort, "80/tcp", $"0.0.0.0:{port}")] }, args);

        var before = ApprovalBinding.Of(With("8080"), capability, "v1");
        var after = ApprovalBinding.Of(With("9090"), capability, "v1");

        Assert.NotEqual(before, after);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task No_env_value_reaches_the_audit_the_approval_card_or_a_notification()
    {
        const string secret = "s3cr3t-dns-token";
        const string key = "caddy.tls.dns";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n      ${LABEL}: \"${TOKEN}\"\n" +
                                   "      caddy: shop.example.org\n",
            """{"services":{"web":{"image":"nginx","labels":{"caddy.tls.dns":"s3cr3t-dns-token","caddy":"shop.example.org"}}}}""",
            env: $"LABEL={key}\nTOKEN={secret}\n");

        var args = Args(ComposeImpact.ExposureArguments(plan));
        var audit = SinkRedaction.Arguments(args);
        var card = SinkRedaction.Json(JsonSerializer.Serialize(SinkRedaction.WithoutPayloads(args)));

        foreach (var sink in new[] { audit, card })
        {
            Assert.DoesNotContain(secret, sink);
            Assert.DoesNotContain(key, sink);
        }
        Assert.DoesNotContain(secret, args["exposure"].GetString());

        // The run's notification for a person names service, kind and key — and a key from a variable not even that.
        _upCreates.Add(("new-web", "web", NoHost, $$""","caddy.tls.dns":"{{secret}}","caddy":"shop.example.org" """));
        using (Decide(plan, agent: false)) await RunAsync(plan);
        Assert.All(_notifications.Sent, n => Assert.DoesNotContain(secret, n.ImageInfo));
        Assert.All(_notifications.Sent, n => Assert.DoesNotContain(key, n.ImageInfo));
    }

    [Fact]
    public async Task A_literal_secret_in_a_label_value_is_withheld_from_the_audited_exposure_argument()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n" +
                                   "      traefik.http.routers.api.rule: \"Host(`api.x`) && Header(`X-Api-Key`, `s3cr3t`)\"\n" +
                                   "      caddy.tls.dns: \"cloudflare literal-token\"\n",
            """{"services":{"web":{"image":"nginx"}}}""");

        var args = Args(ComposeImpact.ExposureArguments(plan));

        Assert.DoesNotContain("s3cr3t", args["exposure"].GetString());
        Assert.DoesNotContain("literal-token", args["exposure"].GetString());
        Assert.DoesNotContain("s3cr3t", SinkRedaction.Arguments(args));
        Assert.DoesNotContain("literal-token", SinkRedaction.Arguments(args));
    }

    [Fact]
    public async Task Every_path_decides_the_same_nested_exposure_arguments()
    {
        var plan = await PlanAsync(FunnelYaml, FunnelConfig);
        var seen = new List<string>();

        // The UI (and the page's .env save), MCP after planning, the webhook and git deploy (the automation gate) all
        // decide ComposeImpact.ExposureArguments on compose.deploy.
        var ui = new UiMutationRunner(Gateway(AdminPolicy()), new CapabilityCatalog(), new Person());
        await ui.RunExposuresAsync(plan, () => { seen.Add(MutationScope.Current!.CapabilityId); return Task.FromResult(true); });

        Assert.Equal(["compose.deploy"], seen);
        Assert.True(ComposeImpact.NeedsExposureDecision(plan));
        var args = Args(ComposeImpact.ExposureArguments(plan));
        Assert.Equal("public", args["exposureClasses"].GetString());
        Assert.Contains("docktail.funnel.enable", args["exposure"].GetString());
    }

    [Fact]
    public async Task A_person_owned_automation_with_funnel_runs_and_an_api_key_owned_one_is_refused()
    {
        var plan = await PlanAsync(FunnelYaml, FunnelConfig);
        var roles = new Roles();
        var whitelist = new Whitelist();
        var keys = new Keys();
        roles.Map[Admin] = AppRole.Admin;
        whitelist.Allowed.Add(Admin);
        keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = McpPermissionLevels.Admin, Enabled = true });
        var automation = new AutomationMutationRunner(Gateway(AdminPolicy()), new CapabilityCatalog(),
            new AutomationOwnerResolver(roles, whitelist, keys, new Accounts()),
            new StaticOptionsMonitor<AutomationOptions>(new AutomationOptions { RequireOwner = true }));
        var ran = 0;

        await automation.GateFor(new AutomationActor("webhook:w1", "deploy", PrincipalIds.User(Admin), PrincipalIds.User(Admin)), "compose.deploy")(plan,
            () => { ran++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            automation.GateFor(new AutomationActor("webhook:w2", "deploy", PrincipalIds.Key("k1"), PrincipalIds.Key("k1")), "compose.deploy")(plan,
                () => { ran++; return Task.CompletedTask; }));

        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task The_agents_env_change_that_turns_on_funnel_through_a_variable_needs_a_confirmation()
    {
        _docker.Add(new ContainerInfo
        {
            Id = "shop-web-id", Name = "shop-web-1", ServerId = "local", State = "running",
            Labels = new()
            {
                [ComposeModel.ProjectLabel] = "shop", [ComposeModel.ServiceLabel] = "web",
                ["com.docker.compose.project.working_dir"] = "/srv/shop",
                ["com.docker.compose.project.config_files"] = "/srv/shop/compose.yaml",
            },
        });
        _host.Files["/srv/shop/compose.yaml"] =
            "name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n      docktail.funnel.enable: \"${FUNNEL:-false}\"\n";
        _host.ComposeConfig = FunnelConfig;

        var (outcome, seen) = await McpAsync(new { containerId = "shop-web-1", envVars = "FUNNEL=true", serverId = "local" },
            capability: "container.env_set");

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        // Only the value came from the .env: the reason names the key (review m1), never the value.
        Assert.Contains("docktail.funnel.enable", outcome.Reason);
        Assert.DoesNotContain("=true", outcome.Reason);
        Assert.Null(seen);
    }

    // ---------------------------------------------------------------- planning

    [Theory]
    [InlineData("name: shop\nservices:\n  web:\n    image: nginx\n    extends:\n      file: 1.yml\n      service: base\n", null)]
    [InlineData("name: shop\nservices:\n  web:\n    image: nginx\n    label_file: ${LF}\n", "LF=labels.env\n")]
    [InlineData("name: shop\nservices:\n  web:\n    image: nginx\n", "COMPOSE_PROFILES=${UNSET}\n")]
    public async Task A_plan_the_model_cannot_take_is_refused_before_compose_config_runs(string yaml, string? env)
    {
        await Assert.ThrowsAsync<ComposePlanException>(() => PlanAsync(yaml, PlainConfig, env));

        Assert.Empty(_host.ConfigCommands);
    }

    [Fact]
    public async Task A_service_only_compose_knows_is_refused() =>
        await Assert.ThrowsAsync<ComposePlanException>(() => PlanAsync(PlainYaml,
            """{"services":{"web":{"image":"nginx"},"ghost":{"image":"nginx","privileged":true}}}"""));

    [Fact]
    public async Task A_service_in_an_inactive_profile_is_not_inspected_and_does_not_refuse_the_run()
    {
        // compose config leaves out the inactive service; its image is not on the host and is never pulled.
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n  tunnel:\n    image: funnel/image\n" +
                                   "    profiles: [public]\n    labels:\n      docktail.funnel.enable: \"true\"\n", PlainConfig);
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, result.Refusal ?? result.Error);
    }

    [Fact]
    public async Task A_plan_that_only_shares_a_namespace_decides_that_container_and_deploys()
    {
        _docker.Add(new ContainerInfo { Id = "helper-id", Name = "helper", ServerId = "local" });
        _host.Inspect["helper-id"] = """{"Id":"helper-id","Name":"/helper","Config":{"Labels":{}},"HostConfig":{"NetworkMode":"bridge"}}""";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    network_mode: \"container:helper\"\n",
            """{"services":{"web":{"image":"nginx","network_mode":"container:helper"}}}""");

        Assert.Empty(plan.Exposures());
        Assert.Empty(plan.Mounts());
        Assert.True(ComposeImpact.NeedsExposureDecision(plan));
        Assert.Contains("helper", plan.SharedIdentities());
        var (outcome, _) = await McpAsync(new
        {
            serverId = "local", projectName = "shop",
            composeContent = "name: shop\nservices:\n  web:\n    image: nginx\n    network_mode: \"container:helper\"\n",
        });
        Assert.Equal(MutationStatus.Executed, outcome.Status);
    }

    [Fact]
    public async Task A_shared_container_named_by_a_variable_is_bound_but_never_written()
    {
        _docker.Add(new ContainerInfo { Id = "helper-id", Name = "helper", ServerId = "local" });
        _host.Inspect["helper-id"] = """{"Id":"helper-id","Name":"/helper","Config":{"Labels":{}},"HostConfig":{"NetworkMode":"bridge"}}""";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    pid: \"container:${X}\"\n",
            """{"services":{"web":{"image":"nginx","pid":"container:helper"}}}""", env: "X=helper\n");

        var args = Args(ComposeImpact.ExposureArguments(plan));

        Assert.DoesNotContain("helper", args["related"].GetString());
        Assert.Contains("helper", args["relatedWithheld"].GetString());
        Assert.DoesNotContain("helper", SinkRedaction.Arguments(args));
    }

    [Fact]
    public async Task A_pid_namespace_shared_with_a_container_of_host_pid_is_of_class_host()
    {
        // container:x inherits x's exposures: node-exporter runs with pid: host.
        _docker.Add(new ContainerInfo { Id = "ne-id", Name = "node-exporter", ServerId = "local" });
        _host.Inspect["ne-id"] = """{"Id":"ne-id","Name":"/node-exporter","Config":{"Labels":{}},"HostConfig":{"PidMode":"host"}}""";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    pid: \"container:node-exporter\"\n",
            """{"services":{"web":{"image":"nginx","pid":"container:node-exporter"}}}""");

        Assert.Contains(ExposureClasses.Host, Args(ComposeImpact.ExposureArguments(plan))["exposureClasses"].GetString());
    }

    [Fact]
    public async Task A_shared_container_that_cannot_be_resolved_refuses_the_plan() =>
        await Assert.ThrowsAsync<ComposePlanException>(() => PlanAsync(
            "name: shop\nservices:\n  web:\n    image: nginx\n    network_mode: \"container:nowhere\"\n",
            """{"services":{"web":{"image":"nginx","network_mode":"container:nowhere"}}}"""));

    // ---------------------------------------------------------------- doubles

    private sealed class Person : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Admin);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Admin);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(AppRole.Admin >= required);
    }

    private sealed class Notifications : INotificationService
    {
        public List<NotificationEvent> Sent { get; } = [];
        public Task SendAsync(NotificationEvent evt) { lock (Sent) Sent.Add(evt); return Task.CompletedTask; }
        public Task SendTestAsync() => Task.CompletedTask;
    }
}
