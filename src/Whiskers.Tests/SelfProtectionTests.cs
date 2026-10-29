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
    public MutationGateway Gateway { get; }
    public int Runs { get; private set; }

    public ProtectionHarness(Action<GuardrailPolicy>? configure = null, Func<FakeDocker, IComposePlanner>? planner = null,
        string serverName = "Local")
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
                ? new TestServices(Docker, new FakeServerConfig(Server(serverName)))
                : new TestServices(Docker, new FakeServerConfig(Server(serverName)), planner(Docker))),
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
        { "volume.backup_delete", """{"volumeName":"serverwatch-data","serverId":"local"}""" },     // the data volume
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
    };

    private static object Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Theory]
    [MemberData(nameof(ControlPlane))]
    public async Task Keys_agents_over_MCP_automations_and_operators_are_refused(string capability, string arguments)
    {
        var h = new ProtectionHarness();
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
        var h = new ProtectionHarness();
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

    [Fact]
    public async Task A_resource_protected_only_by_the_policy_stays_refused_for_an_interactive_Admin()
    {
        var h = new ProtectionHarness(p => p.ProtectedResources = ["web"]);
        var outcome = await h.RunAsync("container.restart", new { containerId = "web", serverId = "local" },
            ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(h.Approvals.GetPending());
    }
}
