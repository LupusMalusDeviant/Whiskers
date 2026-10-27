using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// WP2 step 2.2: UI handlers run through the Mutation Gateway as the signed-in human (PC-D1). A human is bound by
/// authorization, the kill switch, protected resources and the tool settings, but is not asked to approve their own
/// click; PC-D3 makes arbitrary SQL Admin.
/// </summary>
public class UiMutationRunnerTests
{
    private sealed class FixedUser(AppRole role, string? email = "ops@example.com") : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult(email);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    private static (UiMutationRunner Runner, AgentToolTestHelpers.NullMutationAudit Audit) Runner(
        AppRole role, GuardrailPolicy? policy = null, ICapabilityOverrides? overrides = null)
    {
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), audit, [],
            NullLogger<MutationGateway>.Instance, overrides: overrides,
            approvals: new ApprovalGate(new ApprovalStore(), store));
        return (new UiMutationRunner(gateway, new CapabilityCatalog(), new FixedUser(role)), audit);
    }

    [Fact]
    public async Task A_human_click_runs_without_an_approval_even_where_the_agent_would_need_one()
    {
        // SafeDefault asks for confirmation on every write — for the agent and keys, not for a person in the UI.
        var (runner, audit) = Runner(AppRole.Operator);

        var result = await runner.RunAsync("container.restart", new { containerId = "web-1" }, _ => Task.FromResult(42));

        Assert.Equal(42, result);
        Assert.Contains("begin:container.restart:", audit.Events);
        Assert.Contains("complete:Executed", audit.Events);
    }

    [Fact]
    public async Task The_kill_switch_stops_a_human_too()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;
        var (runner, audit) = Runner(AppRole.Admin, policy);
        var ran = false;

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.RunAsync("container.restart", new { containerId = "web-1" }, _ => { ran = true; return Task.CompletedTask; }));

        Assert.False(ran);
        Assert.Contains("Read-only", refused.Message);
        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Contains("denied:Denied", audit.Events);
    }

    [Fact]
    public async Task A_protected_container_is_protected_from_a_human_too()
    {
        var (runner, _) = Runner(AppRole.Admin);   // SafeDefault protects whiskers*
        var ran = false;

        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.RunAsync("container.stop", new { containerId = "whiskers" }, _ => { ran = true; return Task.CompletedTask; }));

        Assert.False(ran);
    }

    [Fact]
    public async Task Tool_lists_and_argument_patterns_are_agent_rules_and_do_not_bind_a_human()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ToolMode = "allow";                       // nothing on the allow list
        policy.ToolDenyList = ["restart_container"];
        var (runner, _) = Runner(AppRole.Operator, policy);

        var result = await runner.RunAsync("container.restart", new { containerId = "web-1" }, _ => Task.FromResult("ok"));

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task A_viewer_cannot_mutate_through_the_runner()
    {
        var (runner, _) = Runner(AppRole.Viewer);

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.RunAsync("container.start", new { containerId = "web-1" }, _ => Task.CompletedTask));

        Assert.Contains("requires 'write'", refused.Message);
    }

    [Fact]
    public async Task Arbitrary_sql_needs_admin_on_the_ui_too()
    {
        var (operatorRunner, _) = Runner(AppRole.Operator);
        var (adminRunner, _) = Runner(AppRole.Admin);

        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            operatorRunner.RunAsync("db.query", new { containerId = "db-1", query = "select 1" }, _ => Task.FromResult("rows")));
        Assert.Equal("rows",
            await adminRunner.RunAsync("db.query", new { containerId = "db-1", query = "select 1" }, _ => Task.FromResult("rows")));
        Assert.Equal(McpPermissionLevels.Admin, McpPermissionLevels.DefaultToolLevels["execute_query"]);
    }

    [Fact]
    public async Task A_tool_switched_off_in_settings_is_off_for_the_ui_too()
    {
        var (runner, _) = Runner(AppRole.Admin,
            overrides: new ToolAuthorizationTests.FixedOverrides(new CapabilityAvailability(false, McpPermissionLevels.Write)));

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.RunAsync("container.restart", new { containerId = "web-1" }, _ => Task.CompletedTask));

        Assert.Contains("disabled", refused.Message);
    }

    [Fact]
    public async Task The_actions_own_exception_reaches_the_page_unchanged()
    {
        var (runner, audit) = Runner(AppRole.Admin);

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            runner.RunAsync("container.restart", new { containerId = "web-1" }, _ => throw new IOException("docker down")));

        Assert.Equal("docker down", thrown.Message);
        Assert.Contains("complete:Failed", audit.Events);
    }

    [Fact]
    public async Task An_unknown_capability_is_refused()
    {
        var (runner, _) = Runner(AppRole.Admin);

        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.RunAsync("no.such.capability", new { }, _ => Task.CompletedTask));
    }

    [Fact]
    public void Arguments_are_taken_from_the_named_properties_and_nulls_are_left_out()
    {
        var args = UiMutationRunner.ToArguments(new { containerId = "web-1", serverId = (string?)null, minutes = 30 });

        Assert.Equal(["containerId", "minutes"], args.Keys.Order());
        Assert.Equal(JsonValueKind.Number, args["minutes"].ValueKind);
    }

    [Fact]
    public async Task A_failure_answered_without_an_exception_is_recorded_as_failed_and_still_reaches_the_page()
    {
        var (runner, audit) = Runner(AppRole.Admin);

        var result = await runner.RunAsync("host.exec", new { serverId = "local" },
            _ => Task.FromResult(new Whiskers.Services.Server.CommandResult { ExitCode = 1, Error = "ufw: denied" }));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("complete:Failed", audit.Events);
        Assert.DoesNotContain("complete:Executed", audit.Events);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Failure_values_are_recognised(bool ok, bool failed)
    {
        Assert.Equal(failed, MutationResults.FailureOf(ok) is not null);
        Assert.Equal(failed, MutationResults.FailureOf((ok, "out")) is not null);
        Assert.Equal(failed, MutationResults.FailureOf(new QueryResult { Error = ok ? null : "x" }) is not null);
        Assert.Null(MutationResults.FailureOf("anything"));
    }

    [Fact]
    public async Task Kubernetes_workload_actions_are_stopped_by_the_kill_switch()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;
        var (runner, _) = Runner(AppRole.Admin, policy);
        var ran = false;

        await Assert.ThrowsAsync<MutationRefusedException>(() => runner.RunAsync("workload.stop",
            new { workloadId = "default/deployment/web", name = "web", serverId = "k8s" },
            _ => { ran = true; return Task.CompletedTask; }));

        Assert.False(ran);
    }

    [Fact]
    public async Task A_protected_kubernetes_workload_is_protected_too()
    {
        var (runner, _) = Runner(AppRole.Admin);   // SafeDefault protects whiskers*

        await Assert.ThrowsAsync<MutationRefusedException>(() => runner.RunAsync("workload.restart",
            new { workloadId = "default/deployment/whiskers", name = "whiskers", serverId = "k8s" },
            _ => Task.CompletedTask));
    }

    [Fact]
    public void Volume_and_workload_targets_are_named()
    {
        var catalog = new CapabilityCatalog();
        var volume = MutationTargets.FromArguments(catalog.Find("volume.backup")!,
            UiMutationRunner.ToArguments(new { volumeName = "pgdata" }));
        var workload = MutationTargets.FromArguments(catalog.Find("workload.start")!,
            UiMutationRunner.ToArguments(new { workloadId = "ns/deployment/web", name = "web" }));

        Assert.Equal("pgdata", volume.Id);
        Assert.Equal(("ns/deployment/web", "web"), (workload.Id, workload.Name));
    }

    [Fact]
    public void Free_text_payloads_never_reach_the_audit_row()
    {
        var args = UiMutationRunner.ToArguments(new
        {
            containerId = "db-1", query = "ALTER ROLE app PASSWORD 'Violet!47'", command = "mysql -pSecret",
        });

        var stored = JsonSerializer.Serialize(AuditLogMutationSink.WithoutPayloads(args));

        Assert.DoesNotContain("Violet", stored);
        Assert.DoesNotContain("Secret", stored);
        Assert.Contains("db-1", stored);
    }

    [Fact]
    public void A_refusal_reason_never_carries_a_payload_value()
    {
        var args = UiMutationRunner.ToArguments(new { query = "ALTER ROLE production PASSWORD 'Violet!47'" });

        var reason = AuditLogMutationSink.WithoutPayloadValues(
            "Protected resource 'production*' matched ALTER ROLE production PASSWORD 'Violet!47'", args);

        Assert.DoesNotContain("Violet", reason);
        Assert.Contains("production*", reason);
    }

    [Fact]
    public async Task A_kubernetes_action_is_decided_on_the_controller_it_changes()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources.Add("payments");   // the Deployment, matched exactly
        var (runner, _) = Runner(AppRole.Admin, policy);
        var ran = false;

        await Assert.ThrowsAsync<MutationRefusedException>(() => runner.RunAsync("workload.stop",
            new { workloadId = "shop/payments-7fdc9-abcde", name = "payments", pod = "payments-7fdc9-abcde", serverId = "k8s" },
            _ => { ran = true; return Task.CompletedTask; }));

        Assert.False(ran);
    }

    [Fact]
    public async Task A_cleanup_is_refused_while_a_stopped_protected_container_would_be_pruned()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources.Add("production-*");
        var docker = new FakeDocker(
            new ContainerInfo { Id = "a1", Name = "production-db", State = "exited", ServerId = "local" },
            new ContainerInfo { Id = "b2", Name = "web", State = "running", ServerId = "local" });
        var executor = new Whiskers.Services.Scheduler.TaskExecutor(docker, null!, null!, null!, null!, null!,
            NullLogger<Whiskers.Services.Scheduler.TaskExecutor>.Instance,
            guardrails: new AgentToolTestHelpers.FixedGuardrailStore(policy));

        var (success, output) = await executor.ExecuteAsync(new ScheduledTaskEntity
        {
            TaskId = "c1", Name = "cleanup", TaskType = ScheduledTaskType.Cleanup,
        });

        Assert.False(success);   // refused before the (null) host executor is reached
        Assert.Contains("production-db", output);
    }

    [Fact]
    public async Task A_local_cleanup_checks_the_local_server_even_when_the_default_is_remote()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources.Add("production-*");
        var docker = new FakeDocker(
            new ContainerInfo { Id = "a1", Name = "production-db", State = "exited", ServerId = "local" })
        {
            DefaultServerId = "remote-1",
        };
        var executor = new Whiskers.Services.Scheduler.TaskExecutor(docker, null!, null!, null!, null!, null!,
            NullLogger<Whiskers.Services.Scheduler.TaskExecutor>.Instance,
            guardrails: new AgentToolTestHelpers.FixedGuardrailStore(policy));

        var (success, output) = await executor.ExecuteAsync(new ScheduledTaskEntity
        {
            TaskId = "c2", Name = "cleanup", TaskType = ScheduledTaskType.Cleanup, ServerId = "local",
        });

        Assert.False(success);   // refused before the (null) host executor is reached
        Assert.Contains("production-db", output);
    }

    private static ContainerInfo ComposeService(string name, string project, string service) => new()
    {
        Id = name + "-id", Name = name, ServerId = "local", State = "running",
        Labels = new()
        {
            ["com.docker.compose.project"] = project,
            ["com.docker.compose.service"] = service,
        },
    };

    [Fact]
    public async Task A_compose_deploy_is_decided_on_every_container_it_recreates()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources.Add("payments-db");   // differs from the project name "payments"
        var (runner, _) = Runner(AppRole.Admin, policy);
        var existing = new[]
        {
            ComposeService("payments-web", "payments", "web"),
            ComposeService("payments-db", "payments", "db"),
            ComposeService("other", "other", "web"),
        };
        var ran = false;

        var affected = ComposeModel.Impact("payments",
            [new ComposeServicePlan("web", null), new ComposeServicePlan("db", null)], false, existing)
            .Select(i => i.Container).ToList();
        await Assert.ThrowsAsync<MutationRefusedException>(() => runner.RunAsync("compose.deploy",
            new { projectName = "payments", serverId = "local" },
            _ => runner.RunForEachAsync("compose.deploy", affected, "local", () => { ran = true; return Task.FromResult(0); })));

        Assert.Equal(["payments-web", "payments-db"], affected.Select(c => c.Name));
        Assert.False(ran);
    }

    [Theory]
    [InlineData("{\"maxBackups\":\"3\"}", 3)]
    [InlineData("{\"maxBackups\":\"0\"}", 0)]
    [InlineData(null, 0)]
    [InlineData("{ broken", 0)]
    public void Retention_is_read_from_the_task_config(string? config, int expected) =>
        Assert.Equal(expected, Whiskers.Services.Scheduler.TaskExecutor.RetentionOf(new ScheduledTaskEntity { Config = config }));

    private sealed class BrokenRequirements : IEffectiveRequirementResolver
    {
        public Task<string> ResolveAsync(Capability capability, IReadOnlyDictionary<string, JsonElement> arguments,
            CancellationToken ct) => throw new InvalidOperationException("scheduler unavailable");
    }

    /// <summary>German texts, looked up the way the page's localizer would.</summary>
    private sealed class GermanText : Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>
    {
        private static readonly System.Resources.ResourceManager Resources =
            new("Whiskers.Resources.SharedResource", typeof(SharedResource).Assembly);
        private static readonly System.Globalization.CultureInfo German = new("de");

        public Microsoft.Extensions.Localization.LocalizedString this[string name] =>
            Resources.GetString(name, German) is { } value ? new(name, value) : new(name, name, resourceNotFound: true);

        public Microsoft.Extensions.Localization.LocalizedString this[string name, params object[] arguments] =>
            this[name] is { ResourceNotFound: false } text ? new(name, string.Format(text.Value, arguments)) : this[name];

        public IEnumerable<Microsoft.Extensions.Localization.LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    [Fact]
    public async Task A_coded_refusal_reaches_the_page_in_the_users_language_and_the_audit_in_english()
    {
        // R1-05: the refusals the effective requirement adds carry a code the page shows localized.
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), audit, [],
            NullLogger<MutationGateway>.Instance, requirements: new BrokenRequirements());
        var runner = new UiMutationRunner(gateway, new CapabilityCatalog(), new FixedUser(AppRole.Admin), new GermanText());

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.RunAsync("scheduler.create", new { taskType = "CustomCommand" }, _ => Task.FromResult(true)));

        Assert.Contains("ließ sich nicht bestimmen", refused.Message);
    }

    [Fact]
    public void Every_refusal_code_has_an_english_and_a_german_text()
    {
        var resources = new System.Resources.ResourceManager("Whiskers.Resources.SharedResource", typeof(SharedResource).Assembly);
        var english = resources.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, false)!;
        var german = resources.GetResourceSet(new System.Globalization.CultureInfo("de"), true, false)!;
        foreach (var code in MutationReasonCodes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(english.GetString(MutationReasonCodes.KeyOf(code))), code);
            Assert.False(string.IsNullOrWhiteSpace(german.GetString(MutationReasonCodes.KeyOf(code))), code);
        }
    }
}
