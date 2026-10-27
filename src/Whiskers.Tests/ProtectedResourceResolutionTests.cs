using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// R1-07 (INV-24, INV-57, INV-59): a compose deployment is planned before anything is written — the project compose
/// really uses (<c>-p</c>, <c>COMPOSE_PROJECT_NAME</c>, top-level <c>name:</c>, the directory) and every existing
/// container it recreates, removes as an orphan or collides with by name — and each of those is decided, on every
/// surface. A plan that cannot be made refuses the deployment; execution runs exactly the planned project and files.
/// </summary>
public sealed class ProtectedResourceResolutionTests
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private readonly FakeHost _host = new();

    /// <summary>The protected database runs as project "payments" from /srv/payments, a directory nobody names.</summary>
    private readonly FakeDocker _docker = new(
        Compose("payments-web-1", "payments", "web", "/srv/payments"),
        Compose("payments-db", "payments", "db", "/srv/payments"),
        Compose("shop-api-1", "shop", "api", "/opt/deployments/shop"),
        new ContainerInfo { Id = "legacy-id", Name = "legacy-proxy", ServerId = "local" });

    private ComposePlanner Planner() => new(_docker, _host);

    // ---------------------------------------------------------------- project name

    [Fact]
    public async Task A_top_level_name_puts_the_deployment_into_that_project_wherever_it_runs_from()
    {
        // Deployed as "invoice" from the editor's directory, but the file says it is "payments".
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local",
            "/opt/deployments/invoice", "name: payments\nservices:\n  web:\n    image: nginx\n  db:\n    image: postgres\n"));

        Assert.Equal("payments", plan.ProjectName);
        Assert.Equal(["payments-web-1", "payments-db"], plan.Containers.Select(c => c.Name));
        Assert.All(plan.Impact, i => Assert.Equal(ComposeImpactKind.Recreate, i.Kind));
    }

    [Fact]
    public async Task A_deployment_from_another_directory_meets_the_project_it_names_not_the_directory()
    {
        _host.Files["/home/ci/payments-copy/compose.yaml"] = "services:\n  db:\n    image: postgres\n";
        _host.Files["/home/ci/payments-copy/.env"] = "COMPOSE_PROJECT_NAME=payments\n";

        var plan = await Planner().PlanAsync(ComposeDeployment.FromDirectory("local", "/home/ci/payments-copy"));

        Assert.Equal("payments", plan.ProjectName);
        Assert.Equal("payments-db", Assert.Single(plan.Containers).Name);
        Assert.Equal(["/home/ci/payments-copy/compose.yaml"], plan.Files);
    }

    [Fact]
    public async Task Without_a_name_the_directory_name_is_normalised_the_way_compose_does()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/Shop",
            "services:\n  api:\n    image: api\n"));

        Assert.Equal("shop", plan.ProjectName);
        Assert.Equal("shop-api-1", Assert.Single(plan.Containers).Name);
    }

    [Fact]
    public void Explicit_project_selection_wins_over_the_env_file_which_wins_over_the_name()
    {
        var env = new Dictionary<string, string?> { ["COMPOSE_PROJECT_NAME"] = "fromenv" };

        Assert.Equal("explicit", ComposeModel.EffectiveProjectName("explicit", env, "declared", "/srv/dir"));
        Assert.Equal("fromenv", ComposeModel.EffectiveProjectName(null, env, "declared", "/srv/dir"));
        Assert.Equal("declared", ComposeModel.EffectiveProjectName(null, new Dictionary<string, string?>(), "declared", "/srv/dir"));
        Assert.Equal("dir", ComposeModel.EffectiveProjectName(null, new Dictionary<string, string?>(), null, "/srv/dir"));
        Assert.Throws<ComposePlanException>(() =>
            ComposeModel.EffectiveProjectName(null, new Dictionary<string, string?>(), "Not Valid", "/srv/dir"));
    }

    [Fact]
    public async Task A_changed_env_file_keeps_the_project_and_files_the_container_was_started_with()
    {
        var db = (await _docker.ListContainersAsync(all: true, serverId: "local")).Single(c => c.Name == "payments-db");
        db.Labels["com.docker.compose.project.config_files"] = "/srv/payments/compose.yaml,/srv/payments/prod.yaml";
        _host.Files["/srv/payments/compose.yaml"] = "services:\n  web:\n    image: nginx\n  db:\n    image: postgres\n";
        _host.Files["/srv/payments/prod.yaml"] = "services:\n  db:\n    container_name: payments-db\n";
        _host.Files["/srv/payments/.env"] = "TAG=1\n";

        // The new .env even asks for another project: the change stays in the container's project (-p).
        var plan = await Planner().PlanEnvChangeAsync(db, "local", current =>
            DotEnvMerge.Merge(current, "TAG=2,COMPOSE_PROJECT_NAME=elsewhere").Content);

        Assert.Equal("payments", plan.ProjectName);
        Assert.Equal(["/srv/payments/compose.yaml", "/srv/payments/prod.yaml"], plan.Files);
        Assert.Equal(["payments-web-1", "payments-db"], plan.Containers.Select(c => c.Name));
        Assert.Contains("TAG=2", plan.EnvFileContent);
    }

    // ---------------------------------------------------------------- interpolation

    [Fact]
    public async Task Names_are_interpolated_from_the_env_file()
    {
        _host.Files["/opt/deployments/stack/.env"] = "STACK=payments\nexport PREFIX=\"legacy\" # comment\n";

        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/stack",
            "name: ${STACK}\nservices:\n  proxy:\n    image: nginx\n    container_name: ${PREFIX}-proxy\n  db:\n    image: postgres\n    container_name: ${DB_NAME:-payments-db}\n"));

        Assert.Equal("payments", plan.ProjectName);
        Assert.Equal(["legacy-proxy", "payments-db"], plan.Services.Select(s => s.ContainerName));
        Assert.Contains(plan.Impact, i => i.Container.Name == "legacy-proxy" && i.Kind == ComposeImpactKind.NameCollision);
        Assert.True(plan.HasEnvFile);
    }

    [Fact]
    public async Task A_name_from_a_variable_whiskers_cannot_see_refuses_the_plan()
    {
        // The value may come from the host's environment, which Whiskers does not know: no guess, no deployment.
        var ex = await Assert.ThrowsAsync<ComposePlanException>(() => Planner().PlanAsync(ComposeDeployment.FromContent(
            "local", "/opt/deployments/x", "services:\n  db:\n    image: postgres\n    container_name: ${UNSET}\n")));
        Assert.Contains("not set", ex.Message);
    }

    [Theory]
    [InlineData("${A}", "a")]
    [InlineData("${B:-dflt}", "dflt")]
    [InlineData("${EMPTY:-dflt}", "dflt")]
    [InlineData("${EMPTY-dflt}", "")]
    [InlineData("${A:+alt}", "alt")]
    [InlineData("$A-$$x", "a-$x")]
    public void Interpolation_follows_compose(string value, string expected)
    {
        var env = new Dictionary<string, string?> { ["A"] = "a", ["EMPTY"] = "" };
        Assert.Equal(expected, ComposeModel.Interpolate(value, env, "test"));
    }

    // ---------------------------------------------------------------- orphans and collisions

    [Fact]
    public async Task Orphans_are_part_of_the_plan_only_when_they_are_removed()
    {
        var file = "name: payments\nservices:\n  web:\n    image: nginx\n";
        var keep = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/srv/payments", file));
        var remove = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/srv/payments", file) with { RemoveOrphans = true });

        Assert.Equal(["payments-web-1"], keep.Containers.Select(c => c.Name));
        Assert.Contains(remove.Impact, i => i.Container.Name == "payments-db" && i.Kind == ComposeImpactKind.RemoveOrphan);
        Assert.EndsWith("up -d --remove-orphans", remove.UpCommand);
    }

    [Fact]
    public async Task A_container_of_another_project_with_a_wanted_name_is_a_collision()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/newdb",
            "services:\n  db:\n    image: postgres\n    container_name: payments-db\n  web:\n    image: nginx\n"));

        var hit = Assert.Single(plan.Impact);
        Assert.Equal(("payments-db", ComposeImpactKind.NameCollision), (hit.Container.Name, hit.Kind));
    }

    [Fact]
    public void A_default_container_name_already_taken_outside_the_project_is_a_collision()
    {
        var stray = new ContainerInfo { Id = "s", Name = "blog-web-1", ServerId = "local" };
        var impact = ComposeModel.Impact("blog", [new ComposeServicePlan("web", null)], false, [stray]);

        Assert.Equal(ComposeImpactKind.NameCollision, Assert.Single(impact).Kind);
    }

    // ---------------------------------------------------------------- fail closed

    [Theory]
    [InlineData("include:\n  - other.yaml\nservices:\n  a:\n    image: x\n")]
    [InlineData("services:\n  a:\n    extends:\n      file: base.yaml\n      service: a\n")]
    [InlineData("services: [broken")]
    [InlineData("name: x\n")]
    public async Task What_cannot_be_planned_refuses_the_deployment(string content) =>
        await Assert.ThrowsAsync<ComposePlanException>(() =>
            Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/x", content)));

    [Fact]
    public async Task An_unreadable_env_file_or_directory_refuses_the_deployment()
    {
        _host.Failing.Add("/opt/deployments/x/.env");
        await Assert.ThrowsAsync<ComposePlanException>(() => Planner().PlanAsync(
            ComposeDeployment.FromContent("local", "/opt/deployments/x", "services:\n  a:\n    image: x\n")));
        await Assert.ThrowsAsync<ComposePlanException>(() =>
            Planner().PlanAsync(ComposeDeployment.FromDirectory("local", "/srv/empty")));
    }

    // ---------------------------------------------------------------- same inputs

    [Fact]
    public async Task Execution_runs_exactly_the_planned_project_files_and_env()
    {
        _host.Files["/srv/app/docker-compose.yml"] = "services:\n  a:\n    image: x\n";
        _host.Files["/srv/app/compose.yaml"] = "name: app\nservices:\n  a:\n    image: x\n";
        _host.Files["/srv/app/compose.override.yml"] = "services:\n  a:\n    container_name: app-a\n";

        var plan = await Planner().PlanAsync(ComposeDeployment.FromDirectory("local", "/srv/app"));
        var script = plan.DeployScript(pull: true);

        Assert.Equal(["/srv/app/compose.yaml", "/srv/app/compose.override.yml"], plan.Files);
        Assert.Contains("docker compose -p 'app' --project-directory '/srv/app' -f '/srv/app/compose.yaml' -f '/srv/app/compose.override.yml' up -d", script);
        Assert.DoesNotContain("--env-file", script);   // there is no .env: compose has none to read either
        Assert.DoesNotContain("base64 -d", script);    // nothing is written: the files are the ones planned
    }

    [Fact]
    public async Task Written_content_is_the_planned_content_and_comes_before_up()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local", "/opt/deployments/shop",
            "services:\n  api:\n    image: api\n") with { EnvFileContent = "A=1\n" });
        var script = plan.DeployScript(pull: false);

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("services:\n  api:\n    image: api\n"));
        Assert.True(script.IndexOf(b64, StringComparison.Ordinal) < script.IndexOf(" up -d", StringComparison.Ordinal));
        Assert.Contains("--env-file '/opt/deployments/shop/.env'", script);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("A=1\n")), script);
    }

    // ---------------------------------------------------------------- decisions on every surface

    [Fact]
    public async Task The_ui_decides_every_planned_container_before_writing_anything()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources.Add("payments-db");
        var runner = UiRunner(policy);

        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.DeployAsync(_host, Planner(), "compose.deploy", new { projectName = "invoice", serverId = "local" }, Payments));

        Assert.DoesNotContain(_host.Executed, c => c.Contains("docker compose"));
    }

    [Fact]
    public async Task The_ui_runs_the_plan_once_every_container_is_allowed()
    {
        var runner = UiRunner(GuardrailPolicy.SafeDefault());

        var result = await runner.DeployAsync(_host, Planner(), "compose.deploy", new { projectName = "invoice", serverId = "local" }, Payments);

        Assert.True(result.Success);
        Assert.Contains(_host.Executed, c => c.EndsWith("-p 'payments' --project-directory '/opt/deployments/invoice' -f '/opt/deployments/invoice/docker-compose.yml' up -d 2>&1"));
    }

    [Fact]
    public async Task An_mcp_deployment_is_decided_on_the_containers_of_its_plan()
    {
        var content = "name: payments\nservices:\n  db:\n    image: postgres\n";
        var (refused, _) = await GatewayAsync("compose.deploy",
            new { serverId = "local", projectName = "invoice", composeContent = content }, protect: "payments-db");
        var (allowed, seen) = await GatewayAsync("compose.deploy",
            new { serverId = "local", projectName = "invoice", composeContent = content });

        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Equal(MutationStatus.Executed, allowed.Status);
        Assert.Contains("payments-db", seen!.Related);
    }

    [Fact]
    public async Task An_mcp_env_change_is_decided_on_every_container_of_the_project()
    {
        _host.Files["/srv/payments/compose.yaml"] = "services:\n  web:\n    image: nginx\n  db:\n    image: postgres\n";

        var (refused, _) = await GatewayAsync("container.env_set",
            new { containerId = "payments-web-1", envVars = "A=1", serverId = "local" }, protect: "payments-db");

        Assert.Equal(MutationStatus.Denied, refused.Status);
    }

    [Fact]
    public async Task A_deployment_whose_impact_cannot_be_determined_is_refused_before_policy()
    {
        var (outcome, _) = await GatewayAsync("compose.deploy",
            new { serverId = "local", projectName = "x", composeContent = "services:\n  a:\n    container_name: ${UNSET}\n" });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetNotResolved, outcome.ReasonCode);
    }

    [Fact]
    public async Task A_container_that_turns_up_after_the_decision_stops_the_execution()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local",
            "/opt/deployments/invoice", "name: payments\nservices:\n  db:\n    image: postgres\n"));

        // Decided only on the server, not on payments-db — as if the plan had changed between decision and execution.
        var request = new MutationRequest("compose.deploy", Admin, MutationSurface.Mcp,
            new MutationTarget("host", "local", "invoice", "local") { Resolved = true }, Args(new { serverId = "local" }));
        using (MutationScope.Begin("compose.deploy", "op", Admin, request))
            Assert.Throws<ComposePlanException>(() => plan.EnsureDecided(MutationScope.Current));
        Assert.Throws<ComposePlanException>(() => plan.EnsureDecided(null));

        var covering = request with { Target = request.Target with { Related = plan.DecidedIdentities() } };
        using (MutationScope.Begin("compose.deploy", "op2", Admin, covering))
            plan.EnsureDecided(MutationScope.Current);
    }

    [Fact]
    public async Task A_decision_on_another_server_does_not_cover_the_plan()
    {
        var plan = await Planner().PlanAsync(ComposeDeployment.FromContent("local",
            "/opt/deployments/invoice", "name: payments\nservices:\n  db:\n    image: postgres\n"));
        var request = new MutationRequest("compose.deploy", Admin, MutationSurface.Mcp,
            new MutationTarget("host", "edge01", null, "edge01") { Related = plan.DecidedIdentities() },
            Args(new { serverId = "edge01" }));

        using (MutationScope.Begin("compose.deploy", "op", Admin, request))
            Assert.Throws<ComposePlanException>(() => plan.EnsureDecided(MutationScope.Current));
    }

    [Fact]
    public async Task The_ui_plans_again_before_writing_and_stops_on_a_container_nobody_decided()
    {
        var runner = UiRunner(GuardrailPolicy.SafeDefault());
        var planner = new ChangingPlanner(Planner(), new ContainerInfo { Id = "late-id", Name = "late-arrival", ServerId = "local" });

        await Assert.ThrowsAsync<ComposePlanException>(() =>
            runner.DeployAsync(_host, planner, "compose.deploy", new { projectName = "invoice", serverId = "local" }, Payments));

        Assert.Equal(2, planner.Calls);
        Assert.DoesNotContain(_host.Executed, c => c.Contains("docker compose"));
    }

    [Fact]
    public async Task A_git_deployment_is_decided_on_its_plan_before_the_first_build()
    {
        var root = Path.Combine(Path.GetTempPath(), $"gitdeploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var git = new Whiskers.Services.GitDeploy.GitDeployService(_host, new NoVault(), Planner(),
                NullLogger<Whiskers.Services.GitDeploy.GitDeployService>.Instance, Path.Combine(root, "apps.json"));
            var app = await git.SaveAppAsync(new GitDeployApp
            {
                Id = "0123456789ab", Name = "payments", RepoUrl = "https://example.org/p.git", ServerId = "local",
            }, token: null);
            _host.Files[$"/opt/whiskers-git/{app.Id}/repo/docker-compose.yml"] =
                "name: payments\nservices:\n  web:\n    image: nginx\n";
            ComposePlan? gated = null;

            await Assert.ThrowsAsync<MutationRefusedException>(() => git.DeployAsync(app.Id, gate: (plan, _) =>
            {
                gated = plan;
                throw new MutationRefusedException("refused", MutationStatus.Denied, "op");
            }));

            // Orphans are removed by this deploy: the protected database the file no longer defines is in the plan.
            Assert.Contains(gated!.Impact, i => i.Container.Name == "payments-db" && i.Kind == ComposeImpactKind.RemoveOrphan);
            Assert.DoesNotContain(_host.Executed, c => c.Contains("docker compose"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Compose_switches_in_the_env_file_are_planned()
    {
        _host.Files["/srv/payments/compose.yml"] = "services:\n  web:\n    image: nginx\n";
        _host.Files["/srv/payments/compose.prod.yml"] = "services:\n  web:\n    container_name: legacy-proxy\n";
        _host.Files["/srv/payments/.env"] =
            "COMPOSE_PROJECT_NAME=payments\nCOMPOSE_FILE=compose.yml:compose.prod.yml\nCOMPOSE_REMOVE_ORPHANS=true\n";

        var plan = await Planner().PlanAsync(ComposeDeployment.FromDirectory("local", "/srv/payments"));

        Assert.Equal(["/srv/payments/compose.yml", "/srv/payments/compose.prod.yml"], plan.Files);
        Assert.True(plan.RemoveOrphans);
        Assert.EndsWith("up -d --remove-orphans", plan.UpCommand);
        Assert.Contains(plan.Impact, i => i.Container.Name == "payments-db" && i.Kind == ComposeImpactKind.RemoveOrphan);
        Assert.Contains(plan.Impact, i => i.Container.Name == "legacy-proxy" && i.Kind == ComposeImpactKind.NameCollision);
    }

    [Fact]
    public async Task An_env_change_that_switches_on_orphan_removal_plans_the_orphans()
    {
        var web = (await _docker.ListContainersAsync(all: true, serverId: "local")).Single(c => c.Name == "payments-web-1");
        _host.Files["/srv/payments/docker-compose.yml"] = "services:\n  web:\n    image: nginx\n";

        var plan = await Planner().PlanEnvChangeAsync(web, "local",
            current => DotEnvMerge.Merge(current, "COMPOSE_REMOVE_ORPHANS=1").Content);

        Assert.Contains(plan.Impact, i => i.Container.Name == "payments-db" && i.Kind == ComposeImpactKind.RemoveOrphan);
    }

    [Theory]
    [InlineData("COMPOSE_PROJECT_NAME=${STACK}\n")]
    [InlineData("COMPOSE_REMOVE_ORPHANS=maybe\n")]
    [InlineData("COMPOSE_FILE=${FILES}\n")]
    [InlineData("CERT=\"-----BEGIN\nMIIB\n-----END\"\n")]
    public async Task Env_file_settings_whiskers_cannot_read_refuse_the_plan(string env)
    {
        _host.Files["/srv/app/compose.yaml"] = "services:\n  a:\n    image: x\n";
        _host.Files["/srv/app/.env"] = env;

        await Assert.ThrowsAsync<ComposePlanException>(() =>
            Planner().PlanAsync(ComposeDeployment.FromDirectory("local", "/srv/app")));
    }

    // ---------------------------------------------------------------- helpers

    private static readonly ComposeDeployment Payments = ComposeDeployment.FromContent("local",
        "/opt/deployments/invoice", "name: payments\nservices:\n  db:\n    image: postgres\n");

    /// <summary>Plans like the real planner, but from the second plan on a new container shows up in the impact.</summary>
    private sealed class ChangingPlanner(IComposePlanner inner, ContainerInfo late) : IComposePlanner
    {
        public int Calls { get; private set; }

        public async Task<ComposePlan> PlanAsync(ComposeDeployment deployment, CancellationToken ct = default)
        {
            var plan = await inner.PlanAsync(deployment, ct);
            return ++Calls == 1 ? plan
                : plan with { Impact = [.. plan.Impact, new ComposeImpactEntry(late, ComposeImpactKind.NameCollision)] };
        }

        public Task<ComposePlan> PlanEnvChangeAsync(ContainerInfo container, string serverId,
            Func<string?, string> newEnv, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoVault : Whiskers.Services.Vault.IVaultService
    {
        public bool IsEnabled => false;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<Whiskers.Models.VaultEntry> ListSecrets() => [];
        public Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null,
            int? rotateAfterDays = null) => Task.CompletedTask;
        public string? GetSecret(string key) => null;
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        public List<Whiskers.Models.VaultEntry> GetExpiringSecrets() => [];
    }

    private static ContainerInfo Compose(string name, string project, string service, string dir) => new()
    {
        Id = name + "-id", Name = name, ServerId = "local", State = "running",
        Labels = new()
        {
            ["com.docker.compose.project"] = project,
            ["com.docker.compose.service"] = service,
            ["com.docker.compose.project.working_dir"] = dir,
        },
    };

    private static UiMutationRunner UiRunner(GuardrailPolicy policy)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            approvals: new ApprovalGate(new ApprovalStore(), store));
        return new UiMutationRunner(gateway, new CapabilityCatalog(), new AdminUser());
    }

    private sealed class AdminUser : Whiskers.Services.Auth.ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Admin);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(AppRole.Admin >= required);
    }

    private async Task<(MutationOutcome<int> Outcome, MutationTarget? Seen)> GatewayAsync(string capabilityId,
        object arguments, string? protect = null)
    {
        var catalog = new CapabilityCatalog();
        var capability = catalog.Find(capabilityId)!;
        var args = Args(arguments);
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        policy.ProtectedResources = protect is null ? [] : [protect];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var resolver = new MutationTargetResolver(new TestServices(_docker,
            new FakeServerConfig(new ServerConfig { Id = "local", Name = "Local", IsDefault = true }), Planner()));
        var gateway = new MutationGateway(catalog, AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            targets: resolver, approvals: new ApprovalGate(new ApprovalStore(), store));
        MutationTarget? seen = null;
        var outcome = await gateway.ExecuteAsync(
            new MutationRequest(capabilityId, Admin, MutationSurface.Mcp, MutationTargets.FromArguments(capability, args), args),
            (ctx, _) => { seen = ctx.Request.Target; return Task.FromResult(0); });
        return (outcome, seen);
    }

    private static Dictionary<string, JsonElement> Args(object arguments) =>
        JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    /// <summary>A host with a file system in memory: answers the planner's reads and records every other command.</summary>
    private sealed class FakeHost : IHostCommandExecutor
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);
        public List<string> Executed { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            if (Regex.Match(command, @"^if \[ -e '([^']*)' \]") is { Success: true } read)
            {
                var path = read.Groups[1].Value;
                if (Failing.Contains(path)) return Done("", 1);
                return Done(Files.TryGetValue(path, out var content)
                    ? Convert.ToBase64String(Encoding.UTF8.GetBytes(content))
                    : "__WHISKERS_ABSENT__");
            }
            if (Regex.Match(command, @"^cd '([^']*)' && for f in") is { Success: true } discover)
            {
                var dir = discover.Groups[1].Value;
                var found = new[] { ComposeModel.DefaultFiles, ComposeModel.OverrideFiles }
                    .Select(names => names.FirstOrDefault(n => Files.ContainsKey($"{dir}/{n}")))
                    .Where(n => n is not null);
                return Done(string.Join("\n", found));
            }
            if (Regex.Match(command, @"^test -f '([^']*)' && echo yes") is { Success: true } test)
                return Files.ContainsKey(test.Groups[1].Value) ? Done("yes") : Done("", 1);
            Executed.Add(command);
            return Done("ok");
        }

        private static Task<CommandResult> Done(string output, int exit = 0) =>
            Task.FromResult(new CommandResult { Output = output, ExitCode = exit });
    }
}
