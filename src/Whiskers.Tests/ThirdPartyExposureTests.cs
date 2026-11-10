using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0013, U3 and D16–D18: Traefik with <c>exposedByDefault</c>, cloudflared and Tailscale serve/Funnel are
/// recognised by image, argv, <c>sh -c</c> and environment key and become <see cref="ExposureKind.ExternalRoute"/>
/// exposures of class <c>public</c> (serve: <c>published</c>); anything unreadable refuses the plan; the host Traefik
/// (Q1) counts as routing when unreadable; <c>traefik.enable=false</c> exempts; step 4 refuses a changed config file;
/// the re-check finds a signal from the container. No value is ever part of a detail or a reason.
/// </summary>
public sealed class ThirdPartyExposureTests
{
    private const string Dir = "/opt/deployments/shop";
    /// <summary>Stands for a token or key in an environment value: it must never appear anywhere.</summary>
    private const string Hidden = "placeholder-value-7f3a9";
    private static readonly string Nginx = "sha256:" + new string('1', 64);
    private static readonly string Tool = "sha256:" + new string('2', 64);

    // ---------------------------------------------------------------- unit: DetectAsync

    private static readonly Dictionary<string, string?> NoFiles = new(StringComparer.Ordinal);

    private static ComposeThirdParty.HostFileReader Files(IReadOnlyDictionary<string, string?> files) =>
        (path, _) => Task.FromResult(files.TryGetValue(path, out var content) ? content : null);

    private static async Task<IReadOnlyList<string>> Signals(ToolSpec spec, IReadOnlyDictionary<string, string?>? files = null,
        IDictionary<string, string>? read = null) =>
        (await ComposeThirdParty.DetectAsync("svc", spec, Files(files ?? NoFiles),
            read ?? new Dictionary<string, string>(StringComparer.Ordinal), CancellationToken.None))
        .Select(e => e.Key).ToList();

    private static Dictionary<string, string?> Env(params string[] entries) =>
        ComposeThirdParty.Env(ComposeThirdParty.EnvList(entries));

    private static ToolSpec Spec(string image, string[]? entrypoint = null, string[]? command = null, string[]? env = null,
        ToolMount[]? mounts = null) =>
        new() { Image = image, Entrypoint = entrypoint, Command = command, Env = Env(env ?? []), Mounts = mounts ?? [] };

    [Theory]
    [InlineData("cloudflare/cloudflared:2024.1.0", null, null, null)]                                 // by image
    [InlineData("alpine", "/usr/local/bin/cloudflared", null, null)]                                  // by argv[0]
    [InlineData("alpine", "sh", "-c|apk add x && exec cloudflared tunnel run", null)]                 // by sh -c
    [InlineData("alpine", null, null, "TUNNEL_TOKEN")]                                                // by env key
    [InlineData("alpine", null, null, "TUNNEL_TOKEN_FILE")]
    public async Task Cloudflared_is_recognised_by_image_argv_sh_c_and_env_and_is_a_tunnel(string image, string? argv0,
        string? args, string? envKey)
    {
        var spec = Spec(image, argv0 is null ? null : [argv0], args?.Split('|'), envKey is null ? null : [envKey + "=" + Hidden]);

        Assert.Equal([ComposeThirdParty.CloudflaredTunnel], await Signals(spec));
        Assert.Equal(ExposureClasses.Public, ExposureClasses.Of(ExposureKind.ExternalRoute, ComposeThirdParty.CloudflaredTunnel, []));
    }

    [Theory]
    [InlineData("traefik:v3.1")]
    [InlineData("docker.io/library/traefik")]
    public async Task Traefik_by_image_without_any_provider_setting_exposes_nothing(string image) =>
        Assert.Empty(await Signals(Spec(image)));

    [Theory]
    [InlineData("--providers.docker")]                                         // Traefik's default exposedByDefault is true
    [InlineData("--providers.docker=true")]
    [InlineData("--providers.swarm.endpoint=tcp://x:2375")]                    // an option enables the provider
    [InlineData("--providers.docker.exposedbydefault=true")]
    [InlineData("--providers.docker.exposedByDefault true")]
    public async Task Traefik_cli_with_the_docker_provider_exposes_by_default(string flags) =>
        Assert.Equal([ComposeThirdParty.TraefikExposedByDefault],
            await Signals(Spec("traefik:v3", command: flags.Split(' '))));

    [Theory]
    [InlineData("--providers.docker --providers.docker.exposedbydefault=false")]
    [InlineData("--providers.docker=false")]
    [InlineData("--api.insecure=true --entrypoints.web.address=:80")]
    public async Task Traefik_cli_without_exposed_by_default_exposes_nothing(string flags) =>
        Assert.Empty(await Signals(Spec("traefik:v3", command: flags.Split(' '))));

    [Fact]
    public async Task Traefik_in_a_sh_c_string_and_by_argv_is_read_from_the_script_too()
    {
        Assert.Equal([ComposeThirdParty.TraefikExposedByDefault],
            await Signals(Spec("traefik", ["sh", "-c"], ["sleep 1; /usr/bin/traefik --providers.docker"])));
        Assert.Equal([ComposeThirdParty.TraefikExposedByDefault],
            await Signals(Spec("traefik", ["/traefik"], ["--providers.docker"])));
    }

    [Theory]
    [InlineData("TRAEFIK_PROVIDERS_DOCKER=true")]
    [InlineData("TRAEFIK_PROVIDERS_DOCKER_ENDPOINT=unix:///var/run/docker.sock")]
    [InlineData("TRAEFIK_PROVIDERS_SWARM_EXPOSEDBYDEFAULT=true")]
    public async Task Traefik_env_enables_the_provider(string env) =>
        Assert.Equal([ComposeThirdParty.TraefikExposedByDefault], await Signals(Spec("traefik", env: [env])));

    [Fact]
    public async Task Traefik_env_with_exposed_by_default_false_exposes_nothing() =>
        Assert.Empty(await Signals(Spec("traefik", env: ["TRAEFIK_PROVIDERS_DOCKER=true", "TRAEFIK_PROVIDERS_DOCKER_EXPOSEDBYDEFAULT=false"])));

    [Fact]
    public async Task Any_source_that_enables_it_counts()
    {
        // The CLI says false, the environment enables it: it counts.
        var spec = Spec("traefik", command: ["--providers.docker.exposedbydefault=false"], env: ["TRAEFIK_PROVIDERS_DOCKER=true"]);
        Assert.Equal([ComposeThirdParty.TraefikExposedByDefault], await Signals(spec));
    }

    private static readonly ToolMount EtcTraefik = new("bind", "/srv/traefik", "/etc/traefik");

    [Theory]
    [InlineData("traefik.yml", "providers:\n  docker: {}\n", true)]
    [InlineData("traefik.yml", "providers:\n  docker:\n    exposedByDefault: true\n", true)]
    [InlineData("traefik.yaml", "providers:\n  swarm:\n    endpoint: tcp://x\n", true)]
    [InlineData("traefik.yml", "providers:\n  docker:\n    exposedByDefault: false\n", false)]
    [InlineData("traefik.yml", "providers:\n  file:\n    directory: /x\n", false)]
    [InlineData("traefik.toml", "[providers.docker]\n  endpoint = \"unix:///var/run/docker.sock\"\n", true)]
    [InlineData("traefik.toml", "[providers.docker]\n  exposedByDefault = false  # explicit\n", false)]
    [InlineData("traefik.toml", "providers.docker.exposedByDefault = true\n", true)]
    [InlineData("traefik.toml", "[providers.docker.tls]\n  ca = \"/x\"\n", true)]
    [InlineData("traefik.toml", "[entryPoints.web]\n  address = \":80\"\n", false)]
    public async Task Traefik_static_file_in_yaml_and_toml(string file, string content, bool exposes)
    {
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        var signals = await Signals(Spec("traefik:v3", mounts: [EtcTraefik]),
            new Dictionary<string, string?> { [$"/srv/traefik/{file}"] = content }, read);

        Assert.Equal(exposes, signals.Contains(ComposeThirdParty.TraefikExposedByDefault));
        Assert.Contains($"/srv/traefik/{file}", read.Keys);
    }

    [Fact]
    public async Task Traefik_config_file_named_on_the_cli_is_read_through_its_mount()
    {
        var spec = Spec("traefik:v3", command: ["--configFile=/conf/static.yml"], mounts: [new("bind", "/srv/conf", "/conf")]);
        Assert.Equal([ComposeThirdParty.TraefikExposedByDefault], await Signals(spec,
            new Dictionary<string, string?> { ["/srv/conf/static.yml"] = "providers:\n  docker: {}\n" }));
    }

    public static TheoryData<string, ToolSpec, Dictionary<string, string?>> Unreadable => new()
    {
        // A sh -c string that runs a program named by a variable.
        { "variable", Spec("alpine", ["sh", "-c"], ["exec $PROXY --run"]), new() },
        // A config file on a named volume or tmpfs.
        { "volume", Spec("traefik", mounts: [new("volume", "conf", "/etc/traefik")]), new() },
        { "tmpfs", Spec("traefik", mounts: [new("tmpfs", null, "/etc/traefik")]), new() },
        // A non-official Traefik image might ship a static file.
        { "image", Spec("myorg/traefik:1"), new() },
        // A configured file that does not exist, does not parse, or uses TOML outside the subset.
        { "absent", Spec("traefik", command: ["--configfile=/conf/t.yml"], mounts: [new("bind", "/srv/conf", "/conf")]), new() },
        { "yaml", Spec("traefik", mounts: [EtcTraefik]), new() { ["/srv/traefik/traefik.yml"] = "providers: [docker\n" } },
        { "toml-inline", Spec("traefik", mounts: [EtcTraefik]), new() { ["/srv/traefik/traefik.toml"] = "providers = { docker = {} }\n" } },
        { "toml-table", Spec("traefik", mounts: [EtcTraefik]), new() { ["/srv/traefik/traefik.toml"] = "[providers]\ndocker = true\n" } },
        { "toml-multiline", Spec("traefik", mounts: [EtcTraefik]), new() { ["/srv/traefik/traefik.toml"] = "a = \"\"\"\nx\n\"\"\"\n" } },
        { "bool", Spec("traefik", command: ["--providers.docker.exposedbydefault=maybe"]), new() },
        // A Tailscale serve config that is not mounted, absent or not JSON.
        { "serve-image", Spec("tailscale/tailscale", env: ["TS_SERVE_CONFIG=/config/serve.json"]), new() },
        { "serve-absent", Spec("tailscale/tailscale", env: ["TS_SERVE_CONFIG=/config/serve.json"], mounts: [new("bind", "/srv/ts", "/config")]), new() },
        { "serve-json", Spec("tailscale/tailscale", env: ["TS_SERVE_CONFIG=/config/serve.json"], mounts: [new("bind", "/srv/ts", "/config")]),
            new() { ["/srv/ts/serve.json"] = "{not json" } },
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task Everything_unreadable_refuses(string _, ToolSpec spec, Dictionary<string, string?> files) =>
        await Assert.ThrowsAsync<ComposePlanException>(() => Signals(spec, files));

    [Fact]
    public async Task A_variable_that_is_only_an_argument_is_not_unreadable() =>
        Assert.Equal([ComposeThirdParty.CloudflaredTunnel],
            await Signals(Spec("alpine", ["sh", "-c"], ["cloudflared tunnel --token \"$TOKEN\" run"])));

    private static readonly ToolMount TsConfig = new("bind", "/srv/ts", "/config");

    [Theory]
    [InlineData("""{"TCP":{"443":{"HTTPS":true}},"AllowFunnel":{"${TS_CERT_DOMAIN}:443":true}}""", ComposeThirdParty.TailscaleFunnel)]
    [InlineData("""{"TCP":{"443":{"HTTPS":true}},"Web":{"x:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:80"}}}}}""", ComposeThirdParty.TailscaleServe)]
    [InlineData("""{"Services":{"svc:web":{"Endpoints":{"tcp:443":"http://127.0.0.1:80"}}}}""", ComposeThirdParty.TailscaleServe)]
    [InlineData("""{"AllowFunnel":{"x:443":false}}""", null)]
    public async Task Tailscale_serve_config_gives_funnel_or_serve(string json, string? signal)
    {
        var signals = await Signals(Spec("tailscale/tailscale:latest", env: ["TS_SERVE_CONFIG=/config/serve.json", "TS_AUTHKEY=" + Hidden],
            mounts: [TsConfig]), new Dictionary<string, string?> { ["/srv/ts/serve.json"] = json });

        Assert.Equal(signal is null ? [] : [signal], signals);
    }

    [Theory]
    [InlineData("tailscale funnel --bg 443", ComposeThirdParty.TailscaleFunnel)]
    [InlineData("tailscaled & sleep 2; tailscale serve --bg http://127.0.0.1:80", ComposeThirdParty.TailscaleServe)]
    public async Task Tailscale_serve_or_funnel_in_a_command(string script, string signal) =>
        Assert.Equal([signal], await Signals(Spec("alpine", ["sh", "-c"], [script])));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tailscale_with_persistent_state_and_no_serve_config_counts_as_public_by_design(bool stateDir)
    {
        var spec = Spec("tailscale/tailscale", env: stateDir ? ["TS_STATE_DIR=/var/lib/tailscale", "TS_AUTHKEY=" + Hidden] : ["TS_AUTHKEY=" + Hidden],
            mounts: stateDir ? [] : [new("volume", "ts-state", "/var/lib/tailscale")]);

        Assert.Equal([ComposeThirdParty.TailscaleState], await Signals(spec));
        Assert.Equal(ExposureClasses.Public, ComposeThirdParty.ClassOf(ComposeThirdParty.TailscaleState));
        Assert.Equal(ExposureClasses.Published, ComposeThirdParty.ClassOf(ComposeThirdParty.TailscaleServe));
    }

    [Fact]
    public async Task A_plain_service_is_no_tool() =>
        Assert.Empty(await Signals(Spec("nginx:1.27", ["/docker-entrypoint.sh"], ["nginx", "-g", "daemon off;"], ["NGINX_HOST=x"])));

    [Fact]
    public void Secret_env_values_are_never_kept()
    {
        var env = Env("TS_AUTHKEY=" + Hidden, "TUNNEL_TOKEN=" + Hidden, "TS_SERVE_CONFIG=/config/serve.json");

        Assert.Null(env["TS_AUTHKEY"]);
        Assert.Null(env["TUNNEL_TOKEN"]);
        Assert.Equal("/config/serve.json", env["TS_SERVE_CONFIG"]);
    }

    [Fact]
    public void The_image_config_completes_what_compose_says_as_docker_does()
    {
        const string image = """{"Entrypoint":["/usr/local/bin/containerboot"],"Cmd":["x"],"Env":["TS_STATE_DIR=/var/lib/tailscale","PATH=/bin"],"WorkingDir":"/w"}""";
        var effective = ComposeThirdParty.Effective(new ToolSpec { Image = "x", Command = ["y"] }, image);
        Assert.Equal(["/usr/local/bin/containerboot", "y"], ComposeThirdParty.ArgvOf(effective));
        Assert.Equal("/var/lib/tailscale", effective.Env["TS_STATE_DIR"]);
        Assert.Equal("/w", effective.WorkingDir);
        // Compose's entrypoint drops the image's Cmd.
        Assert.Equal(["sh"], ComposeThirdParty.ArgvOf(ComposeThirdParty.Effective(new ToolSpec { Image = "x", Entrypoint = ["sh"] }, image)));
    }

    // ---------------------------------------------------------------- D18: traefik. prefix, traefik.enable=false

    [Theory]
    [InlineData("traefik.enable", "true", true)]
    [InlineData("traefik.enable", "false", false)]
    [InlineData("Traefik.Enable", " FALSE ", false)]
    [InlineData("traefik.docker.network", "web", true)]
    [InlineData("traefik.http.routers.a.rule", "Host(`a`)", true)]
    public void Every_traefik_label_is_a_route_except_traefik_enable_false(string key, string value, bool route)
    {
        Assert.Contains("traefik.", GuardrailPolicy.DefaultExposureLabelPrefixes);
        Assert.Equal(route, ComposeExposures.IsRouteLabel(key, value, GuardrailPolicy.DefaultExposureLabelPrefixes));
    }

    [Fact]
    public void The_model_exempts_only_a_literal_traefik_enable_false()
    {
        var (literal, _) = ComposeModel.ExposuresOf([("/x/docker-compose.yml",
            "services:\n  web:\n    image: nginx\n    labels:\n      traefik.enable: \"false\"\n")],
            new Dictionary<string, string?>(), GuardrailPolicy.DefaultExposureLabelPrefixes);
        var (variable, _) = ComposeModel.ExposuresOf([("/x/docker-compose.yml",
            "services:\n  web:\n    image: nginx\n    labels:\n      traefik.enable: \"${ENABLE}\"\n")],
            new Dictionary<string, string?> { ["ENABLE"] = "false" }, GuardrailPolicy.DefaultExposureLabelPrefixes);

        Assert.Empty(literal["web"]);
        Assert.Single(variable["web"]);
    }

    [Fact]
    public void An_unresolved_variable_in_a_tool_field_refuses_the_plan()
    {
        Assert.Throws<ComposePlanException>(() => ComposeModel.ExposuresOf([("/x/docker-compose.yml",
            "services:\n  edge:\n    image: alpine\n    command: [\"${PROXY}\", \"run\"]\n")],
            new Dictionary<string, string?>(), GuardrailPolicy.DefaultExposureLabelPrefixes));
        Assert.Throws<ComposePlanException>(() => ComposeModel.ExposuresOf([("/x/docker-compose.yml",
            "services:\n  ts:\n    image: tailscale/tailscale\n    environment:\n      TS_SERVE_CONFIG: ${SERVE}\n")],
            new Dictionary<string, string?>(), GuardrailPolicy.DefaultExposureLabelPrefixes));
    }

    // ---------------------------------------------------------------- the rule: reasons without values

    [Theory]
    [InlineData(ComposeThirdParty.CloudflaredTunnel, "Cloudflare tunnel (cloudflared)")]
    [InlineData(ComposeThirdParty.TraefikExposedByDefault, "routes every container on the server (exposedByDefault)")]
    [InlineData(ComposeThirdParty.TraefikHost, "routes every container (exposedByDefault)")]
    [InlineData(ComposeThirdParty.TailscaleFunnel, "Tailscale Funnel")]
    [InlineData(ComposeThirdParty.TailscaleState, "counts as public")]
    public void The_rule_confirms_a_public_external_route_with_a_reason_that_names_the_signal(string key, string text)
    {
        var policy = new GuardrailPolicy { MaxAutonomousLevel = McpPermissionLevels.Admin, RequireConfirmationForWrites = false };
        var request = new GuardrailRequest("deploy_compose", McpPermissionLevels.Admin,
            new Dictionary<string, JsonElement>
            {
                [ExposureChangeRule.ResolvedPrefix + "0"] = JsonSerializer.SerializeToElement(
                    ComposeExposures.Identity("web", ExposureKind.ExternalRoute, key, "true")),
            },
            new AgentContext("s", new AgentPrincipal(AgentPrincipalKind.McpKey, "key", McpPermissionLevels.Admin, null),
                AgentOrigin.McpDirect, policy));

        Assert.Equal(GuardrailVerdict.Confirm, new ExposureChangeRule().Evaluate(request, out var reason));
        Assert.Contains(text, reason);
    }

    [Fact]
    public void Tailscale_serve_is_published_and_described_by_its_key()
    {
        Assert.Equal(ExposureClasses.Published,
            ExposureClasses.Of(ExposureKind.ExternalRoute, ComposeThirdParty.TailscaleServe, GuardrailPolicy.DefaultExposureLabelPrefixes));
        Assert.Equal("web: external route tailscale.serve",
            ComposeExposures.Describe(ComposeExposures.Identity("web", ExposureKind.ExternalRoute, ComposeThirdParty.TailscaleServe, "true"), false));
    }

    // ---------------------------------------------------------------- planner and runner

    private readonly ProtectedResourceResolutionTests.FakeHost _host = new();
    private readonly FakeDocker _docker = new() { AllowMutations = true };
    private readonly List<(string Id, string Service, string Inspect)> _upCreates = [];

    public ThirdPartyExposureTests()
    {
        _host.Images["nginx"] = (Nginx, "{}");
        _host.Effect = command =>
        {
            if (!command.Contains(" up -d", StringComparison.Ordinal)) return null;
            foreach (var (id, service, inspect) in _upCreates)
            {
                _docker.Add(new ContainerInfo
                {
                    Id = id, Name = $"shop-{service}-1", ServerId = "local", State = "running",
                    Labels = new() { [ComposeModel.ProjectLabel] = "shop", [ComposeModel.ServiceLabel] = service },
                });
                _host.Inspect[id] = inspect.Replace("@IMAGE@", _host.PinnedImageId(service) ?? "", StringComparison.Ordinal);
            }
            return null;
        };
    }

    private static string Container(string id, string service, string config) =>
        "{\"Id\":\"" + id + "\",\"Name\":\"/shop-" + service + "-1\",\"Image\":\"@IMAGE@\",\"Config\":{" + config +
        (config.Length > 0 ? "," : "") +
        "\"Labels\":{\"com.docker.compose.project\":\"shop\",\"com.docker.compose.service\":\"" + service + "\"}}," +
        "\"HostConfig\":{\"NetworkMode\":\"shop_default\"},\"Mounts\":[]}";

    private Task<ComposePlan> PlanAsync(string yaml, string config)
    {
        _host.ComposeConfig = config;
        return new ComposePlanner(_docker, _host).PlanAsync(ComposeDeployment.FromContent("local", Dir, yaml));
    }

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
        scope.SetDecision(agent, agent ? [ExposureClasses.Public, ExposureClasses.Host] : []);
        return scope;
    }

    private Task<ComposeRunResult> RunAsync(ComposePlan plan) =>
        new ComposeRunner(_host, _docker).RunAsync(plan, (c, t, k) => _host.ExecuteAsync("local", c, t, k),
            new ComposeRunOptions(true, TimeSpan.FromMinutes(1)));

    private const string TunnelYaml = "name: shop\nservices:\n  tunnel:\n    image: cloudflare/cloudflared\n    command: [\"tunnel\", \"run\"]\n" +
                                      "    environment:\n      TUNNEL_TOKEN: " + Hidden + "\n  web:\n    image: nginx\n    network_mode: service:tunnel\n";
    private const string TunnelConfig = "{\"services\":{\"tunnel\":{\"image\":\"cloudflare/cloudflared\",\"command\":[\"tunnel\",\"run\"]," +
                                        "\"environment\":{\"TUNNEL_TOKEN\":\"" + Hidden + "\"}},\"web\":{\"image\":\"nginx\",\"network_mode\":\"service:tunnel\"}}}";

    [Fact]
    public async Task The_plan_detects_a_tunnel_attaches_it_to_the_sharing_service_and_shows_no_value()
    {
        _host.Images["cloudflare/cloudflared"] = (Tool, "{}");

        var plan = await PlanAsync(TunnelYaml, TunnelConfig);

        var tunnel = Assert.Single(plan.Exposures(), e => e is { Service: "tunnel", Kind: ExposureKind.ExternalRoute });
        Assert.Equal(ComposeThirdParty.CloudflaredTunnel, tunnel.Key);
        Assert.Contains(plan.Exposures(), e => e is { Service: "web", Kind: ExposureKind.ExternalRoute, Key: ComposeThirdParty.CloudflaredTunnel });
        Assert.All(plan.Exposures(), e => Assert.DoesNotContain(Hidden, e.Identity + e.Display));
        Assert.All(plan.Services, s => Assert.DoesNotContain(Hidden, JsonSerializer.Serialize(s.Tool)));
    }

    [Fact]
    public async Task A_tool_found_only_in_the_pulled_image_is_refused_at_step_4_for_an_agent()
    {
        // While planning the image is not on the host; after the pull its entrypoint is cloudflared.
        var plan = await PlanAsync("name: shop\nservices:\n  edge:\n    image: myorg/edge\n",
            """{"services":{"edge":{"image":"myorg/edge"}}}""");
        Assert.DoesNotContain(plan.Exposures(), e => e.Kind == ExposureKind.ExternalRoute);
        var effect = _host.Effect;
        _host.Effect = command =>
        {
            if (command.Contains(" pull", StringComparison.Ordinal) && !command.Contains(" up -d", StringComparison.Ordinal))
            {
                _host.Images["myorg/edge"] = (Tool, "{}");
                _host.ImageConfigs["myorg/edge"] = """{"Entrypoint":["/usr/bin/cloudflared"],"Cmd":["tunnel","run"]}""";
            }
            return effect?.Invoke(command);
        };
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains(ComposeThirdParty.CloudflaredTunnel, result.Refusal);
        Assert.Contains("nothing was started", result.Refusal);
        Assert.DoesNotContain(_host.Executed, c => c.Contains(" up -d", StringComparison.Ordinal));
    }

    private const string TraefikYaml = "name: shop\nservices:\n  proxy:\n    image: traefik:v3\n    volumes:\n      - /srv/traefik:/etc/traefik:ro\n";
    private const string TraefikConfig = """{"services":{"proxy":{"image":"traefik:v3","volumes":[{"type":"bind","source":"/srv/traefik","target":"/etc/traefik","read_only":true}]}}}""";

    [Fact]
    public async Task Step_4_refuses_a_config_file_that_changed_since_the_plan()
    {
        _host.Images["traefik:v3"] = (Tool, "{}");
        _host.Files["/srv/traefik/traefik.yml"] = "providers:\n  file: {}\n";
        var plan = await PlanAsync(TraefikYaml, TraefikConfig);
        Assert.DoesNotContain(plan.Exposures(), e => e.Kind == ExposureKind.ExternalRoute);
        Assert.True(plan.ThirdPartyFiles.ContainsKey("/srv/traefik/traefik.yml"));
        var effect = _host.Effect;
        _host.Effect = command =>
        {
            if (command.Contains(" pull", StringComparison.Ordinal) && !command.Contains(" up -d", StringComparison.Ordinal))
                _host.Files["/srv/traefik/traefik.yml"] = "providers:\n  file: {}\n# edited\n";
            return effect?.Invoke(command);
        };
        using var _ = Decide(plan, agent: false);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("changed since the deployment was planned", result.Refusal);
    }

    [Fact]
    public async Task A_traefik_with_exposed_by_default_in_its_file_is_planned_as_public()
    {
        _host.Images["traefik:v3"] = (Tool, "{}");
        _host.Files["/srv/traefik/traefik.toml"] = "[providers.docker]\n";

        var plan = await PlanAsync(TraefikYaml, TraefikConfig);

        var e = Assert.Single(plan.Exposures(), x => x.Kind == ExposureKind.ExternalRoute);
        Assert.Equal(("proxy", ComposeThirdParty.TraefikExposedByDefault), (e.Service, e.Key));
    }

    private void HostTraefik(string inspect, string state = "running")
    {
        _docker.Add(new ContainerInfo
        {
            Id = "edge1", Name = "/edge-traefik", ServerId = "local", State = state, Image = "traefik:v2.11",
            Labels = new() { [ComposeModel.ProjectLabel] = "edge" },
        });
        if (inspect.Length > 0) _host.Inspect["edge1"] = inspect;
    }

    [Theory]
    [InlineData("""{"Id":"edge1","Config":{"Cmd":["--providers.docker"],"Labels":{}},"Mounts":[]}""", true)]
    [InlineData("""{"Id":"edge1","Config":{"Cmd":["--providers.docker","--providers.docker.exposedbydefault=false"],"Labels":{}},"Mounts":[]}""", false)]
    [InlineData("", true)]                                                       // cannot be inspected: counts, no refusal
    [InlineData("""{"Id":"edge1","Config":{"Cmd":["--configfile=/x/t.yml"],"Labels":{}},"Mounts":[]}""", true)]   // unreadable file
    public async Task A_traefik_on_the_host_routes_every_planned_service_without_traefik_enable_false(string inspect, bool routes)
    {
        HostTraefik(inspect);

        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n  db:\n    image: nginx\n    labels:\n      traefik.enable: \"false\"\n",
            """{"services":{"web":{"image":"nginx"},"db":{"image":"nginx","labels":{"traefik.enable":"false"}}}}""");

        var found = plan.Exposures().Where(e => e.Key == ComposeThirdParty.TraefikHost).ToList();
        if (!routes)
        {
            Assert.Empty(found);
            return;
        }
        var e = Assert.Single(found);
        Assert.Equal(("web", "edge-traefik"), (e.Service, e.Value));
        Assert.Equal(ExposureClasses.Public, ExposureClasses.Of(e.Kind, e.Key, plan.ExposurePrefixes));
    }

    [Fact]
    public async Task A_stopped_traefik_on_the_host_routes_nothing()
    {
        HostTraefik("""{"Id":"edge1","Config":{"Cmd":["--providers.docker"]}}""", state: "exited");

        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n", """{"services":{"web":{"image":"nginx"}}}""");

        Assert.DoesNotContain(plan.Exposures(), e => e.Kind == ExposureKind.ExternalRoute);
    }

    [Fact]
    public async Task An_unreadable_tool_of_the_plan_refuses_the_plan()
    {
        _host.Images["myorg/traefik"] = (Tool, "{}");

        await Assert.ThrowsAsync<ComposePlanException>(() => PlanAsync("name: shop\nservices:\n  proxy:\n    image: myorg/traefik\n",
            """{"services":{"proxy":{"image":"myorg/traefik"}}}"""));
    }

    [Fact]
    public async Task The_re_check_finds_a_tunnel_the_container_runs_and_stops_it_under_the_agent_rule_set()
    {
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n", """{"services":{"web":{"image":"nginx"}}}""");
        _upCreates.Add(("web-new", "web", Container("web-new", "web", "\"Cmd\":[\"cloudflared\",\"tunnel\",\"run\"]")));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains(result.Violations, v => v.Contains(ComposeThirdParty.CloudflaredTunnel, StringComparison.Ordinal));
        Assert.Equal([("disable_restart", "web-new"), ("stop", "web-new")], _docker.Actions.Select(a => (a.Op, a.ContainerId)));
    }

    [Fact]
    public async Task A_decided_tunnel_passes_the_re_check()
    {
        _host.Images["cloudflare/cloudflared"] = (Tool, "{}");
        var plan = await PlanAsync(TunnelYaml, TunnelConfig);
        _upCreates.Add(("t-new", "tunnel", Container("t-new", "tunnel",
            "\"Image\":\"cloudflare/cloudflared\",\"Cmd\":[\"tunnel\",\"run\"],\"Env\":[\"TUNNEL_TOKEN=" + Hidden + "\"]")));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.True(result.Success, string.Join('\n', result.Violations) + result.Error + result.Refusal);
        Assert.Empty(_docker.Actions);
        Assert.DoesNotContain(Hidden, string.Join('\n', result.Violations) + result.Output);
    }
}
