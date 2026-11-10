using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Deployment;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0013, D3, D5, D8, D9, D15: MCP <c>deploy_app</c> is a compose deployment — planned by the gateway from its
/// arguments through the mapper (an exposure needs a confirmation even with admin autonomy), run through the compose
/// runner (an exposure that only the pulled image shows refuses an API key's run before <c>up</c>), refused for
/// injected decision arguments, a malformed entry, a Kubernetes server, a directory or name owned by another path; a
/// redeploy recreates; one gateway event per attempt; the output names the project, containers and pinned image id.
/// </summary>
public sealed class DeployAppComposeTests
{
    private const string Dir = "/opt/deployments/web";
    private const string Hidden = "tok-SECRET-value";
    private static readonly string NginxId = "sha256:" + new string('1', 64);
    private static readonly string PulledId = "sha256:" + new string('9', 64);

    private readonly ProtectedResourceResolutionTests.FakeHost _host = new();
    private readonly FakeDocker _docker = new() { AllowMutations = true };
    private readonly FakeServerConfig _servers = new(
        new ServerConfig { Id = "local", Name = "Local", IsDefault = true },
        new ServerConfig { Id = "k8s", Name = "Cluster", ConnectionType = ConnectionType.Kubernetes });
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly RecordingToolAudit _toolAudit = new();
    private readonly Dictionary<string, (string Id, string Labels)> _pulls = new(StringComparer.Ordinal);
    private bool _upCreates = true;
    private Whiskers.Modules.IModuleRegistry _modules = Modules(deploymentOn: true);

    private static Whiskers.Modules.IModuleRegistry Modules(bool deploymentOn) =>
        new Whiskers.Modules.ModuleRegistry([], [], deploymentOn ? ["core", DeploymentModuleGate.ModuleId] : ["core"]);

    public DeployAppComposeTests()
    {
        _host.Images["nginx"] = (NginxId, "{}");
        _host.ComposeConfig = Config();
        _host.Effect = command =>
        {
            if (command.Contains(" pull", StringComparison.Ordinal) && command.Contains("docker compose", StringComparison.Ordinal))
                foreach (var (image, result) in _pulls) _host.Images[image] = result;
            if (command.Contains(" up -d", StringComparison.Ordinal) && _upCreates)
            {
                var pinned = _host.PinnedImageId("web") ?? "";
                _docker.Add(new ContainerInfo
                {
                    Id = "web-new-0123456789ab", Name = "web", ServerId = "local", State = "running", Image = pinned,
                    Labels = new() { [ComposeModel.ProjectLabel] = "web", [ComposeModel.ServiceLabel] = "web" },
                    Ports = [new PortMapping { PublicPort = 8080, PrivatePort = 80, Type = "tcp" }],
                });
                _host.Inspect["web-new-0123456789ab"] = "{\"Id\":\"web-new-0123456789ab\",\"Name\":\"/web\",\"Image\":\"" + pinned +
                    "\",\"Config\":{\"Labels\":{\"com.docker.compose.project\":\"web\",\"com.docker.compose.service\":\"web\"}}," +
                    "\"HostConfig\":{\"NetworkMode\":\"bridge\"},\"Mounts\":[]}";
            }
            return null;
        };
    }

    /// <summary>What <c>docker compose config --format json</c> prints for the mapped file.</summary>
    private static string Config(string? environment = null) =>
        "{\"name\":\"web\",\"services\":{\"web\":{\"container_name\":\"web\",\"image\":\"nginx\",\"network_mode\":\"bridge\"," +
        "\"restart\":\"unless-stopped\"" + (environment is null ? "" : ",\"environment\":" + environment) + "}}}";

    // ---------------------------------------------------------------- harness

    private ComposePlanner Planner() => new(_docker, _host);
    private ComposeRunner Runner() => new(_host, _docker);

    private static IHttpContextAccessor Http()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer k1";
        return new HttpContextAccessor { HttpContext = context };
    }

    private static GuardrailPolicy AdminPolicy()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        return policy;
    }

    private MutationGateway Gateway(GuardrailPolicy policy)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        return new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance,
            targets: new MutationTargetResolver(new TestServices(_docker, _servers, Planner(), _host, _modules)),
            approvals: new ApprovalGate(new ApprovalStore(), store));
    }

    private static Dictionary<string, JsonElement> Args(object arguments) =>
        JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private Task<string> Tool(string serverId = "local", string? ports = null, string? envVars = null, string appName = "web") =>
        ContainerTools.DeployApp(Http(), new ToolKeys(), _docker, _host, Planner(), Runner(), _servers, _toolAudit, _modules,
            appName, "nginx", serverId, ports, envVars);

    /// <summary>A <c>deploy_app</c> call as <c>McpGuardrailFilter</c> makes it: the gateway decides, the tool runs inside.</summary>
    private async Task<(MutationOutcome<string> Outcome, bool Ran)> CallAsync(object arguments, Func<Task<string>> tool,
        MutationSurface surface = MutationSurface.Mcp, GuardrailPolicy? policy = null)
    {
        var args = Args(arguments);
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin);
        var ran = false;
        var outcome = await Gateway(policy ?? AdminPolicy()).ExecuteAsync(
            new MutationRequest("app.deploy", principal, surface,
                MutationTargets.FromArguments(new CapabilityCatalog().Find("app.deploy")!, args), args),
            async (_, _) => { ran = true; return await tool(); });
        return (outcome, ran);
    }

    private int Events => _audit.Events.Count(e => e.StartsWith("begin:", StringComparison.Ordinal)
                                                  || e.StartsWith("denied:", StringComparison.Ordinal));

    private bool Started => _host.Executed.Any(c => c.Contains(" up -d", StringComparison.Ordinal));

    // ---------------------------------------------------------------- exposures are decided

    [Fact]
    public async Task A_funnel_label_in_the_image_needs_a_confirmation_even_with_admin_autonomy()
    {
        _host.Images["nginx"] = (NginxId, """{"docktail.funnel.enable":"true"}""");

        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local" }, () => Tool());

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.False(ran);
        Assert.False(Started);
        Assert.Equal(1, Events);
    }

    [Fact]
    public async Task A_tunnel_token_makes_it_a_cloudflare_tunnel_that_needs_a_confirmation_and_shows_no_value()
    {
        _host.ComposeConfig = Config("{\"TUNNEL_TOKEN\":\"" + Hidden + "\"}");

        var (outcome, ran) = await CallAsync(
            new { appName = "web", image = "nginx", serverId = "local", envVars = "TUNNEL_TOKEN=" + Hidden }, () => Tool());

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.False(ran);
        Assert.Contains("Cloudflare tunnel", outcome.Reason);
        Assert.DoesNotContain(Hidden, outcome.Reason);
    }

    [Fact]
    public async Task An_api_key_run_whose_pulled_image_brings_a_funnel_label_is_refused_before_up()
    {
        _pulls["nginx"] = (PulledId, """{"docktail.funnel.enable":"true"}""");

        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local" }, () => Tool());

        Assert.True(ran);
        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.StartsWith("Deployment refused:", outcome.Value);
        Assert.Contains("docktail.funnel.enable", outcome.Value);
        Assert.False(Started);
        Assert.Equal(1, Events);
        Assert.Single(_toolAudit.Reports);
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [InlineData("exposure")]
    [InlineData("exposureWithheld")]
    [InlineData("related")]
    [InlineData("mounts")]
    public async Task An_injected_decision_argument_is_refused_before_anything_runs(string name)
    {
        var arguments = new Dictionary<string, string> { ["appName"] = "web", ["image"] = "nginx", ["serverId"] = "local", [name] = "x" };

        foreach (var surface in new[] { MutationSurface.Mcp, MutationSurface.Agent })
        {
            var (outcome, ran) = await CallAsync(arguments, () => Tool(), surface);

            Assert.False(ran);
            Assert.NotEqual(MutationStatus.Executed, outcome.Status);
            Assert.Contains($"'{name}'", outcome.Reason);
        }
        Assert.Empty(_host.Executed);
    }

    [Fact]
    public async Task A_malformed_port_is_refused_not_dropped_by_the_gateway_and_by_the_tool()
    {
        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local", ports = "8080:80,9000" },
            () => Tool(ports: "8080:80,9000"));

        Assert.False(ran);
        Assert.Contains("Port mapping #2", outcome.Reason);
        Assert.Equal(1, Events);

        var direct = await Tool(ports: "8080:80,9000");
        Assert.StartsWith("Deployment refused: Port mapping #2", direct);
        Assert.Empty(_host.Executed);
        Assert.Single(_toolAudit.Reports);
        Assert.False(_toolAudit.Reports[0].Success);
    }

    [Fact]
    public async Task A_malformed_environment_entry_is_refused_without_showing_it()
    {
        var direct = await Tool(envVars: "A=1," + Hidden);

        Assert.StartsWith("Deployment refused: Environment entry #2", direct);
        Assert.DoesNotContain(Hidden, direct);
        Assert.DoesNotContain(Hidden, _toolAudit.Reports[0].Details);
    }

    [Fact]
    public async Task A_kubernetes_server_is_refused()
    {
        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "k8s" }, () => Tool("k8s"));

        Assert.False(ran);
        Assert.Contains("deploy_app needs a Docker host", outcome.Reason);
        Assert.Contains("deploy_app needs a Docker host", await Tool("k8s"));
    }

    // ---------------------------------------------------------------- the Deployment module gates deploy_app

    [Fact]
    public async Task With_the_deployment_module_off_deploy_app_is_refused_with_the_switch_and_nothing_runs()
    {
        _modules = Modules(deploymentOn: false);

        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local" }, () => Tool());
        var direct = await Tool();

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("Deployment refused: the Deployment module is disabled", outcome.Reason);
        Assert.Contains("Features:deployment:Enabled=true", outcome.Reason);
        Assert.Contains("Deployment refused: the Deployment module is disabled", direct);
        Assert.Empty(_host.Executed);          // nothing written, pulled or started — not even planned
        Assert.Empty(_host.ConfigCommands);
        Assert.Single(_toolAudit.Reports);     // the direct call's one report
        Assert.False(_toolAudit.Reports[0].Success);
    }

    [Fact]
    public async Task With_the_deployment_module_off_the_ui_cannot_deploy_an_app_either()
    {
        _modules = Modules(deploymentOn: false);

        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local" }, () => Tool(),
            MutationSurface.Ui);

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("the Deployment module is disabled", outcome.Reason);
    }

    [Fact]
    public async Task With_the_deployment_module_on_deploy_app_is_planned_and_run()
    {
        _modules = Modules(deploymentOn: true);

        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local" }, () => Tool());

        Assert.True(ran);
        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.NotEmpty(_host.ConfigCommands);   // planned
        Assert.True(Started);                    // and run
    }

    [Fact]
    public async Task A_directory_another_path_wrote_is_refused()
    {
        _host.Files[$"{Dir}/docker-compose.yml"] = "services:\n  web:\n    image: nginx\n";

        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local" }, () => Tool());

        Assert.False(ran);
        Assert.Contains("another path owns it", outcome.Reason);
        Assert.Contains("compose editor", outcome.Reason);
    }

    [Fact]
    public async Task A_project_of_that_name_in_another_directory_is_refused()
    {
        _docker.Add(new ContainerInfo
        {
            Id = "other-web", Name = "other", ServerId = "local", State = "running",
            Labels = new() { [ComposeModel.ProjectLabel] = "web", [ComposeModel.ServiceLabel] = "x",
                ["com.docker.compose.project.working_dir"] = "/srv/web" },
        });

        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local" }, () => Tool());

        Assert.False(ran);
        Assert.Contains("already runs from another directory", outcome.Reason);
    }

    [Fact]
    public async Task A_legacy_container_with_the_name_is_refused_with_the_ways_out()
    {
        _docker.Add(new ContainerInfo { Id = "legacy-id", Name = "web", ServerId = "local", State = "running" });

        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local" }, () => Tool());

        Assert.False(ran);
        Assert.Contains("Container 'web'", outcome.Reason);
        Assert.Contains("Remove it on the container page, or choose another name", outcome.Reason);
        Assert.Empty(_host.Executed);
    }

    // ---------------------------------------------------------------- runs

    [Fact]
    public async Task A_deployment_runs_through_the_compose_runner_and_reports_project_containers_and_pinned_id()
    {
        var (outcome, ran) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local", ports = "8080:80" },
            () => Tool(ports: "8080:80"));

        Assert.True(ran);
        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Contains("App 'web' deployed as compose project 'web' on local", outcome.Value);
        Assert.Contains("Container: web (web-new-0123", outcome.Value);
        Assert.Contains("Image: nginx", outcome.Value);
        Assert.Contains($"Pinned to image id: {NginxId}", outcome.Value);
        Assert.Contains("Ports: 8080→80/tcp", outcome.Value);
        Assert.DoesNotContain("Container ID:", outcome.Value);
        // The file written is the mapper's, with its marker, in the project's directory.
        var marker = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(DeploymentCompose.Marker))[..20];
        Assert.Contains(_host.Executed, c => c.Contains(marker, StringComparison.Ordinal));
        Assert.Contains(_host.Executed, c => c.Contains(" up -d --pull never --no-build", StringComparison.Ordinal));
        Assert.Equal(1, Events);
        Assert.Single(_toolAudit.Reports);
        Assert.True(_toolAudit.Reports[0].Success);
    }

    [Fact]
    public async Task A_redeploy_with_the_same_name_recreates_the_project()
    {
        _host.Files[$"{Dir}/docker-compose.yml"] = DeploymentCompose.Marker + "\nname: \"web\"\n";
        _docker.Add(new ContainerInfo
        {
            Id = "web-old", Name = "web", ServerId = "local", State = "running",
            Labels = new() { [ComposeModel.ProjectLabel] = "web", [ComposeModel.ServiceLabel] = "web",
                ["com.docker.compose.project.working_dir"] = Dir },
        });
        MutationTarget? seen = null;
        var args = Args(new { appName = "web", image = "nginx", serverId = "local" });
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin);

        var outcome = await Gateway(AdminPolicy()).ExecuteAsync(
            new MutationRequest("app.deploy", principal, MutationSurface.Mcp,
                MutationTargets.FromArguments(new CapabilityCatalog().Find("app.deploy")!, args), args),
            async (ctx, _) => { seen = ctx.Request.Target; return await Tool(); });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Contains("web-old", seen!.Related);
        Assert.True(Started);
    }

    [Fact]
    public async Task A_failed_up_is_one_failed_event()
    {
        _host.Effect = command => command.Contains(" up -d", StringComparison.Ordinal)
            ? new Whiskers.Services.Server.CommandResult { ExitCode = 1, Error = "boom" }
            : null;

        var (outcome, _) = await CallAsync(new { appName = "web", image = "nginx", serverId = "local" }, () => Tool());

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.StartsWith("Deployment failed", outcome.Value);
        Assert.Equal(1, Events);
    }

    // ---------------------------------------------------------------- doubles

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

    private sealed class RecordingToolAudit : IToolAuditLog
    {
        public List<(string Action, string? Details, bool Success)> Reports { get; } = [];

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            Reports.Add((action, details, success));
            return Task.CompletedTask;
        }
    }
}
