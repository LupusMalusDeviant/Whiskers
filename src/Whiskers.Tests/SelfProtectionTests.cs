using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>A gateway on the real target resolver and policy, over one server that runs Whiskers' control plane next
/// to an ordinary app (R1-16). The active policy protects nothing of its own: what holds, holds by default.</summary>
internal sealed class ProtectionHarness
{
    public static readonly ServerConfig Local = new() { Id = "local", Name = "Local", IsDefault = true };

    private static ServerConfig Server(string name) => new() { Id = "local", Name = name, IsDefault = true };

    public static readonly MutationPrincipal UiAdmin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);
    public static readonly MutationPrincipal UiOperator =
        new(MutationPrincipalKind.Human, "user:op@example.com", "op@example.com", McpPermissionLevels.Write);
    public static readonly MutationPrincipal AdminKey =
        new(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin, CredentialId: "k1");

    public ContainerInfo[] Containers { get; } =
    [
        new() { Id = "c0ffee123456whiskers", Name = "whiskers", ServerId = "local", State = "running" },
        new() { Id = "d0d0d0123456db", Name = "whiskers-db", ServerId = "local", State = "running" },
        new() { Id = "eeee11223344proxy", Name = "nginx-proxy-manager", ServerId = "local", State = "running" },
        new() { Id = "ffff55667788tunnel", Name = "ghostunnel", ServerId = "local", State = "running" },
        new() { Id = "aaaa99887766web", Name = "web", ServerId = "local", State = "running" },
        new() { Id = "9a9a12121212bill", Name = "billing", ServerId = "local", State = "running" },
        new() { Id = "bbbb12341234sock", Name = "socket-proxy", ServerId = "local", State = "running" },
        new() { Id = "cccc56785678ca", Name = "step-ca", ServerId = "local", State = "running" },
    ];

    public FakeDocker Docker { get; }

    public GuardrailPolicy Policy { get; }
    public ApprovalStore Approvals { get; }
    public AgentToolTestHelpers.NullMutationAudit Audit { get; } = new();

    /// <summary>Backups of Whiskers' data volume (plain and of a compose project) on "local" (contract 0012, D5: a
    /// backup is resolved by its id).</summary>
    public StoredVolumeBackups Backups { get; } = new(
        new VolumeBackupEntity { BackupId = "bk-data", VolumeName = "serverwatch-data", ServerId = "local", FileName = "d.tar.gz" },
        new VolumeBackupEntity { BackupId = "bk-prod", VolumeName = "prod_serverwatch-data", ServerId = "local", FileName = "p.tar.gz" });
    public MutationGateway Gateway { get; }
    public int Runs { get; private set; }

    public ProtectionHarness(Action<GuardrailPolicy>? configure = null, Func<FakeDocker, IComposePlanner>? planner = null,
        string serverName = "Local", Whiskers.Services.Server.IHostCommandExecutor? host = null)
    {
        Docker = new FakeDocker(Containers) { DefaultServerId = "local" };
        Policy = GuardrailPolicy.SafeDefault();
        Policy.ProtectedResources = [];
        // The preset's forbidden arguments (rm -rf, DROP …) would refuse some cases for agents on their own.
        Policy.ForbiddenArgPatterns = [];
        Policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        Policy.RequireConfirmationForWrites = false;
        configure?.Invoke(Policy);
        var store = new AgentToolTestHelpers.FixedGuardrailStore(Policy);
        Approvals = new ApprovalStore(policy: store);
        Gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), Audit, [],
            NullLogger<MutationGateway>.Instance,
            targets: new MutationTargetResolver(planner is null
                ? new TestServices(Docker, new FakeServerConfig(Server(serverName)), Backups)
                : host is null
                    ? new TestServices(Docker, new FakeServerConfig(Server(serverName)), planner(Docker), Backups)
                    : new TestServices(Docker, new FakeServerConfig(Server(serverName)), planner(Docker), host, Backups)),
            approvals: new ApprovalGate(Approvals, store));
    }

    public static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    public static MutationPrincipal ChatAgent(string level = McpPermissionLevels.Admin) => MutationPrincipals.Agent(
        new AgentPrincipal(AgentPrincipalKind.WebUser, "admin", level, null, UserEmail: "admin@example.com"), "chat-1");

    public static MutationPrincipal InstructedAgent() => MutationPrincipals.Agent(
        new AgentPrincipal(AgentPrincipalKind.McpKey, "ci", McpPermissionLevels.Admin, null, McpKeyId: "k1"), "mcp-1");

    public static MutationPrincipal AdminsTask() =>
        new(MutationPrincipalKind.Automation, "task:t1", "nightly", McpPermissionLevels.Admin, OnBehalfOf: UiAdmin);

    /// <summary>Runs <paramref name="capabilityId"/> as <paramref name="who"/> on <paramref name="surface"/>.</summary>
    public async Task<MutationOutcome<int>> RunAsync(string capabilityId, object arguments, MutationPrincipal who,
        MutationSurface surface, AgentOrigin? origin = null)
    {
        var catalog = new CapabilityCatalog();
        var capability = catalog.Find(capabilityId)!;
        var args = Args(arguments);
        var request = new MutationRequest(capabilityId, who, surface, MutationTargets.FromArguments(capability, args), args)
        {
            AgentOrigin = origin,
        };
        return await Gateway.ExecuteAsync(request, (_, _) => Task.FromResult(++Runs));
    }
}

/// <summary>
/// R1-16 (INV-59): Whiskers' own control plane — its container, its database, its data volume, the proxy in front of
/// it and the mTLS material — is protected on every surface, even under a policy that protects nothing. The one
/// exception is an interactive Admin (web UI or web chat): the action then needs an explicit Admin approval, is bound
/// to that call and audited. A key, an agent instructed over MCP, an automation and an Operator are refused.
/// </summary>
public class SelfProtectionTests
{
    public static TheoryData<string, string> ControlPlane => new()
    {
        { "container.restart", """{"containerId":"whiskers","serverId":"local"}""" },               // the app
        { "container.restart", """{"containerId":"c0ffee","serverId":"local"}""" },                 // by a unique id prefix
        { "db.query", """{"containerId":"whiskers-db","serverId":"local","query":"select 1"}""" },  // the database
        { "volume.backup_delete", """{"volumeName":"serverwatch-data","backupId":"bk-data","serverId":"local"}""" },     // the data volume
        { "volume.backup_delete", """{"volumeName":"prod_serverwatch-data","backupId":"bk-prod","serverId":"local"}""" }, // … of a compose project
        { "container.stop", """{"containerId":"nginx-proxy-manager","serverId":"local"}""" },       // the proxy
        { "container.restart", """{"containerId":"ghostunnel","serverId":"local"}""" },             // the mTLS tunnel
        { "container.stop", """{"containerId":"socket-proxy","serverId":"local"}""" },             // the socket proxy
        { "container.restart", """{"containerId":"step-ca","serverId":"local"}""" },                // the CA
        { "app.deploy", """{"serverId":"local","appName":"x","image":"alpine","volumes":"/var/run/docker.sock:/s"}""" },
        { "certificate.renew_mtls", """{"serverId":"local"}""" },                                   // re-keys a tunnel
        { "host.exec", """{"serverId":"local","command":"cat /app/data/mtls/client.key"}""" },      // mTLS material
        { "host.exec", """{"serverId":"local","command":"rm -rf /opt/dockerproxy"}""" },            // a host's tunnel keys
        { "host.exec", """{"serverId":"local","command":"rm -rf /opt/step-ca/secrets"}""" },        // the CA's keys
        { "host.exec", """{"serverId":"local","command":"docker stop whiskers"}""" },               // named in a command
        { "host.exec", """{"serverId":"local","command":"docker rm -f c0ffee123456"}""" },          // by id in a command
        { "db.query", """{"containerId":"web","serverId":"local","query":"DROP DATABASE whiskers"}""" },
        { "compose.deploy", """{"serverId":"local","yaml":"services:\n  x:\n    volumes:\n      - serverwatch-data:/d"}""" },
        // a local volume that binds a host path through driver_opts
        { "compose.deploy", """{"serverId":"local","yaml":"services:\n  x:\n    volumes:\n      - host:/h\nvolumes:\n  host:\n    driver: local\n    driver_opts:\n      type: none\n      o: bind\n      device: /opt/dockerproxy"}""" },
    };

    private static object Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>Contract 0013, D5: an <c>app.deploy</c> off the UI is planned before the policy, so its harness has a
    /// planner and a host to plan on; every other case needs neither.</summary>
    private static ProtectionHarness HarnessFor(string capability)
    {
        if (capability != "app.deploy") return new ProtectionHarness();
        var host = new ProtectedResourceResolutionTests.FakeHost();
        return new ProtectionHarness(planner: docker => new ComposePlanner(docker, host), host: host);
    }

    [Theory]
    [MemberData(nameof(ControlPlane))]
    public async Task Keys_agents_over_MCP_automations_and_operators_are_refused(string capability, string arguments)
    {
        var h = HarnessFor(capability);
        var args = Parse(arguments);

        var outcomes = new[]
        {
            await h.RunAsync(capability, args, ProtectionHarness.AdminKey, MutationSurface.Mcp),
            await h.RunAsync(capability, args, ProtectionHarness.InstructedAgent(), MutationSurface.Agent, AgentOrigin.McpTool),
            await h.RunAsync(capability, args, ProtectionHarness.AdminsTask(), MutationSurface.Automation),
            await h.RunAsync(capability, args, ProtectionHarness.AdminsTask(), MutationSurface.Webhook),
            await h.RunAsync(capability, args, ProtectionHarness.UiOperator, MutationSurface.Ui),
            await h.RunAsync(capability, args, ProtectionHarness.ChatAgent(McpPermissionLevels.Write), MutationSurface.Agent,
                AgentOrigin.WebUi),
        };

        Assert.All(outcomes, o => Assert.Equal(MutationStatus.Denied, o.Status));
        Assert.Equal(0, h.Runs);
        Assert.Empty(h.Approvals.GetPending());
    }

    [Theory]
    [MemberData(nameof(ControlPlane))]
    public async Task An_interactive_Admin_needs_an_explicit_Admin_approval(string capability, string arguments)
    {
        var h = HarnessFor(capability);
        var args = Parse(arguments);

        var ui = await h.RunAsync(capability, args, ProtectionHarness.UiAdmin, MutationSurface.Ui);
        var chat = await h.RunAsync(capability, args, ProtectionHarness.ChatAgent(), MutationSurface.Agent, AgentOrigin.WebUi);

        Assert.Equal(MutationStatus.PendingApproval, ui.Status);
        Assert.Equal(MutationStatus.PendingApproval, chat.Status);
        Assert.All(h.Approvals.GetPending(), a => Assert.Equal(McpPermissionLevels.Admin, a.Level));
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public async Task The_approved_exception_runs_once_and_is_audited_with_its_approval()
    {
        var h = new ProtectionHarness();
        var args = new { containerId = "whiskers", serverId = "local" };

        var asked = await h.RunAsync("container.restart", args, ProtectionHarness.UiAdmin, MutationSurface.Ui);
        // An Operator cannot approve it; an Admin can (here the Admin who asked — an interactive request, PC-D4).
        Assert.Equal(ApprovalDecisionResult.InsufficientLevel, await h.Approvals.DecideAsync(asked.ApprovalId!, true,
            ApprovalResolver.User("op@example.com", AppRole.Operator)));
        Assert.Equal(ApprovalDecisionResult.Decided, await h.Approvals.DecideAsync(asked.ApprovalId!, true,
            ApprovalResolver.User("admin@example.com", AppRole.Admin)));

        var ran = await h.RunAsync("container.restart", args, ProtectionHarness.UiAdmin, MutationSurface.Ui);
        var again = await h.RunAsync("container.restart", args, ProtectionHarness.UiAdmin, MutationSurface.Ui);

        Assert.Equal(MutationStatus.Executed, ran.Status);
        Assert.Equal(1, h.Runs);
        Assert.Contains($"begin:container.restart:{asked.ApprovalId}", h.Audit.Events);
        Assert.Equal(MutationStatus.PendingApproval, again.Status);   // single use
    }

    [Fact]
    public async Task A_refusal_and_the_approval_card_never_carry_the_payload_a_person_typed()
    {
        var h = new ProtectionHarness();
        var query = "ALTER ROLE whiskers PASSWORD 'not-a-real-one'";

        var denied = await h.RunAsync("db.query", new { containerId = "web", serverId = "local", query },
            ProtectionHarness.AdminKey, MutationSurface.Mcp);
        var asked = await h.RunAsync("db.query", new { containerId = "web", serverId = "local", query },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);

        Assert.DoesNotContain("not-a-real-one", denied.Reason);
        Assert.DoesNotContain("whiskers", denied.Reason!.Replace("whiskers*", ""));
        var card = Assert.Single(h.Approvals.GetPending());
        Assert.DoesNotContain("not-a-real-one", card.ParamsJson);
        Assert.Contains("withheld", card.ParamsJson);
    }

    [Fact]
    public async Task A_whole_payload_matching_a_wildcard_pattern_is_not_quoted_on_the_card_or_in_the_reason()
    {
        var h = new ProtectionHarness(p => p.ProtectedResources = ["*role*"]);
        var query = "ALTER ROLE app PASSWORD 'not-a-real-one'";

        var denied = await h.RunAsync("db.query", new { containerId = "web", serverId = "local", query },
            ProtectionHarness.AdminKey, MutationSurface.Mcp);
        // A pattern of the policy alone is refused in the UI too (no approval card).
        var inUi = await h.RunAsync("db.query", new { containerId = "web", serverId = "local", query },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);

        Assert.DoesNotContain("not-a-real-one", denied.Reason);
        Assert.Equal(MutationStatus.Denied, inUi.Status);
        Assert.DoesNotContain("not-a-real-one", inUi.Reason);
        Assert.Empty(h.Approvals.GetPending());
    }

    [Fact]
    public async Task Restoring_whiskers_own_data_needs_an_Admin_approval()
    {
        var h = new ProtectionHarness();
        var outcome = await h.RunAsync("selfbackup.restore", new { archive = "whiskers-backup.tar.gz" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.Equal(McpPermissionLevels.Admin, Assert.Single(h.Approvals.GetPending()).Level);
    }

    [Fact]
    public async Task An_id_prefix_sent_straight_to_the_Docker_API_is_looked_up()
    {
        var h = new ProtectionHarness();
        Assert.Equal(MutationStatus.Denied, (await h.RunAsync("host.exec",
            new { serverId = "local", command = "curl -X POST http://172.18.0.5:2375/containers/c0ffee/stop" },
            ProtectionHarness.AdminKey, MutationSurface.Mcp)).Status);
    }

    [Fact]
    public async Task A_second_approver_policy_makes_the_exception_four_eyes()
    {
        var h = new ProtectionHarness(p => p.RequireSecondApprover = true);
        var asked = await h.RunAsync("container.restart", new { containerId = "whiskers", serverId = "local" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);

        Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, await h.Approvals.DecideAsync(asked.ApprovalId!, true,
            ApprovalResolver.User("admin@example.com", AppRole.Admin)));
    }

    [Theory]
    [InlineData("container.restart", """{"containerId":"web","serverId":"local"}""")]
    [InlineData("host.exec", """{"serverId":"local","command":"df -h /var/lib/docker"}""")]
    [InlineData("db.query", """{"containerId":"web","serverId":"local","query":"select * from orders"}""")]
    public async Task Everything_else_runs_unhindered_on_every_surface(string capability, string arguments)
    {
        var h = new ProtectionHarness();
        var args = Parse(arguments);

        Assert.Equal(MutationStatus.Executed, (await h.RunAsync(capability, args, ProtectionHarness.AdminKey, MutationSurface.Mcp)).Status);
        Assert.Equal(MutationStatus.Executed, (await h.RunAsync(capability, args, ProtectionHarness.UiAdmin, MutationSurface.Ui)).Status);
        Assert.Equal(MutationStatus.Executed,
            (await h.RunAsync(capability, args, ProtectionHarness.AdminsTask(), MutationSurface.Automation)).Status);
    }

    [Fact]
    public async Task An_operator_and_an_admin_run_ordinary_actions()
    {
        var h = new ProtectionHarness();
        Assert.Equal(MutationStatus.Executed, (await h.RunAsync("db.query",
            new { containerId = "web", serverId = "local", query = "update orders set state = 'paid' where id = 7" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui)).Status);
        Assert.Equal(MutationStatus.Executed, (await h.RunAsync("container.restart",
            new { containerId = "web", serverId = "local" }, ProtectionHarness.UiOperator, MutationSurface.Ui)).Status);
    }

    [Fact]
    public async Task A_server_or_other_name_sharing_the_prefix_is_not_the_control_plane()
    {
        var h = new ProtectionHarness(serverName: "whiskers-vps");
        Assert.Equal(MutationStatus.Executed, (await h.RunAsync("host.exec",
            new { serverId = "local", command = "df -h" }, ProtectionHarness.AdminKey, MutationSurface.Mcp)).Status);
    }

    [Fact]
    public async Task A_policy_pattern_equal_to_a_control_plane_one_still_refuses_what_only_the_policy_covers()
    {
        // whiskers* in the policy protects the server whiskers-vps too; the control plane's names do not reach a
        // server, so that hit is the policy's alone: refused, no approval.
        var h = new ProtectionHarness(p => p.ProtectedResources = ["whiskers*"], serverName: "whiskers-vps");
        var outcome = await h.RunAsync("host.exec", new { serverId = "local", command = "df -h" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(h.Approvals.GetPending());

        // The control plane named in the same call does not hide a hit only the policy explains.
        var both = await h.RunAsync("container.restart", new { serverId = "local", containerId = "whiskers", note = "whiskers-vps" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, both.Status);
        Assert.Empty(h.Approvals.GetPending());

        // Free text that cannot be analysed could name what only the policy covers: refused, no approval.
        foreach (var command in new[] { "eval \"$CMD\"", "echo whiskers serverwatch; eval \"$CMD\"" })
        {
            var unanalysable = await h.RunAsync("host.exec", new { serverId = "local", command },
                ProtectionHarness.UiAdmin, MutationSurface.Ui);
            Assert.Equal(MutationStatus.Denied, unanalysable.Status);
        }
        var sql = await h.RunAsync("db.query",
            new { containerId = "whiskers-db", serverId = "local", query = "DROP TABLE whiskers; EXECUTE 'DROP ' || t;" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, sql.Status);
        Assert.Empty(h.Approvals.GetPending());

        // The same pattern on the Whiskers container is the control plane: an Admin may approve it.
        var container = await h.RunAsync("container.restart", new { containerId = "whiskers", serverId = "local" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.PendingApproval, container.Status);
    }

    [Fact]
    public async Task A_callers_own_resolved_keys_do_not_reach_the_rules()
    {
        var h = new ProtectionHarness();
        // Pretending the target is a server would take the container's name out of the control plane's reach.
        var outcome = await h.RunAsync("container.restart",
            new { containerId = "whiskers", serverId = "local", __resolvedKind = "server", __resolvedTargetName = "x" },
            ProtectionHarness.AdminKey, MutationSurface.Mcp);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Fact]
    public async Task A_short_number_in_a_docker_command_is_not_taken_for_an_id()
    {
        var h = new ProtectionHarness();
        h.Containers[0].Id = "100abc0000whiskers";   // the app's id starts with 100
        Assert.Equal(MutationStatus.Executed, (await h.RunAsync("host.exec",
            new { serverId = "local", command = "docker logs --tail 100 web" }, ProtectionHarness.AdminKey,
            MutationSurface.Mcp)).Status);
    }

    [Fact]
    public async Task An_all_digit_id_prefix_in_a_docker_command_is_looked_up()
    {
        var h = new ProtectionHarness();
        h.Containers[0].Id = "71abc0000whiskers";
        Assert.Equal(MutationStatus.Denied, (await h.RunAsync("host.exec",
            new { serverId = "local", command = "docker rm -f 71" }, ProtectionHarness.AdminKey, MutationSurface.Mcp)).Status);
    }

    [Fact]
    public async Task An_agent_instructed_over_mcp_with_a_session_cookie_raises_no_approval()
    {
        var h = new ProtectionHarness();
        var outcome = await h.RunAsync("container.restart", new { containerId = "whiskers", serverId = "local" },
            ProtectionHarness.ChatAgent(), MutationSurface.Agent, AgentOrigin.McpTool);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(h.Approvals.GetPending());
    }

    [Fact]
    public async Task A_two_digit_id_prefix_in_a_docker_command_is_looked_up()
    {
        var h = new ProtectionHarness();
        Assert.Equal(MutationStatus.Denied, (await h.RunAsync("host.exec",
            new { serverId = "local", command = "docker stop c0" }, ProtectionHarness.AdminKey, MutationSurface.Mcp)).Status);
    }

    [Fact]
    public async Task The_nginx_editor_hands_its_file_to_the_check()
    {
        var h = new ProtectionHarness();
        var outcome = await h.RunAsync("nginx.update",
            new { serverId = "local", siteName = "default", content = "location / { proxy_pass http://whiskers:8080; }" },
            ProtectionHarness.AdminKey, MutationSurface.Mcp);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Fact]
    public async Task A_pending_control_plane_action_says_an_Admin_must_approve_it()
    {
        var h = new ProtectionHarness();
        var outcome = await h.RunAsync("container.restart", new { containerId = "whiskers", serverId = "local" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationReasonCodes.ProtectedNeedsAdminApproval, outcome.ReasonCode);
        Assert.Equal([outcome.ApprovalId!], outcome.ReasonArgs);
    }

    [Fact]
    public async Task Reading_the_control_plane_stays_possible()
    {
        // A read changes nothing; the control plane is protected from changes only.
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        var engine = Whiskers.Services.Guardrails.GuardrailEngine.CreateDefault();
        var context = new AgentContext("s", new AgentPrincipal(AgentPrincipalKind.McpKey, "ci", McpPermissionLevels.Admin,
            null, McpKeyId: "k1"), AgentOrigin.McpDirect, policy);

        var read = engine.Evaluate(new Whiskers.Services.Guardrails.GuardrailRequest("get_container_logs",
            McpPermissionLevels.Read, ProtectionHarness.Args(new { containerId = "whiskers" }), context));

        Assert.Equal(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, read.Verdict);
    }

    [Fact]
    public async Task A_scheduled_self_backup_runs_under_the_default_policy()
    {
        // The kind of target ("whiskers") is not a name: it must not meet the default policy's "whiskers*".
        var h = new ProtectionHarness(p => p.ProtectedResources = GuardrailPolicy.SafeDefault().ProtectedResources);
        var outcome = await h.RunAsync("selfbackup.create", new { label = "nightly" }, ProtectionHarness.AdminsTask(),
            MutationSurface.Automation);
        Assert.Equal(MutationStatus.Executed, outcome.Status);
    }

    [Theory]
    [InlineData("docker logs web 2>&1", "2")]                 // a redirection
    [InlineData("docker pull postgres:16", "16")]             // an image tag
    [InlineData("docker logs --tail=100 web", "100")]         // an option's value
    [InlineData("docker run -d redis:7", "7")]
    public async Task A_number_that_is_not_a_container_is_not_taken_for_an_id(string command, string idPrefix)
    {
        var h = new ProtectionHarness();
        h.Containers[0].Id = idPrefix + "abc0000whiskers";
        Assert.Equal(MutationStatus.Executed, (await h.RunAsync("host.exec",
            new { serverId = "local", command }, ProtectionHarness.AdminKey, MutationSurface.Mcp)).Status);
    }

    [Theory]
    [InlineData("rm -f /opt/dockerproxy/certs/ca.crt; docker stop web")]
    [InlineData("docker stop web; rm -f /opt/dockerproxy/certs/ca.crt")]
    public async Task A_policy_protected_resource_is_refused_whatever_control_plane_hit_comes_first(string command)
    {
        var h = new ProtectionHarness(p => p.ProtectedResources = ["web"]);
        var outcome = await h.RunAsync("host.exec", new { serverId = "local", command }, ProtectionHarness.UiAdmin,
            MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(h.Approvals.GetPending());
    }

    [Fact]
    public async Task A_single_digit_id_after_an_option_value_is_looked_up()
    {
        var h = new ProtectionHarness();
        h.Containers[0].Id = "7abc0000whiskers";
        Assert.Equal(MutationStatus.Denied, (await h.RunAsync("host.exec",
            new { serverId = "local", command = "docker stop -t 10 7" }, ProtectionHarness.AdminKey, MutationSurface.Mcp)).Status);
    }

    [Fact]
    public async Task A_resource_protected_only_by_the_policy_stays_refused_for_an_interactive_Admin()
    {
        var h = new ProtectionHarness(p => p.ProtectedResources = ["web"]);
        var outcome = await h.RunAsync("container.restart", new { containerId = "web", serverId = "local" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(h.Approvals.GetPending());
    }

    [Fact]
    public async Task A_refused_mount_from_an_env_value_is_never_quoted()
    {
        var host = new ProtectedResourceResolutionTests.FakeHost();
        host.Files["/opt/deployments/tools/.env"] = "DATA_ROOT=/srv/canary-secret\n";
        var h = new ProtectionHarness(p => p.ProtectedResources = ["/srv/*"], planner: d => new ComposePlanner(d, host));

        // The text alone mounts "/x"; only the planned mount, with the .env value, is protected.
        var outcome = await h.RunAsync("compose.deploy", new
        {
            serverId = "local", projectName = "tools",
            composeContent = "services:\n  a:\n    image: x\n    volumes:\n      - ${DATA_ROOT}/x:/data\n",
        }, ProtectionHarness.AdminKey, MutationSurface.Mcp);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains(Whiskers.Services.Guardrails.ProtectedResourceMatcher.ResolvedWithheldPrefix, outcome.Reason);
        Assert.DoesNotContain("canary", outcome.Reason);
        Assert.DoesNotContain(h.Audit.Events, e => e.Contains("canary"));
    }

    [Fact]
    public async Task An_import_into_a_protected_database_is_refused_by_the_database_it_runs_in()
    {
        // As the container page imports or seeds a file: the database selected there goes along.
        var h = new ProtectionHarness(p => p.ProtectedResources = ["billing"]);
        var outcome = await h.RunAsync("db.query",
            new { containerId = "web", serverId = "local", database = "billing", query = "UPDATE orders SET paid = true;" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Fact]
    public async Task Whiskers_own_database_on_a_shared_server_is_the_control_plane()
    {
        // A postgres that also holds other databases: the database the request runs in counts, not only the container.
        var h = new ProtectionHarness();
        var args = new { containerId = "web", serverId = "local", database = "whiskers", query = "DELETE FROM users;" };
        Assert.Equal(MutationStatus.Denied, (await h.RunAsync("db.query", args, ProtectionHarness.AdminKey, MutationSurface.Mcp)).Status);
        Assert.Equal(MutationStatus.PendingApproval,
            (await h.RunAsync("db.query", args, ProtectionHarness.UiAdmin, MutationSurface.Ui)).Status);
    }

    [Theory]
    [InlineData("UPDATE billing.orders SET paid = true")]
    [InlineData("UPDATE \"billing\".\"orders\" SET paid = true")]
    [InlineData("DELETE FROM `billing`.`orders`")]
    [InlineData("TRUNCATE prod.billing.orders")]
    public async Task A_qualified_name_names_each_of_its_parts(string query)
    {
        var h = new ProtectionHarness(p => p.ProtectedResources = ["billing"]);
        var outcome = await h.RunAsync("db.query", new { containerId = "web", serverId = "local", query },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Theory]
    [InlineData("\\i /tmp/wipe.sql")]
    [InlineData("select 'drop database ' || 'bil' || 'ling' \\gexec")]
    public async Task A_sql_client_command_is_refused_while_the_policy_protects_resources(string query)
    {
        var h = new ProtectionHarness(p => p.ProtectedResources = ["billing"]);
        var outcome = await h.RunAsync("db.query", new { containerId = "web", serverId = "local", query },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(h.Approvals.GetPending());
    }

    // Contract 0017 B (INV-59): a mount written inside a shell command is read as a mount.
    private static Whiskers.Services.Guardrails.GuardrailVerdict ExecuteCommandAsAdminKey(string command)
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        var principal = new AgentPrincipal(AgentPrincipalKind.McpKey, "k", McpPermissionLevels.Admin, null, McpKeyId: "k1");
        var args = new Dictionary<string, JsonElement>
        {
            ["serverId"] = JsonSerializer.SerializeToElement("local"),
            ["command"] = JsonSerializer.SerializeToElement(command),
        };
        return Whiskers.Services.Guardrails.GuardrailEngine.CreateDefault().Evaluate(
            new Whiskers.Services.Guardrails.GuardrailRequest("execute_command", McpPermissionLevels.Admin, args,
                new AgentContext("s", principal, AgentOrigin.McpDirect, policy))).Verdict;
    }

    /// <summary>The review's repro (finding 6): an Admin MCP key may not mount the volumes directory or /var/run.</summary>
    [Theory]
    [InlineData("docker run --rm -v /var/lib/docker/volumes:/v alpine find /v -name vault.json -delete")]
    [InlineData("docker run --rm -v /var/run:/r docker docker -H unix:///r/docker.sock ps")]
    public void A_key_cannot_mount_a_directory_above_the_control_plane_through_execute_command(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    [Theory]
    [InlineData("docker run --rm --mount type=bind,source=/var/run,target=/r alpine ls /r")]
    [InlineData("docker run --rm --volume=/var/lib/docker/volumes:/v alpine ls /v")]
    [InlineData("docker run --rm -v/var/run:/r alpine ls /r")]                       // the attached short form
    [InlineData("docker run --rm -v/var/lib/docker/volumes:/v alpine ls /v")]
    [InlineData("docker run --rm -itv/var/run:/r alpine ls /r")]                     // a flag cluster ending in v
    [InlineData("docker run -dv/var/lib/docker/volumes:/v alpine ls /v")]
    [InlineData("docker run --rm -itv /var/run:/r alpine ls /r")]                    // the cluster, value apart
    [InlineData("docker run --rm -v=/var/lib/docker/volumes:/v alpine ls /v")]
    [InlineData("docker run --rm --mount type=volume,volume-opt=type=none,volume-opt=o=bind,volume-opt=device=/var/lib/docker/volumes,dst=/v alpine ls /v")]
    [InlineData("docker volume create -o type=none -o o=bind -o device=/var/lib/docker/volumes x")]
    [InlineData("docker volume create --opt device=/var/run x")]
    [InlineData("docker run --rm -v ./var/lib/docker/volumes:/v alpine ls /v")]   // unresolvable: unanalysable
    [InlineData("docker run --rm -v ~/..:/v alpine ls /v")]
    [InlineData("docker run --rm -v /var/lib/docker/volumes:/v:ro alpine find /v -name vault.json -delete")]
    public void A_key_cannot_mount_the_control_plane_through_mount_or_volume_forms(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>Contract 0017 B, amendment 3 (the S2 review's bypass probe): every form below reached the control plane
    /// through a mount and must stay refused for a key.</summary>
    [Theory]
    // B1: Docker lower-cases the keys of a --mount value
    [InlineData("docker run --rm --mount type=bind,SOURCE=/var/run,target=/r alpine ls /r")]
    [InlineData("docker run --rm --mount type=bind,Src=/var/lib/docker/volumes,dst=/v alpine ls /v")]
    [InlineData("docker run --rm --mount type=volume,VOLUME-OPT=type=none,VOLUME-OPT=o=bind,VOLUME-OPT=device=/var/lib/docker/volumes,dst=/v alpine ls /v")]
    [InlineData("docker run --rm --mount=SRC=/var/run,dst=/r,type=bind alpine ls /r")]
    // B4: an attached -o value
    [InlineData("docker volume create -otype=none -oo=bind -odevice=/var/lib/docker/volumes x")]
    [InlineData("docker volume create -d local -odevice=/var/run -otype=none -oo=bind x")]
    [InlineData("docker volume create -o type=none -o o=bind -o DEVICE=/var/run x")]
    // B2: a keyed source is a path; it is not cut at ':'
    [InlineData("docker run --rm --mount type=bind,source=/tmp/a:b/../../var/run,target=/r alpine ls /r")]
    [InlineData("mkdir -p /tmp/a:b && docker run --rm --mount type=bind,source=/tmp/a:b/../../var/lib/docker/volumes,target=/v alpine ls /v")]
    [InlineData("docker volume create -o type=none -o o=bind -o device=/tmp/a:b/../../var/run x")]
    // B3: a flag cluster ending in v takes the next word; a relative source in it is unanalysable
    [InlineData("cd /var && docker run --rm -itv ./run:/r alpine ls /r")]
    [InlineData("cd /var/lib/docker && docker run -dv ./volumes:/v alpine ls /v")]
    [InlineData("docker run --rm -itv ~/..:/v alpine ls /v")]
    [InlineData("docker run --rm -itv ../run:/r alpine ls /r")]
    [InlineData("docker run --rm -itv /var/run:/r alpine ls /r")]
    [InlineData("sudo nerdctl run -dv ../run:/r alpine ls /r")]                     // the runtime after sudo
    [InlineData("alias d=docker; d run -itv ./x:/r alpine ls /r")]                   // the runtime behind an alias
    // quoting and normalisation
    [InlineData("docker run --rm -v \"/var/run\":/r alpine ls /r")]
    [InlineData("docker run --rm -v '/var/run:/r' alpine ls /r")]
    [InlineData("docker run --rm '-v' /var/run:/r alpine ls /r")]
    [InlineData("docker run --rm -v /var/run\\:/r alpine ls /r")]
    [InlineData("docker run --rm -v //var//run:/r alpine ls /r")]
    [InlineData("docker run --rm -v /var/run/../run:/r alpine ls /r")]
    [InlineData("docker run --rm -v /var/run/:/r alpine ls /r")]
    [InlineData("docker run --rm -v /var/./run/.:/r alpine ls /r")]
    [InlineData("docker run --rm -v /var/lib/docker/volumes/../volumes/:/v alpine ls /v")]
    [InlineData("docker run --rm -itv=/var/run:/r alpine ls /r")]
    [InlineData("docker run --rm --volume=\"/var/run\":/r alpine ls /r")]
    [InlineData("docker run --rm --mount 'type=bind,\"source=/var/run\",target=/r' alpine ls /r")]
    [InlineData("docker run --rm --mount type=bind,source=\"/var/run\",target=/r alpine ls /r")]
    [InlineData("docker run --rm --mount type=bind,src=/var//run/.,dst=/r alpine ls /r")]
    [InlineData("docker run --rm --mount type=bind,target=/r,source=/var/run alpine ls /r")]
    [InlineData("docker service create --mount type=bind,source=/var/run,target=/r alpine")]
    // run-time expansion
    [InlineData("docker run --rm -v $HOME/..:/v alpine ls /v")]
    [InlineData("docker run --rm -v $(pwd):/v alpine ls /v")]
    [InlineData("docker run --rm -v /var/{run,x}:/r alpine ls /r")]
    // other runtimes, compose, heredoc
    [InlineData("nerdctl run --rm -v /var/run:/r alpine ls /r")]
    [InlineData("sudo nerdctl run --rm --mount type=bind,src=/var/run,dst=/r alpine ls /r")]
    [InlineData("docker compose -f - up -d <<EOF\nservices:\n  t:\n    image: alpine\n    volumes: [\"/var/run:/r\"]\nEOF")]
    [InlineData("echo 'docker run -v /var/run:/r alpine ls' | sh")]
    // M3: another process's view of the file system
    [InlineData("docker run --rm -v /proc/1/root/var/run:/r alpine ls /r")]
    [InlineData("docker run --rm -v /proc/1/root:/h alpine ls /h")]
    [InlineData("docker run --rm -v /proc/self/root/var/lib/docker/volumes:/v alpine ls /v")]
    [InlineData("docker run --rm --mount type=bind,source=/proc/thread-self/cwd,target=/c alpine ls /c")]
    public void Mount_forms_from_the_S2_review_are_refused_for_a_key(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>Contract 0017 B, amendment 3, round 3 (the S2 delta review): the classes behind each bypass are closed
    /// fail-closed — only a plain absolute path is analysable as a mount source.</summary>
    [Theory]
    // B5: a line continuation starts no word of its own
    [InlineData("cd /var && docker run -v \\\n ./run:/r alpine")]
    [InlineData("cd /var && docker run -itv \\\n ./run:/r alpine")]
    [InlineData("cd /var && docker run --volume \\\n  ./run:/r alpine")]
    [InlineData("docker run --mount \\\n type=bind,SOURCE=/var/run,target=/r alpine")]
    [InlineData("docker run --mount \\\n type=bind,source=/var/run,target=/r alpine")]
    [InlineData("docker run -v \\\n /var/run:/r alpine")]
    // B6: a CSV-quoted --mount field
    [InlineData("docker run --rm --mount 'type=bind,\"source=/tmp/a,b/../../var/run\",target=/r' alpine ls /r")]
    [InlineData("docker run --rm --mount 'type=bind,source=\"/srv/x\",target=/r' alpine ls /r")]
    // B7: --mount-add reads like --mount
    [InlineData("docker service update --mount-add type=bind,source=/var/run,target=/r app")]
    [InlineData("docker service update --mount-add=type=bind,SRC=/var/lib/docker/volumes,dst=/v app")]
    // B8: a glob in the command word
    [InlineData("cd /var && /usr/bin/docke? run -v ./run:/r alpine")]
    [InlineData("cd /var && /usr/bin/docke? run -itv /var/run:/r alpine")]
    [InlineData("cd /var && /usr/bin/dock* run --rm -itv ./run:/r alpine ls /r")]
    [InlineData("cd /var && /usr/bin/podma? run --rm -itv ./run:/r alpine ls /r")]
    [InlineData("cd /var && /usr/bin/nerdct[l] run --rm -dv ./run:/r alpine ls /r")]
    [InlineData("cd /var && [d]ocker run --rm -itv ./run:/r alpine ls /r")]
    // M4: anything under /proc, /sys or /dev
    [InlineData("docker run --rm -v /proc/1/task/1/root:/h alpine ls /h")]
    [InlineData("docker run --rm -v /proc/self/task/7/cwd:/c alpine ls /c")]
    [InlineData("docker run --rm -v /sys:/s alpine ls /s")]
    [InlineData("docker run --rm -v /dev:/d alpine ls /d")]
    // M5: a runtime under another name (the cluster rule no longer needs a runtime word)
    [InlineData("cd /var && podman-remote run --rm -itv ./run:/r alpine ls /r")]
    [InlineData("cd /var && docker-current run --rm -itv ./run:/r alpine ls /r")]
    [InlineData("cd /var && finch run --rm -dv ./run:/r alpine ls /r")]
    [InlineData("cp /usr/bin/docker /tmp/d && cd /var && /tmp/d run --rm -itv ./run:/r alpine ls /r")]
    [InlineData("/tmp/d run --rm --mount type=bind,SOURCE=/var/run,target=/r alpine ls /r")]
    // the allow-list: any other character in an absolute source
    [InlineData("docker run --rm -v '/var/r un':/r alpine ls /r")]
    [InlineData("docker run --rm -v /var/ru?:/r alpine ls /r")]
    public void Mount_bypass_classes_from_the_S2_delta_review_are_refused_for_a_key(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>Contract 0017 B, amendment 3, round 4 (the S2 delta review of 91097206): every probe that was allowed.</summary>
    [Theory]
    // B9: a word built with escaped spaces is read again as a command
    [InlineData("sh -c docker\\ run\\ -v\\ /var/run:/r\\ alpine")]
    [InlineData("ssh localhost docker\\ run\\ -v\\ /var/run:/r\\ alpine")]
    [InlineData("tar -cf /tmp/x.tar /etc/hostname --checkpoint=1 --checkpoint-action=exec=docker\\ run\\ -v\\ /var/run:/r\\ alpine")]
    [InlineData("bash -c docker\\ run\\ -dv\\ ./x:/r\\ alpine")]
    [InlineData("ssh h docker\\ run\\ --volume=/var/run:/r\\ alpine")]
    [InlineData("sh -c rm\\ -rf\\ /var/lib/docker")]
    [InlineData("sh -c docker\\ run\\ -v\\ /:/r\\ alpine")]
    [InlineData("sudo sh -c docker\\ run\\ -v/var/run:/r\\ alpine")]
    [InlineData("sh -c docker\\ stop\\ whiskers")]
    [InlineData("docker run -v\\ /var/run:/r alpine")]
    // B10: no tool name decides whether a v cluster is a volume
    [InlineData("ssh localhost docker run -dv ~/../var/run:/r alpine")]
    [InlineData("ssh localhost docker run -dv ./../var/run:/r alpine")]
    [InlineData("find /tmp -maxdepth 0 -exec docker run -dv ~/../var/run:/r alpine ;")]
    [InlineData("find /tmp -maxdepth 0 -exec docker run -dv ~/../var/run:/r alpine \\;")]
    [InlineData("cp /usr/bin/docker /tmp/rsync; /tmp/rsync run -dv ~/../var/run:/r alpine")]
    [InlineData("/tmp/rsync run -dv ~/../var/run:/r alpine")]
    [InlineData("/tmp/tar run -dv ./../../var/run:/r alpine")]
    [InlineData("alias cp=docker\ncp run -dv ~/../var/run:/r alpine")]
    [InlineData("time rsync run -dv ./x:/r alpine")]
    [InlineData("ls -lv 2>&1 docker run -dv ./x:/r alpine")]
    [InlineData("ls > /tmp/o docker run -dv ./x:/r alpine")]
    [InlineData("env -i ls docker run -dv ./x:/r alpine")]
    [InlineData("env -u tar docker run -dv ./x:/r alpine")]
    [InlineData("env -u tar docker run -dv ~/../var/run:/r alpine")]
    [InlineData("sudo -u ls docker run -dv ./x:/r alpine")]
    [InlineData("sudo -u tar docker run -dv ./x:/r alpine")]
    [InlineData("sudo -g tar docker run -dv ./x:/r alpine")]
    [InlineData("timeout -s tar 5 docker run -dv ./x:/r alpine")]
    [InlineData("flock /tmp/ls docker run -dv ./x:/r alpine")]
    [InlineData("flock /tmp/tar docker run -dv ./x:/r alpine")]
    [InlineData("chroot /tmp/ls docker run -dv ./x:/r alpine")]
    [InlineData("stdbuf -o tar docker run -dv ./x:/r alpine")]
    [InlineData("exec -a ls docker run -dv ./x:/r alpine")]
    [InlineData("exec -a tar docker run -dv ./x:/r alpine")]
    [InlineData("nice -n tar docker run -dv ./x:/r alpine")]
    // B10, accepted overreach: another tool's v cluster before a word that starts with . ~ / and holds a ':'
    [InlineData("curl -sv ./x:/r")]
    [InlineData("ssh host -tv ./x:/r")]
    [InlineData("ssh host 'ls -lv ./x:/r'")]
    [InlineData("rsync -av ./a:b /srv/c")]
    [InlineData("scp -rv ./a:b host:/c")]
    [InlineData("tar -czv ./a:b")]
    [InlineData("echo docker\\ stop\\ whiskers")]                                    // read again: names whiskers
    // B11: a device= value or a bind source is a host path, relative ones included
    [InlineData("docker volume create -d local -o type=none -o o=bind -o device=var/run sockvol")]
    [InlineData("docker volume create --opt type=none --opt o=bind --opt device=var/run/../run sockvol")]
    [InlineData("docker volume create -odevice=var/run -otype=none -oo=bind v")]
    [InlineData("docker volume create -o type=none -o o=bind -o device=var v")]
    [InlineData("docker run --rm --mount type=volume,volume-opt=type=none,volume-opt=o=bind,volume-opt=device=var/run,dst=/r ubuntu ls /r")]
    [InlineData("cd / && podman run --mount type=bind,src=var/run,dst=/r alpine")]
    [InlineData("docker run --mount type=bind,source=var/run,target=/r alpine")]
    [InlineData("docker run --mount type=bind,source=var,target=/r alpine")]
    [InlineData("docker run --mount type=volume,source=var/run,target=/r alpine")]
    // B12: a line continuation inside double quotes is removed (a key, a protected name, the socket path)
    [InlineData("docker stop \"whis\\\nkers\"")]
    [InlineData("docker rm -f \"whis\\\nkers\"")]
    [InlineData("docker run -v \"/var/\\\nrun:/r\" alpine")]
    [InlineData("docker run --mount \"type=bind,sou\\\nrce=/var/run,target=/r\" alpine")]
    [InlineData("docker run --mount \"type=bind,source=/var/\\\nrun,target=/r\" alpine")]
    [InlineData("cat \"/var/run/dock\\\ner.sock\"")]
    public void Bypasses_from_the_S2_round_4_review_are_refused_for_a_key(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>B13: a word with quoted and unquoted parts is read again whole, not only its quoted parts.</summary>
    [Theory]
    [InlineData("sh -c 'docker run -v '/var/run:/r' alpine'")]
    [InlineData("sh -c 'docker run -v '/var/run:/r")]
    [InlineData("sh -c 'docker run -v /var/r'un:/r' alpine'")]
    [InlineData("sh -c docker\" run -v /var/r\"un:/r")]
    [InlineData("sh -c 'docker run -'v' ~/../var/run:/r alpine'")]
    [InlineData("ssh host 'docker run -'v' ~/../var/run:/r alpine'")]
    [InlineData("sh -c \"docker run -\"v\" ~/../var/run:/r alpine\"")]
    [InlineData("sh -c 'docker run -dv .'/x:/r' alpine'")]
    [InlineData("sh -c 'docker run --vol'ume' ~/../var/run:/r alpine'")]
    [InlineData("sh -c 'docker volume create -o type=none -o o=bind -o dev'ice=var/run' v'")]
    [InlineData("sh -c 'docker volume create --o'pt' device=var/run v'")]
    [InlineData("sh -c 'docker stop whis'kers")]
    [InlineData("sh -c 'cat /var/run/dock'er.sock")]
    public void Bypasses_from_the_S2_round_5_review_are_refused_for_a_key(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>B14: a quoted or escaped word without whitespace that a second shell expands (braces, globs,
    /// redirections) is read again too.</summary>
    [Theory]
    [InlineData("bash -c '{docker,stop,whi{s,}kers}'")]
    [InlineData("bash -c '{docker,run,-v,/var/run:/r,alpine}'")]
    [InlineData("bash -c \\{docker,stop,whi\\{s,\\}kers\\}")]
    [InlineData("sudo bash -c '{docker,stop,whi{s,}kers}'")]
    [InlineData("docker exec app bash -c '{docker,stop,whi{s,}kers}'")]
    [InlineData("bash -c '{rm,-rf,/var/lib/docker/volumes/whisk{e,}rs_data}'")]
    [InlineData("ssh host docker stop 'whi{s,}kers'")]
    [InlineData("ssh host docker stop whi\\{s,\\}kers")]
    [InlineData("ssh host docker run '{-v,/var/run:/r}' alpine")]
    [InlineData("ssh host rm -rf '/var/lib/docker/volumes/whisk*'")]
    [InlineData("ssh host rm -rf '/var/lib/docker/volumes/whis[k]ers_data'")]
    [InlineData("ssh host cat '/var/run/docker.s?ck'")]
    [InlineData("sh -c 'cat</var/run/docker.s?ck'")]
    [InlineData("rm -rf /var/lib/docker/volumes/whis[k]ers_data")]
    [InlineData("rm -rf /var/lib/docker/volumes/whis?ers_data")]
    public void Bypasses_from_the_S2_round_6_review_are_refused_for_a_key(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>B15, B16, M1, M2: nested or escaped braces, globs in any segment and under a compose prefix, a ']'
    /// first in a bracket expression, and fish's command substitution.</summary>
    [Theory]
    [InlineData("docker stop whi{{},s}kers")]
    [InlineData("docker stop whi{\\},s}kers")]
    [InlineData("docker stop whi{'}',s}kers")]
    [InlineData("bash -c 'docker stop whi{{},s}kers'")]
    [InlineData("bash -c '{{},docker,stop,whi{{},s}kers}'")]
    [InlineData("ssh host docker stop 'whi{{},s}kers'")]
    [InlineData("ssh host docker stop whi\\{\\{\\},s\\}kers")]
    [InlineData("cat /var/run/docker.so{{},c}k")]
    [InlineData("ssh host cat '/var/run/docker.so{{},c}k'")]
    [InlineData("cat /var/lib/docker/volumes/whis?ers_data/_data/vault.key")]
    [InlineData("cat /var/lib/docker/volumes/whis?ers_d?ta/_data/vault.key")]
    [InlineData("cat /var/lib/docker/volumes/whis?ers_data/_d?ta/vault.key")]
    [InlineData("ls /var/lib/docker/volumes/proj_whis[k]ers_data")]
    [InlineData("cat /var/lib/docker/volumes/proj_whis?ers_data/_data/vault.key")]
    [InlineData("ssh host rm -rf '/var/lib/docker/volumes/proj_whis[k]ers_data'")]
    [InlineData("cat /var/run/docker.soc[]k]")]
    [InlineData("cat /var/run/docker.s[]o]ck")]
    [InlineData("cat /var/run/docker.s[!]x]ck")]
    [InlineData("ssh host cat '/var/run/docker.s[]o]ck'")]
    [InlineData("fish -c 'docker stop (printf %s%s whi skers)'")]
    public void Bypasses_from_the_S2_round_7_review_are_refused_for_a_key(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>B17, M3, M4, m5: unnormalised glob paths, brackets the shell reads differently, globs on a directory
    /// above a protected path in a changing command, and relative glob segments.</summary>
    [Theory]
    [InlineData("cat /var//run/docker.s?ck")]
    [InlineData("cat /var/./run/docker.s?ck")]
    [InlineData("cat /var/run/../run/docker.s?ck")]
    [InlineData("cat //var/run/docker.s?ck")]
    [InlineData("curl --unix-socket /var//run/docker.s?ck http://x/containers/json")]
    [InlineData("ssh host cat '/var//run/docker.s?ck'")]
    [InlineData("cat /var/lib/docker//volumes/whis?ers_data/_data/vault.key")]
    [InlineData("ls /var/lib/docker/volumes/../volumes/whis?ers_data")]
    [InlineData("cat /var/run/docker.s[a\\]o]ck")]
    [InlineData("cat /var/run/docker.s[[.o.]]ck")]
    [InlineData("ssh host cat '/var/run/docker.s[[=o=]]ck'")]
    [InlineData("cat /tmp/[/../../var/run/docker.soc[]k]")]
    [InlineData("chmod -R 000 /var/lib/docker/v*")]
    [InlineData("mv /var/lib/dock?r /tmp/x")]
    [InlineData("mv /var/ru? /tmp/x")]
    [InlineData("rm -r /var/lib/dock?r")]
    [InlineData("chown -R nobody /var/lib/dock?r")]
    [InlineData("find /var/lib/dock?r -delete")]
    [InlineData("cd /app/data && cat mtl?/client.key")]
    public void Bypasses_from_the_S2_round_8_review_are_refused_for_a_key(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>B19, B20, m7: braces after a comment or an escaped quote, glob segments that can be '.' or '..', and
    /// a relative glob in a changing command.</summary>
    [Theory]
    [InlineData("echo ok # it's fine\ndocker stop whi{s,}kers")]
    [InlineData("echo ok # it's fine\ncurl --unix-socket /var/run/docker.so{c,}k -X POST http://x/containers/abc/stop")]
    [InlineData("echo \"\\\"\\\"\" ; docker stop whi{s,}kers")]
    [InlineData("echo \"a\\\"b\\\"c\" ; docker stop whi{s,}kers")]
    [InlineData("echo x | nc -U /var/run/.*/docker.sock")]
    [InlineData("socat - /var/run/.*/docker.sock")]
    [InlineData("cat /var/run/lock/.?/docker.sock")]
    [InlineData("cat /var/run/docker.s[[.o.]]ck")]
    [InlineData("cd /app/data && rm -rf mtl?")]
    [InlineData("cd /app/data && chmod -R 777 mtl?")]
    [InlineData("cd /app/data && mv mtl? /tmp/x")]
    public void Bypasses_from_the_S2_round_9_review_are_refused_for_a_key(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>Round 10: a relative glob on a protected volume name, widened brackets in a relative path, and a
    /// brace bash reads past a '}' without a comma.</summary>
    [Theory]
    [InlineData("cd /var/lib/docker/volumes && cat whisk?rs_data/_data/vault.key")]
    [InlineData("cd /var/lib/docker/volumes && cat whi[s]kers_data/_data/vault.key")]
    [InlineData("cd /var/lib/docker/volumes && cat ./whisk?rs_data/_data/vault.key")]
    [InlineData("cd /var/lib/docker && cat volumes/whisk?rs_data/_data/vault.key")]
    [InlineData("cd /app/data && rm -rf mt[]l]s")]
    [InlineData("cd /app/data && rm -rf mt[[.l.]]s")]
    [InlineData("cd /app/data && chmod -R 777 mt[]l]s")]
    [InlineData("docker stop whi{x},s}kers")]
    [InlineData("docker stop whi{},s}kers")]
    [InlineData("ssh host docker stop whi{x},s}kers")]
    [InlineData("curl --unix-socket /var/run/docker.so{k},c}k -X POST http://x/containers/abc/stop")]
    [InlineData("cat /var/lib/docker/volumes/whi{x},s}kers_data/_data/vault.key")]
    public void Bypasses_from_the_S2_round_10_review_are_refused_for_a_key(string command) =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>M5, M6: everyday bracket expressions and relative or quoted globs stay allowed.</summary>
    [Theory]
    [InlineData("[[ -f /etc/hosts ]] && echo ok")]
    [InlineData("if [[ -d /srv/app ]]; then echo ok; fi")]
    [InlineData("grep '[0-9][0-9]*' /srv/app/f")]
    [InlineData("grep -E '^[[:space:]]*#' /etc/fstab")]
    [InlineData("ls /var/log/*.[0-9][0-9].gz")]
    [InlineData("ls -d /srv/app/v[0-9].[0-9]")]
    [InlineData("find /srv -name '[a-z]*[0-9].log'")]
    [InlineData("ls logs/*.log")]
    [InlineData("tail -n 100 logs/*.log")]
    [InlineData("rm -f build/*.o")]
    [InlineData("git add src/*.cs")]
    [InlineData("ssh host 'ls /srv/app/*.conf'")]
    [InlineData("bash -c 'ls /srv/*.log'")]
    [InlineData("sed 's/a*/b/' /srv/app/f")]
    [InlineData("sed -i 's/foo.*/bar/' /srv/app/f")]
    [InlineData("grep -r 'foo/.*bar' /srv/app")]
    [InlineData("ls ../logs/*.log")]
    [InlineData("cp -a /etc/skel/.[!.]* /home/u/")]
    [InlineData("rm -f /tmp/build/.[!.]*")]
    [InlineData("rm -rf dist/*")]
    [InlineData("docker ps --format '{{.Names}},{{.Status}}'")]
    [InlineData("echo {a},{b}")]
    public void Everyday_globs_and_brackets_stay_allowed_for_a_key(string command) =>
        Assert.Equal(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>B18: braces up to the cap are decided in one pass, not one scan per brace.</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("{a")]
    [InlineData("{{}}")]
    public void Many_braces_up_to_the_limit_are_decided_quickly(string unit)
    {
        var body = string.Concat(Enumerable.Repeat(unit, (64 * 1024 - 10) / unit.Length));
        foreach (var command in new[] { "echo " + body, "echo '" + body + "'", "sh -c 'sh -c \"echo " + body[..^20] + "\"'" })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ExecuteCommandAsAdminKey(command);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"{command.Length} characters took {watch.Elapsed}");
        }
    }

    /// <summary>m3: glob words up to the cap are decided in time that grows with the command, not its square.</summary>
    [Fact]
    public void Many_glob_words_up_to_the_limit_are_decided_quickly()
    {
        var questionMarks = "cat " + string.Join(' ', Enumerable.Repeat("/a/" + new string('?', 255), 240));
        var stars = "cat " + string.Join(' ', Enumerable.Repeat("/a/*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a", 1500));
        foreach (var command in new[] { questionMarks[..Math.Min(questionMarks.Length, 64 * 1024 - 1)], stars[..Math.Min(stars.Length, 64 * 1024 - 1)] })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ExecuteCommandAsAdminKey(command);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"{command.Length} characters took {watch.Elapsed}");
        }
    }

    /// <summary>B14 must not refuse ordinary quoted patterns that name nothing protected.</summary>
    [Theory]
    [InlineData("find /var/log -name '*.log' -mtime +7")]
    [InlineData("grep 'a.*b' /etc/hosts")]
    [InlineData("docker ps --format '{{.Names}}'")]
    [InlineData("ls '/srv/app/*.conf'")]
    [InlineData("find /srv -exec ls '{}' \\;")]
    [InlineData("docker ps --format '{{.Names}},{{.Status}}'")]
    [InlineData("docker inspect -f '{{range.Mounts}}{{.Source}},{{end}}' app")]
    [InlineData("ls /srv/app/*.conf")]
    [InlineData("rm -f /srv/app/logs/*.log")]
    [InlineData("chmod 644 /srv/app/conf/*.yml")]
    [InlineData("cat ./logs/app-?.log")]
    [InlineData("tar -czf /backup/logs.tgz --exclude='*.tmp' /var/log")]
    public void Quoted_patterns_that_name_nothing_protected_stay_allowed_for_a_key(string command) =>
        Assert.Equal(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    /// <summary>M7: above the cap the helpers that look for container ids see no words; that is safe only because the
    /// same request is unanalysable in the protected-resource check.</summary>
    [Fact]
    public void A_command_over_the_cap_names_no_words_but_is_unanalysable()
    {
        var text = "docker stop whiskers; echo " + new string('a', Whiskers.Services.Guardrails.ProtectedResourceMatcher.MaxShellLength);
        Assert.Empty(Whiskers.Services.Guardrails.ProtectedResourceMatcher.MentionWords("command", text));
        Assert.Empty(Whiskers.Services.Guardrails.ProtectedResourceMatcher.RuntimeIdWords(text));
        Assert.False(Whiskers.Services.Guardrails.ProtectedResourceMatcher.InvokesContainerRuntime("command", text));
        Assert.Equal("command", Whiskers.Services.Guardrails.ProtectedResourceMatcher.FirstUnanalyzable(
            new Dictionary<string, JsonElement> { ["command"] = JsonSerializer.SerializeToElement(text) })?.UnanalyzableArgument);
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(text));
    }

    [Fact]
    public void A_command_longer_than_the_matcher_reads_is_refused_for_a_key() =>
        Assert.NotEqual(Whiskers.Services.Guardrails.GuardrailVerdict.Allow,
            ExecuteCommandAsAdminKey("echo " + new string('a', Whiskers.Services.Guardrails.ProtectedResourceMatcher.MaxShellLength)));

    [Fact]
    public void Many_commands_up_to_the_limit_are_decided_quickly()
    {
        // Linear, not quadratic: four times the commands take well under eight times as long (a quadratic check takes
        // sixteen). Best of three after a warm-up, so a busy runner does not decide it; 20 s is only a backstop.
        var count = Whiskers.Services.Guardrails.ProtectedResourceMatcher.MaxShellLength / 5 - 1;
        var full = string.Concat(Enumerable.Repeat("a b; ", count));
        var quarter = string.Concat(Enumerable.Repeat("a b; ", count / 4));
        ExecuteCommandAsAdminKey(quarter);
        var small = Best(quarter);
        var large = Best(full);
        Assert.True(large < 20_000, $"took {large} ms");
        Assert.True(large < 8 * Math.Max(small, 5), $"{count} commands took {large} ms, {count / 4} took {small} ms");

        static long Best(string command) => Enumerable.Range(0, 3).Min(_ =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ExecuteCommandAsAdminKey(command);
            return watch.ElapsedMilliseconds;
        });
    }

    [Theory]
    [InlineData("docker run -v /srv/app:/app alpine ls")]
    [InlineData("docker run -v /srv/app:/app:ro alpine ls")]
    [InlineData("docker run --mount type=bind,source=/srv/a:b,target=/r alpine ls")]  // a ':' inside a keyed path
    [InlineData("docker run --mount type=bind,SOURCE=/srv/app,target=/app alpine ls")]
    [InlineData("docker volume create -otype=none -oo=bind -odevice=/srv/data x")]
    [InlineData("rsync -av ./a host:/b")]                                              // another tool's verbose cluster
    [InlineData("tar -czvf ./a.tgz ./dir")]
    [InlineData("cp -rv ./a ./b")]
    [InlineData("rsync -av /srv/a/ /srv/b/")]
    [InlineData("tar -xzvf ./a.tgz")]
    [InlineData("awk -v x=1 1 /etc/hosts")]
    [InlineData("awk -v x=./y 1 /etc/hosts")]
    [InlineData("grep -iv foo /etc/hosts")]
    [InlineData("scp ./a host:/tmp/h")]
    [InlineData("ls -lav ./x")]
    [InlineData("docker exec app cp -rv ./a ./b")]
    [InlineData("docker exec app ls -lav ./x")]
    [InlineData("docker ps && rsync -av ./a host:/b")]
    [InlineData("ssh -oStrictHostKeyChecking=no host uptime")]
    [InlineData("mount -o remount,ro /srv")]
    [InlineData("journalctl -u docker -o short")]
    [InlineData("systemctl status docker && ls -lv ./x")]
    [InlineData("docker run --mount type=volume,src=data,dst=/var/run alpine ls")]      // a named volume, keyed
    [InlineData("[ -f /etc/hosts ] && echo ok")]                                       // [ is a command, not a glob
    [InlineData("psql -c \"select count(*) from orders\"")]
    [InlineData("docker run -v data:/var/run alpine ls")]                              // a named volume
    [InlineData("echo SRC=./build")]                                                   // not Docker's lower-case key
    [InlineData("export MYSRC=/var/run")]                                              // not a key of its own
    public void A_key_may_still_mount_an_ordinary_directory_through_execute_command(string command) =>
        Assert.Equal(Whiskers.Services.Guardrails.GuardrailVerdict.Allow, ExecuteCommandAsAdminKey(command));

    [Fact]
    public void A_key_may_still_mount_an_ordinary_directory_through_a_flag_cluster() =>
        Assert.Equal(Whiskers.Services.Guardrails.GuardrailVerdict.Allow,
            ExecuteCommandAsAdminKey("docker run -itv/srv/app:/app alpine ls"));
}
