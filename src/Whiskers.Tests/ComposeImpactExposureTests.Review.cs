using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Utils;
using static Whiskers.Tests.AutomationMutationRunnerTests;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0006, review round 1: the nested decision's arguments only from Whiskers itself (B1), the daemon's default
/// cgroup namespace (M1), the paths through their real entry points (M2), the key of a value-only withheld entry (m1),
/// the re-check after an <c>up</c> that threw (m2), no withheld key in a violation (m3) and no
/// <c>exposure_change</c> after a failed <c>up</c>.
/// </summary>
public sealed partial class ComposeImpactExposureTests
{
    private static readonly string FunnelIdentity =
        ComposeExposures.Identity("web", ExposureKind.RouteLabel, "docktail.funnel.enable", "true");

    // ---------------------------------------------------------------- harness: the MCP tools through the gateway

    private static IHttpContextAccessor Http()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer k1";
        return new HttpContextAccessor { HttpContext = context };
    }

    /// <summary>A tool call as <c>McpGuardrailFilter</c> makes it: the gateway decides, then the tool runs inside.</summary>
    private async Task<(MutationOutcome<string> Outcome, bool Ran)> ToolAsync(string capability, object arguments,
        Func<Task<string>> tool, MutationSurface surface = MutationSurface.Mcp, GuardrailPolicy? policy = null,
        IMutationAuditSink? audit = null)
    {
        var catalog = new CapabilityCatalog();
        var args = Args(arguments);
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin);
        var ran = false;
        var outcome = await Gateway(policy ?? AdminPolicy(), audit).ExecuteAsync(
            new MutationRequest(capability, principal, surface, MutationTargets.FromArguments(catalog.Find(capability)!, args), args),
            async (_, _) => { ran = true; return await tool(); });
        return (outcome, ran);
    }

    private Task<string> DeployComposeTool(string yaml, ComposeRunner? runner = null) =>
        ContainerTools.DeployCompose(Http(), new ToolKeys(), _host, Planner(), runner ?? Runner(), new NullToolAudit(),
            "local", "shop", yaml);

    private Task<string> SetContainerEnvTool(string envVars, ComposeRunner? runner = null) =>
        ContainerTools.SetContainerEnv(Http(), new ToolKeys(), _docker, _host, Planner(), runner ?? Runner(),
            new NullToolAudit(), "shop-web-1", envVars, "local");

    /// <summary>The running <c>shop-web-1</c> of the project in <c>/srv/shop</c>, as the .env paths find it.</summary>
    private ContainerInfo ShopWeb(string yaml)
    {
        var web = new ContainerInfo
        {
            Id = "shop-web-id", Name = "shop-web-1", ServerId = "local", State = "running",
            Labels = new()
            {
                [ComposeModel.ProjectLabel] = "shop", [ComposeModel.ServiceLabel] = "web",
                ["com.docker.compose.project.working_dir"] = "/srv/shop",
                ["com.docker.compose.project.config_files"] = "/srv/shop/compose.yaml",
            },
        };
        _docker.Add(web);
        _host.Files["/srv/shop/compose.yaml"] = yaml;
        return web;
    }

    // ---------------------------------------------------------------- B1: the lists come from Whiskers only

    [Fact]
    public async Task Deploy_compose_with_its_own_pulled_funnel_image_is_refused_before_up()
    {
        _host.ComposeConfig = PlainConfig;
        _pulls["nginx"] = ("sha256:9999999999999999999999999999999999999999999999999999999999999999", """{"docktail.funnel.enable":"true"}""");

        var (outcome, ran) = await ToolAsync("compose.deploy",
            new { serverId = "local", projectName = "shop", composeContent = PlainYaml }, () => DeployComposeTool(PlainYaml));

        Assert.True(ran);
        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.Contains("docktail.funnel.enable", outcome.Value);
        Assert.Equal(-1, IndexOf(" up -d"));
    }

    [Theory]
    [InlineData("exposureWithheld")]
    [InlineData("exposureValueWithheld")]
    [InlineData("exposure")]
    [InlineData("exposureShown")]
    [InlineData("exposureClasses")]
    [InlineData("related")]
    [InlineData("relatedWithheld")]
    [InlineData("mounts")]
    public async Task Deploy_compose_that_brings_a_nested_decision_argument_is_refused_before_anything_runs(string name)
    {
        _host.ComposeConfig = PlainConfig;
        _pulls["nginx"] = ("sha256:9999999999999999999999999999999999999999999999999999999999999999", """{"docktail.funnel.enable":"true"}""");
        var arguments = new Dictionary<string, string>
        {
            ["serverId"] = "local", ["projectName"] = "shop", ["composeContent"] = PlainYaml,
            [name] = name.StartsWith("related", StringComparison.Ordinal) ? "whiskers" : name == "mounts" ? "/var/run/docker.sock" : FunnelIdentity,
        };

        foreach (var surface in new[] { MutationSurface.Mcp, MutationSurface.Agent, MutationSurface.Webhook, MutationSurface.Terminal })
        {
            var (outcome, ran) = await ToolAsync("compose.deploy", arguments, () => DeployComposeTool(PlainYaml), surface);

            Assert.False(ran);
            Assert.NotEqual(MutationStatus.Executed, outcome.Status);
            Assert.Contains($"'{name}'", outcome.Reason);
        }
        Assert.Equal(-1, IndexOf(" pull"));
        Assert.Equal(-1, IndexOf(" up -d"));
    }

    [Fact]
    public async Task The_ui_and_automation_surfaces_still_bring_the_nested_arguments()
    {
        var args = new Dictionary<string, string> { ["serverId"] = "local", ["exposure"] = FunnelIdentity, ["related"] = "helper" };
        _docker.Add(new ContainerInfo { Id = "helper-id", Name = "helper", ServerId = "local" });

        foreach (var surface in new[] { MutationSurface.Ui, MutationSurface.Automation })
        {
            var (outcome, _) = await ToolAsync("compose.deploy", args, () => Task.FromResult("ok"), surface);

            Assert.DoesNotContain("set by Whiskers itself", outcome.Reason ?? "");
        }
    }

    [Fact]
    public void The_scope_takes_exposures_and_shared_containers_from_the_resolved_target_only()
    {
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin);
        var brought = Args(new { exposure = FunnelIdentity, exposureWithheld = FunnelIdentity, related = "helper" });
        var target = new MutationTarget("server", "local", ServerId: "local");
        using var scope = MutationScope.Begin("compose.deploy", "op-b1", principal,
            new MutationRequest("compose.deploy", principal, MutationSurface.Mcp, target, brought));

        Assert.False(scope.CoversExposure("local", FunnelIdentity));
        Assert.False(scope.CoversRelated("local", "helper"));
    }

    [Fact]
    public async Task An_env_change_over_mcp_gains_nothing_from_a_brought_exposure_argument()
    {
        ShopWeb("name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n      docktail.funnel.enable: \"${FUNNEL:-false}\"\n");
        _host.ComposeConfig = FunnelConfig;

        var (outcome, ran) = await ToolAsync("container.env_set",
            new { containerId = "shop-web-1", envVars = "FUNNEL=true", serverId = "local", exposure = FunnelIdentity },
            () => SetContainerEnvTool("FUNNEL=true"));

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.False(ran);
    }

    // ---------------------------------------------------------------- M1: the daemon's default cgroup namespace

    private const string CgroupHost = """{"NetworkMode":"shop_default","CgroupnsMode":"host"}""";

    [Fact]
    public async Task A_host_cgroup_namespace_that_is_the_daemons_default_passes_the_re_check()
    {
        _host.CgroupnsOutput = "1\n---\n";   // cgroup v1: the default is host
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", CgroupHost, ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, string.Join("; ", result.Violations));
        Assert.Empty(_docker.Actions);
    }

    [Fact]
    public async Task A_daemon_json_default_of_host_wins_over_cgroup_v2()
    {
        _host.CgroupnsOutput = "2\n---\n{\"default-cgroupns-mode\": \"host\"}\n";
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", CgroupHost, ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, string.Join("; ", result.Violations));
    }

    [Fact]
    public async Task A_host_cgroup_namespace_on_a_private_default_is_stopped()
    {
        _host.CgroupnsOutput = "2\n---\n";
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", CgroupHost, ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, v => v.Contains("host namespace cgroup", StringComparison.Ordinal));
        Assert.Contains(_docker.Actions, a => a is { Op: "stop", ContainerId: "new-web" });
    }

    [Fact]
    public async Task A_default_that_cannot_be_read_fails_closed_and_says_so()
    {
        _host.CgroupnsOutput = null;
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", CgroupHost, ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("default cgroup namespace mode could not be read", StringComparison.Ordinal));
        Assert.Contains(_docker.Actions, a => a is { Op: "stop", ContainerId: "new-web" });
    }

    [Fact]
    public async Task A_plan_that_sets_cgroup_is_compared_whatever_the_default()
    {
        _host.CgroupnsOutput = "1\n---\n";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    cgroup: private\n",
            """{"services":{"web":{"image":"nginx","cgroup":"private"}}}""");
        _upCreates.Add(("new-web", "web", CgroupHost, ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("host namespace cgroup", StringComparison.Ordinal));
        Assert.Equal(0, _host.CgroupnsReads);
    }

    [Fact]
    public async Task The_default_is_read_once_per_server()
    {
        _host.CgroupnsOutput = "1\n---\n";
        var runner = Runner();
        foreach (var id in new[] { "new-web-1", "new-web-2" })
        {
            _upCreates.Clear();
            _upCreates.Add((id, "web", CgroupHost, ""));
            var plan = await PlanAsync(PlainYaml, PlainConfig);
            using var _ = Decide(plan, agent: true);
            var result = await runner.RunAsync(plan, (c, t, k) => _host.ExecuteAsync("local", c, t, k),
                new ComposeRunOptions(true, TimeSpan.FromMinutes(1)));
            Assert.True(result.Success, string.Join("; ", result.Violations));
        }

        Assert.Equal(1, _host.CgroupnsReads);
    }

    [Theory]
    [InlineData("1\n---\n", "host")]
    [InlineData("2\n---\n", "private")]
    [InlineData("2\r\n---\r\n", "private")]
    [InlineData("2\n---\n{\"default-cgroupns-mode\":\"host\"}", "host")]
    [InlineData("1\n---\n{\"default-cgroupns-mode\":\"private\"}", "private")]
    [InlineData("2\n---\n{\"log-driver\":\"json-file\"}", "private")]
    [InlineData("2\n---\n{not json", null)]
    [InlineData("2\n---\n[]", null)]
    [InlineData("2\n---\n{\"default-cgroupns-mode\":\"weird\"}", null)]
    [InlineData("2\n---\n{\"default-cgroupns-mode\":1}", null)]
    [InlineData("\n---\n", null)]
    [InlineData("3\n---\n", null)]
    [InlineData("2", null)]
    [InlineData("", null)]
    public void The_default_cgroup_namespace_mode_is_parsed_fail_closed(string output, string? expected) =>
        Assert.Equal(expected, ComposeExposures.ParseCgroupnsDefault(output));

    [Theory]
    [InlineData("1\n---\n{\"default-cgroupns-mode\":\"private\"}", "host")]
    [InlineData("1\n---\n", "host")]
    [InlineData("2\n---\n", "private")]
    [InlineData("2\n---\n{\"default-cgroupns-mode\":\"private\"}", "private")]
    [InlineData("3\n---\n", null)]
    public void On_cgroup_v1_a_privileged_containers_default_is_host_whatever_daemon_json_says(string output, string? expected) =>
        Assert.Equal(expected, ComposeExposures.ParseCgroupnsDefault(output, privileged: true));

    [Fact]
    public async Task A_privileged_service_on_cgroup_v1_with_a_private_daemon_default_passes_the_re_check()
    {
        _host.CgroupnsOutput = "1\n---\n{\"default-cgroupns-mode\":\"private\"}\n";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    privileged: true\n",
            """{"services":{"web":{"image":"nginx","privileged":true}}}""");
        _upCreates.Add(("new-web", "web", """{"NetworkMode":"shop_default","Privileged":true,"CgroupnsMode":"host"}""", ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, string.Join("; ", result.Violations));
        Assert.Empty(_docker.Actions);
    }

    [Fact]
    public async Task One_run_reads_the_default_once_and_still_decides_per_container_whether_it_is_privileged()
    {
        _host.CgroupnsOutput = "1\n---\n{\"default-cgroupns-mode\":\"private\"}\n";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    privileged: true\n  db:\n    image: nginx\n",
            """{"services":{"web":{"image":"nginx","privileged":true},"db":{"image":"nginx"}}}""");
        _upCreates.Add(("new-web", "web", """{"NetworkMode":"shop_default","Privileged":true,"CgroupnsMode":"host"}""", ""));
        _upCreates.Add(("new-db", "db", CgroupHost, ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.Equal(1, _host.CgroupnsReads);
        Assert.Single(result.Violations, v => v.Contains("host namespace cgroup", StringComparison.Ordinal));
        Assert.Equal([("disable_restart", "new-db"), ("stop", "new-db")], _docker.Actions.Select(a => (a.Op, a.ContainerId)));
    }

    [Fact]
    public async Task The_cgroup_cache_keeps_the_parsed_pair_and_no_text_of_daemon_json()
    {
        const string secret = "http://user:s3cr3t@proxy.example.org:3128";
        _host.CgroupnsOutput = "1\n---\n{\"default-cgroupns-mode\":\"private\",\"proxies\":{\"http-proxy\":\"" + secret + "\"}}\n";
        var runner = Runner();
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", CgroupHost, ""));
        using var _ = Decide(plan, agent: true);

        await runner.RunAsync(plan, (c, t, k) => _host.ExecuteAsync("local", c, t, k), new ComposeRunOptions(true, TimeSpan.FromMinutes(1)));

        Assert.Equal(new CgroupnsDaemon(1, CgroupnsConfig.Private), Assert.Single(runner.CgroupnsCache).Value);
        // The cached value type has no room for text: only the version and an enum.
        var fields = typeof(CgroupnsDaemon).GetFields(System.Reflection.BindingFlags.Instance
                                                      | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.All(fields, f => Assert.True(f.FieldType == typeof(int) || f.FieldType == typeof(CgroupnsConfig), f.Name));
    }

    [Fact]
    public async Task An_unprivileged_service_on_cgroup_v1_with_a_private_daemon_default_is_still_stopped()
    {
        _host.CgroupnsOutput = "1\n---\n{\"default-cgroupns-mode\":\"private\"}\n";
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", CgroupHost, ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("host namespace cgroup", StringComparison.Ordinal));
        Assert.Contains(_docker.Actions, a => a is { Op: "stop", ContainerId: "new-web" });
    }

    // ---------------------------------------------------------------- m1: a value-only withheld entry

    private const string FunnelFromEnvYaml =
        "name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n      docktail.funnel.enable: \"${FUNNEL}\"\n";

    [Fact]
    public async Task A_value_from_the_env_keeps_its_key_in_the_audited_exposure_shown_and_never_its_value()
    {
        var plan = await PlanAsync(FunnelFromEnvYaml, FunnelConfig, env: "FUNNEL=true\n");

        var args = Args(ComposeImpact.ExposureArguments(plan));
        var audited = SinkRedaction.Arguments(args);

        Assert.Equal(FunnelIdentity, args["exposureValueWithheld"].GetString());
        Assert.Equal("", args["exposureWithheld"].GetString());
        Assert.Contains("docktail.funnel.enable", args["exposureShown"].GetString());
        Assert.DoesNotContain("true", args["exposureShown"].GetString());
        Assert.Contains("docktail.funnel.enable", audited);
        Assert.DoesNotContain(FunnelIdentity, audited);
    }

    [Fact]
    public async Task A_key_from_the_env_shows_only_its_kind()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n      ${LABEL}: \"true\"\n",
            FunnelConfig, env: "LABEL=docktail.funnel.enable\n");

        var args = Args(ComposeImpact.ExposureArguments(plan));

        Assert.Equal("web: label (withheld)", args["exposureShown"].GetString());
        Assert.Equal(FunnelIdentity, args["exposureWithheld"].GetString());
        Assert.Equal("", args["exposureValueWithheld"].GetString());
        Assert.DoesNotContain("docktail", SinkRedaction.Arguments(args));
    }

    [Fact]
    public void The_policy_hands_a_value_only_withheld_entry_to_the_rule_under_its_own_prefix()
    {
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin);
        var keyWithheld = ComposeExposures.Identity("web", ExposureKind.RouteLabel, "caddy.tls.dns", "x");
        var target = new MutationTarget("server", "local", ServerId: "local")
        {
            Exposures = [FunnelIdentity, keyWithheld],
            Withheld = new HashSet<string>([FunnelIdentity, keyWithheld], StringComparer.Ordinal),
            ExposureKeysShown = new HashSet<string>([FunnelIdentity], StringComparer.Ordinal),
        };

        var args = GuardrailMutationPolicy.WithResolvedTarget(
            new MutationRequest("compose.deploy", principal, MutationSurface.Mcp, target, new Dictionary<string, JsonElement>()));

        Assert.Equal(FunnelIdentity, args[$"{ExposureChangeRule.ResolvedValueWithheldPrefix}0"].GetString());
        Assert.Equal(keyWithheld, args[$"{ExposureChangeRule.ResolvedWithheldPrefix}1"].GetString());
    }

    [Fact]
    public async Task A_ui_decision_with_a_value_only_withheld_entry_marks_it_so_on_the_target()
    {
        var plan = await PlanAsync(FunnelFromEnvYaml, FunnelConfig, env: "FUNNEL=true\n");
        var args = Args(ComposeImpact.ExposureArguments(plan));
        var person = new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(Admin), Admin, McpPermissionLevels.Admin);
        var catalog = new CapabilityCatalog();
        MutationTarget? seen = null;

        var outcome = await Gateway(AdminPolicy()).ExecuteAsync(
            new MutationRequest("compose.deploy", person, MutationSurface.Ui,
                MutationTargets.FromArguments(catalog.Find("compose.deploy")!, args), args),
            (ctx, _) => { seen = ctx.Request.Target; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Contains(FunnelIdentity, seen!.ExposureKeysShown);
        Assert.Contains(FunnelIdentity, seen.Withheld);
    }

    // ---------------------------------------------------------------- m2: an up that threw

    [Fact]
    public async Task An_up_that_throws_still_re_checks_and_stops_and_the_exception_goes_on()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", """{"Privileged":true}""", ""));
        var inner = _host.Effect!;
        _host.Effect = command =>
        {
            var result = inner(command);
            if (command.Contains(" up -d", StringComparison.Ordinal)) throw new TimeoutException("up timed out");
            return result;
        };
        using var _ = Decide(plan, agent: true);

        await Assert.ThrowsAsync<TimeoutException>(() => RunAsync(plan));

        Assert.Equal([("disable_restart", "new-web"), ("stop", "new-web")], _docker.Actions.Select(a => (a.Op, a.ContainerId)));
        Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureViolation);
        Assert.DoesNotContain(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureChange);
    }

    // ---------------------------------------------------------------- m3: no withheld key in a violation

    [Fact]
    public async Task A_violation_never_quotes_a_key_the_plan_withheld()
    {
        const string key = "caddy.tls.dns";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n      ${LABEL}: \"x\"\n",
            """{"services":{"web":{"image":"nginx","labels":{"caddy.tls.dns":"x"}}}}""", env: $"LABEL={key}\n");
        // up starts it with another value than the one decided.
        _upCreates.Add(("new-web", "web", NoHost, $$""","{{key}}":"y" """));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("label (withheld)", StringComparison.Ordinal));
        Assert.All(result.Violations, v => Assert.DoesNotContain(key, v));
        Assert.All(_notifications.Sent, n => Assert.DoesNotContain(key, n.ImageInfo));
    }

    [Fact]
    public async Task A_violation_never_quotes_a_label_key_neither_the_plan_nor_the_image_has()
    {
        const string key = "traefik.http.routers.s3cr3t-name.rule";
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        _upCreates.Add(("new-web", "web", NoHost, $$""","{{key}}":"Host(`a.example.org`)" """));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("label (withheld)", StringComparison.Ordinal));
        Assert.All(result.Violations, v => Assert.DoesNotContain("s3cr3t-name", v));
    }

    [Fact]
    public async Task A_violation_names_a_literal_key_of_the_plan()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    labels:\n      caddy: a.example.org\n",
            """{"services":{"web":{"image":"nginx","labels":{"caddy":"a.example.org"}}}}""");
        _upCreates.Add(("new-web", "web", NoHost, ""","caddy":"b.example.org" """));
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.Contains(result.Violations, v => v.Contains("label caddy", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- NIT: no exposure_change after a failed up

    [Fact]
    public async Task A_failed_up_sends_no_exposure_change()
    {
        var plan = await PlanAsync(FunnelYaml, FunnelConfig);
        var inner = _host.Effect!;
        _host.Effect = command => inner(command)
                                  ?? (command.Contains(" up -d", StringComparison.Ordinal)
                                      ? new Whiskers.Services.Server.CommandResult { ExitCode = 1, Error = "up failed" }
                                      : null);
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.DoesNotContain(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureChange);
    }

    // ---------------------------------------------------------------- M2: the paths through their entry points

    [Fact]
    public async Task Mcp_deploy_compose_re_checks_and_stops_what_up_started_undecided()
    {
        _host.ComposeConfig = PlainConfig;
        _upCreates.Add(("new-web", "web", """{"NetworkMode":"host"}""", ""));

        var (outcome, ran) = await ToolAsync("compose.deploy",
            new { serverId = "local", projectName = "shop", composeContent = PlainYaml }, () => DeployComposeTool(PlainYaml));

        Assert.True(ran);
        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Contains("host network", outcome.Value);
        Assert.Equal([("disable_restart", "new-web"), ("stop", "new-web")], _docker.Actions.Select(a => (a.Op, a.ContainerId)));
    }

    [Fact]
    public async Task Mcp_set_container_env_re_checks_and_stops_what_up_started_undecided()
    {
        ShopWeb(PlainYaml);
        _host.ComposeConfig = PlainConfig;
        _upCreates.Add(("new-web", "web", """{"Privileged":true}""", ""));

        var (outcome, ran) = await ToolAsync("container.env_set",
            new { containerId = "shop-web-1", envVars = "A=1", serverId = "local" }, () => SetContainerEnvTool("A=1"));

        Assert.True(ran);
        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Contains("privileged", outcome.Value);
        Assert.Contains(_docker.Actions, a => a is { Op: "stop", ContainerId: "new-web" });
    }

    [Fact]
    public async Task The_pages_env_save_re_checks_what_up_started_and_reports_it_for_the_person()
    {
        var web = ShopWeb(PlainYaml);
        _host.ComposeConfig = PlainConfig;
        _upCreates.Add(("new-web", "web", """{"Privileged":true}""", ""));
        var ui = new UiMutationRunner(Gateway(AdminPolicy()), new CapabilityCatalog(), new Person());

        var result = await ui.SaveEnvAsync(_host, Planner(), Runner(), web, "local", "A=1\n");

        Assert.False(result.Success);
        Assert.Contains("privileged", result.Error);
        Assert.Empty(_docker.Actions);   // a person's run is reported, not stopped (PC-D1)
        Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureViolation);
        Assert.Contains(_host.Executed, c => c.Contains(" up -d --pull never --no-build", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_pages_env_save_decides_the_exposures_nested_and_announces_them()
    {
        var web = ShopWeb(FunnelYaml);
        _host.ComposeConfig = FunnelConfig;
        _upCreates.Add(("new-web", "web", NoHost, ""","docktail.funnel.enable":"true" """));
        var audit = new RequestAudit();
        var ui = new UiMutationRunner(Gateway(AdminPolicy(), audit), new CapabilityCatalog(), new Person());

        var result = await ui.SaveEnvAsync(_host, Planner(), Runner(), web, "local", "A=1\n");

        Assert.True(result.Success, result.Error);
        var decided = Assert.Single(audit.Requests, r => r.Arguments.ContainsKey("exposureShown"));
        Assert.Equal("compose.deploy", decided.CapabilityId);
        Assert.Contains("docktail.funnel.enable", decided.Arguments["exposure"].GetString());
        Assert.Contains(audit.Requests, r => r.CapabilityId == "container.env_file_save");
        Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureChange);
    }

    [Fact]
    public async Task An_exposure_that_turns_up_between_the_decision_and_the_run_stops_the_env_save()
    {
        var web = ShopWeb(PlainYaml);
        _host.ComposeConfig = PlainConfig;
        var ui = new UiMutationRunner(Gateway(AdminPolicy()), new CapabilityCatalog(), new Person());
        var planner = new EditedBetweenPlanner(Planner(), () =>
        {
            _host.Files["/srv/shop/compose.yaml"] = FunnelYaml;
            _host.ComposeConfig = FunnelConfig;
        });

        var error = await Assert.ThrowsAnyAsync<Exception>(() => ui.SaveEnvAsync(_host, planner, Runner(), web, "local", "A=1\n"));

        Assert.Contains("exposure", error.Message);
        Assert.Equal(-1, IndexOf(" up -d"));
        Assert.DoesNotContain(_host.Executed, c => c.Contains(".env", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_exposure_that_turns_up_between_the_decision_and_the_run_stops_mcp_set_container_env()
    {
        ShopWeb(PlainYaml);
        _host.ComposeConfig = PlainConfig;

        var (outcome, ran) = await ToolAsync("container.env_set",
            new { containerId = "shop-web-1", envVars = "A=1", serverId = "local" }, () =>
            {
                // The compose file is edited after the gateway decided.
                _host.Files["/srv/shop/compose.yaml"] = FunnelYaml;
                _host.ComposeConfig = FunnelConfig;
                return SetContainerEnvTool("A=1");
            });

        Assert.True(ran);
        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Contains("not decided", outcome.Value);
        Assert.Equal(-1, IndexOf(" up -d"));
    }

    [Fact]
    public async Task Every_path_hands_the_gateway_the_same_nested_exposure_arguments()
    {
        var web = ShopWeb(FunnelYaml);
        _host.ComposeConfig = FunnelConfig;
        var plan = await PlanAsync(FunnelYaml, FunnelConfig);
        var expected = Nested(Args(ComposeImpact.ExposureArguments(plan)));
        var audit = new RequestAudit();
        var gateway = Gateway(AdminPolicy(), audit);
        var ui = new UiMutationRunner(gateway, new CapabilityCatalog(), new Person());

        // The UI deployment, the page's .env save and the automation gate (webhook, Git deploy).
        await ui.DeployAsync(_host, Planner(), Runner(), "compose.deploy", new { serverId = "local", projectName = "shop" },
            ComposeDeployment.FromContent("local", Dir, FunnelYaml));
        await ui.SaveEnvAsync(_host, Planner(), Runner(), web, "local", "A=1\n");
        var roles = new Roles();
        var whitelist = new Whitelist();
        roles.Map[Admin] = AppRole.Admin;
        whitelist.Allowed.Add(Admin);
        var automation = new AutomationMutationRunner(gateway, new CapabilityCatalog(),
            new AutomationOwnerResolver(roles, whitelist, new Keys(), new Accounts()),
            new StaticOptionsMonitor<AutomationOptions>(new AutomationOptions { RequireOwner = true }));
        await automation.GateFor(new AutomationActor("webhook:w1", "deploy", PrincipalIds.User(Admin), PrincipalIds.User(Admin)),
            "compose.deploy")(plan, () => Task.CompletedTask);

        var nested = audit.Requests.Where(r => r.Arguments.ContainsKey("exposureShown")).Select(r => Nested(r.Arguments)).ToList();
        Assert.Equal(3, nested.Count);
        Assert.All(nested, n => Assert.Equal(expected, n));

        // MCP decides the same exposures on its target, planned by the gateway itself.
        var (_, seen) = await McpAsync(new { serverId = "local", projectName = "shop", composeContent = FunnelYaml },
            ConfirmNothing());
        Assert.Equal(plan.Exposures().Select(e => e.Identity).Order(), seen!.Exposures.Order());
    }

    private static string Nested(IReadOnlyDictionary<string, JsonElement> arguments) =>
        string.Join("|", MutationTargetResolver.NestedDecisionArguments.Select(k => $"{k}={(arguments.TryGetValue(k, out var v) ? v.GetRawText() : "-")}"));

    private static GuardrailPolicy ConfirmNothing()
    {
        var policy = AdminPolicy();
        policy.ExposureConfirmClasses = [];
        return policy;
    }

    // ---------------------------------------------------------------- doubles

    /// <summary>A planner whose project is edited on disk after its first plan (the decision) and before the next.</summary>
    private sealed class EditedBetweenPlanner(IComposePlanner inner, Action edit) : IComposePlanner
    {
        private int _calls;

        public Task<ComposePlan> PlanAsync(ComposeDeployment deployment, CancellationToken ct = default) =>
            inner.PlanAsync(deployment, ct);

        public Task<ComposePlan> PlanEnvChangeAsync(ContainerInfo container, string serverId, Func<string?, string> newEnv,
            CancellationToken ct = default)
        {
            if (++_calls == 2) edit();
            return inner.PlanEnvChangeAsync(container, serverId, newEnv, ct);
        }
    }

    /// <summary>Remembers every request the gateway audits.</summary>
    private sealed class RequestAudit : IMutationAuditSink
    {
        private readonly AgentToolTestHelpers.NullMutationAudit _inner = new();
        public List<MutationRequest> Requests { get; } = [];

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            Whiskers.Services.Approvals.ApprovalGateResult? approval, CancellationToken ct)
        {
            Requests.Add(request);
            return _inner.BeginAsync(operationId, request, capability, approval, ct);
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct) =>
            _inner.CompleteAsync(auditId, operationId, status, verification, reason, duration, ct);

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            Requests.Add(request);
            return _inner.RecordDeniedAsync(operationId, request, capability, status, reason, ct);
        }
    }

    /// <summary>The key <c>k1</c>, allowed every tool.</summary>
    private sealed class ToolKeys : IMcpPermissionService
    {
        private static readonly McpApiKeyConfig K1 = new()
        {
            Id = "k1", Name = "ci", PermissionLevel = McpPermissionLevels.Admin, Enabled = true,
        };

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public McpApiKeyConfig? ValidateKey(string key) => key == "k1" ? K1 : null;
        public bool IsToolAllowed(string key, string toolName) => key == "k1";
        public HashSet<string> GetAllowedTools(string key) => [];
        public McpPermissionData GetPermissionData() => new();
        public Task SavePermissionDataAsync(McpPermissionData data) => Task.CompletedTask;
        public Task<McpApiKeyConfig> AddApiKeyAsync(string name, string permissionLevel) => throw new NotSupportedException();
        public Task RemoveApiKeyAsync(string id) => Task.CompletedTask;
        public Task SetToolEnabledAsync(string toolName, bool enabled) => Task.CompletedTask;
    }

    private sealed class NullToolAudit : IToolAuditLog
    {
        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.CompletedTask;
    }
}
