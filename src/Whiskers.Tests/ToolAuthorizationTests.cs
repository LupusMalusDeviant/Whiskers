using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Docker;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// INV-15 and the new gateway steps of WP2 step 2.1: the tool switch and a stricter stored level apply to every
/// principal kind; the target resolves before policy; a Confirm runs only with an approval for exactly this call.
/// </summary>
public class ToolAuthorizationTests
{
    private static Dictionary<string, JsonElement> Args(string container) =>
        new() { ["containerId"] = JsonSerializer.SerializeToElement(container) };

    private static MutationRequest Request(MutationPrincipal principal, MutationSurface surface, string container = "web-1",
        string? operationId = null, string? sessionId = null) =>
        new("container.restart", principal, surface, new MutationTarget("container", container), Args(container),
            operationId, sessionId);

    private static readonly MutationPrincipal Agent = new(MutationPrincipalKind.Agent, "agent:ops@example.com",
        "agent:ops@example.com", McpPermissionLevels.Write,
        OnBehalfOf: new MutationPrincipal(MutationPrincipalKind.Human, "ops@example.com", "ops@example.com", McpPermissionLevels.Write));

    private static readonly MutationPrincipal WebUser =
        new(MutationPrincipalKind.Human, "ops@example.com", "ops@example.com", McpPermissionLevels.Write);

    private static MutationGateway Gateway(ICapabilityOverrides? overrides = null, IMutationTargetResolver? targets = null,
        GuardrailPolicy? policy = null, ApprovalStore? approvals = null, AgentToolTestHelpers.NullMutationAudit? audit = null)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? AllowWrites());
        return new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            audit ?? new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            overrides: overrides, targets: targets, approvals: new ApprovalGate(approvals ?? new ApprovalStore(), store));
    }

    /// <summary>Writes run autonomously; nothing is protected.</summary>
    private static GuardrailPolicy AllowWrites()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        policy.ProtectedResources = [];
        return policy;
    }

    [Theory]
    [InlineData(MutationSurface.Agent)]
    [InlineData(MutationSurface.Ui)]
    [InlineData(MutationSurface.Automation)]
    public async Task A_disabled_tool_is_refused_for_every_principal_kind(MutationSurface surface)
    {
        var gateway = Gateway(new FixedOverrides(new CapabilityAvailability(false, McpPermissionLevels.Write)));
        var ran = false;

        var outcome = await gateway.ExecuteAsync(Request(surface == MutationSurface.Agent ? Agent : WebUser, surface),
            (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("disabled", outcome.Reason);
        Assert.False(ran);
    }

    [Fact]
    public async Task A_stricter_stored_level_applies_to_the_agent_too()
    {
        var gateway = Gateway(new FixedOverrides(new CapabilityAvailability(true, McpPermissionLevels.Admin)));

        var outcome = await gateway.ExecuteAsync(Request(Agent, MutationSurface.Agent), (_, _) => Task.FromResult(0));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("admin", outcome.Reason);
    }

    [Fact]
    public async Task The_mcp_tool_settings_can_only_raise_the_level_and_switch_the_tool_off()
    {
        var dir = Path.Combine(Path.GetTempPath(), "whiskers-overrides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var permissions = new McpPermissionService(NullLogger<McpPermissionService>.Instance, new DataPathOptions(dir));
            await permissions.InitializeAsync();
            var overrides = new McpToolCapabilityOverrides(permissions);
            var restart = new CapabilityCatalog().Find("container.restart")!;

            Assert.Equal(new CapabilityAvailability(true, McpPermissionLevels.Write), overrides.Resolve(restart));

            var data = permissions.GetPermissionData();
            data.Tools["restart_container"].RequiredLevel = McpPermissionLevels.Admin;
            await permissions.SavePermissionDataAsync(data);
            Assert.Equal(McpPermissionLevels.Admin, overrides.Resolve(restart).RequiredLevel);

            await permissions.SetToolEnabledAsync("restart_container", false);
            Assert.False(overrides.Resolve(restart).Enabled);

            // A UI-only capability has no tool switch.
            Assert.True(overrides.Resolve(new CapabilityCatalog().Find("container.remove")!).Enabled);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task An_unknown_container_is_refused_before_policy_and_a_known_one_is_recorded_by_its_full_id()
    {
        var docker = new FakeDocker(new ContainerInfo { Id = "abc123def456", Name = "web-1" });
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var gateway = Gateway(targets: new MutationTargetResolver(new TestServices(docker,
            new FakeServerConfig(new ServerConfig { Id = "local", Name = "Local", IsDefault = true }))), audit: audit);

        var unknown = await gateway.ExecuteAsync(Request(WebUser, MutationSurface.Ui, "nope"), (_, _) => Task.FromResult(0));
        MutationTarget? seen = null;
        var known = await gateway.ExecuteAsync(Request(WebUser, MutationSurface.Ui, "abc123"),
            (ctx, _) => { seen = ctx.Request.Target; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, unknown.Status);
        Assert.Contains("not found", unknown.Reason);
        Assert.Equal(MutationStatus.Executed, known.Status);
        Assert.Equal("abc123def456", seen!.Id);
        Assert.Equal("web-1", seen.Name);
    }

    [Fact]
    public async Task A_protected_container_named_by_an_id_prefix_is_still_protected()
    {
        // The safe default protects whiskers*; the caller names the container only by a unique id prefix.
        var docker = new FakeDocker(new ContainerInfo { Id = "c0ffee123456", Name = "whiskers" });
        var policy = AllowWrites();
        policy.ProtectedResources = ["whiskers*"];
        var gateway = Gateway(targets: new MutationTargetResolver(new TestServices(docker,
            new FakeServerConfig(new ServerConfig { Id = "local", Name = "Local", IsDefault = true }))), policy: policy);
        var ran = false;

        var outcome = await gateway.ExecuteAsync(Request(WebUser, MutationSurface.Ui, "c0ffee"),
            (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("protected", outcome.Reason);
        Assert.Contains("protected-resource", outcome.MatchedRules);
        Assert.False(ran);
    }

    [Fact]
    public async Task Confirm_without_an_approval_waits_and_with_the_session_approval_runs_once()
    {
        var approvals = new ApprovalStore();
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var gateway = Gateway(policy: GuardrailPolicy.SafeDefault(), approvals: approvals, audit: audit);   // writes need a human
        var runs = 0;
        Task<int> Run(MutationContext _, CancellationToken __) { runs++; return Task.FromResult(runs); }

        var pending = await gateway.ExecuteAsync(Request(Agent, MutationSurface.Agent, operationId: "op-1", sessionId: "s1"), Run);
        Assert.Equal(MutationStatus.PendingApproval, pending.Status);
        Assert.Equal(0, runs);

        // The session's own approval for this operation (what ApprovalCoordinator raises), decided by a human.
        var approval = approvals.Create(new ApprovalRequest("s1", "call-1", "ops@example.com", "agent-web",
            "restart_container", McpPermissionLevels.Write, null, "needs a human", CorrelationId: "op-1",
            ArgumentsHash: ApprovalFingerprint.OfArguments(Args("web-1"))), _ => Task.CompletedTask);
        await approvals.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin);

        var executed = await gateway.ExecuteAsync(Request(Agent, MutationSurface.Agent, operationId: "op-1", sessionId: "s1"), Run);
        var again = await gateway.ExecuteAsync(Request(Agent, MutationSurface.Agent, operationId: "op-1", sessionId: "s1"), Run);

        Assert.Equal(MutationStatus.Executed, executed.Status);
        Assert.Equal(approval.Id, executed.ApprovalId);
        Assert.Contains($"begin:container.restart:{approval.Id}", audit.Events);
        Assert.Equal(MutationStatus.PendingApproval, again.Status);
        Assert.Equal(1, runs);
    }

    /// <summary>The session's own approval for operation <paramref name="operation"/>, decided by a human.</summary>
    private static async Task<Approval> ApprovedSessionApproval(ApprovalStore approvals, string operation)
    {
        var approval = approvals.Create(new ApprovalRequest("s1", "call-1", "ops@example.com", "agent-web",
            "restart_container", McpPermissionLevels.Write, null, "needs a human", CorrelationId: operation,
            ArgumentsHash: ApprovalFingerprint.OfArguments(Args("web-1"))), _ => Task.CompletedTask);
        await approvals.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin);
        return approval;
    }

    [Fact]
    public async Task A_failed_audit_intent_leaves_the_approval_usable()
    {
        var approvals = new ApprovalStore();
        var audit = new AgentToolTestHelpers.NullMutationAudit { FailNextBegin = true };
        var gateway = Gateway(policy: GuardrailPolicy.SafeDefault(), approvals: approvals, audit: audit);
        var approval = await ApprovedSessionApproval(approvals, "op-1");
        var runs = 0;
        Task<int> Run(MutationContext _, CancellationToken __) => Task.FromResult(++runs);

        var refused = await gateway.ExecuteAsync(Request(Agent, MutationSurface.Agent, operationId: "op-1", sessionId: "s1"), Run);
        var retried = await gateway.ExecuteAsync(Request(Agent, MutationSurface.Agent, operationId: "op-1", sessionId: "s1"), Run);

        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Equal(MutationStatus.Executed, retried.Status);
        Assert.Equal(approval.Id, retried.ApprovalId);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Two_concurrent_runs_on_one_approval_execute_once_and_the_other_is_recorded_as_refused()
    {
        var approvals = new ApprovalStore();
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var gateway = Gateway(policy: GuardrailPolicy.SafeDefault(), approvals: approvals, audit: audit);
        await ApprovedSessionApproval(approvals, "op-1");
        var runs = 0;
        Task<int> Run(MutationContext _, CancellationToken __) => Task.FromResult(Interlocked.Increment(ref runs));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            gateway.ExecuteAsync(Request(Agent, MutationSurface.Agent, operationId: "op-1", sessionId: "s1"), Run))));

        Assert.Equal(1, runs);
        Assert.Single(outcomes, o => o.Status == MutationStatus.Executed);
        Assert.All(outcomes.Where(o => o.Status != MutationStatus.Executed),
            o => Assert.True(o.Status is MutationStatus.Denied or MutationStatus.PendingApproval));
        // Every attempt that wrote an intent row also completed it.
        lock (audit.Events)
            Assert.Equal(audit.Events.Count(e => e.StartsWith("begin:")), audit.Events.Count(e => e.StartsWith("complete:")));
    }

    [Fact]
    public async Task A_tool_that_reports_a_failure_is_recorded_as_failed_and_its_answer_is_kept()
    {
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var gateway = Gateway(audit: audit);

        var outcome = await gateway.ExecuteAsync(Request(WebUser, MutationSurface.Mcp),
            (_, _) => Task.FromResult(MutationScope.Fail("Container not found: web-1")));

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Equal("Container not found: web-1", outcome.Value);
        Assert.Equal("Container not found: web-1", outcome.Reason);
        Assert.Null(outcome.Error);
        Assert.Contains("complete:Failed", audit.Events);
        Assert.Contains("Container not found: web-1", audit.Reasons);
    }

    [Fact]
    public async Task Fail_outside_a_gateway_scope_only_returns_the_text_and_a_long_reason_is_shortened()
    {
        Assert.Equal("plain", MutationScope.Fail("plain"));

        var gateway = Gateway();
        var longText = new string('x', 5000);
        var outcome = await gateway.ExecuteAsync(Request(WebUser, MutationSurface.Mcp),
            (_, _) => Task.FromResult(MutationScope.Fail(longText)));

        Assert.Equal(longText, outcome.Value);
        Assert.True(outcome.Reason!.Length < 600);
    }

    [Fact]
    public async Task An_agent_tool_that_reports_a_failure_returns_an_error_to_the_agent()
    {
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var services = new ServiceCollection().AddHttpContextAccessor().BuildServiceProvider();
        var invoker = new Whiskers.Services.Agent.AgentToolInvoker(AgentToolTestHelpers.DefaultRegistry(),
            Whiskers.Services.Guardrails.GuardrailEngine.CreateDefault(),
            services.GetRequiredService<IServiceScopeFactory>(),
            gateway: AgentToolTestHelpers.Gateway(AllowWrites(), audit: audit));
        var context = new AgentContext("s1",
            new AgentPrincipal(AgentPrincipalKind.WebUser, "ops", McpPermissionLevels.Admin, null, UserEmail: "ops@example.com"),
            AgentOrigin.WebUi, AllowWrites());

        // pause_server_checks without serverId: the tool answers with an error text instead of throwing.
        var result = await invoker.InvokeAsync(new AgentToolCall("1", "pause_server_checks", "{}"), context);

        Assert.True(result.IsError);
        Assert.Contains("serverId is required", result.Content);
        Assert.Contains("complete:Failed", audit.Events);
    }

    [Fact]
    public async Task A_disabled_read_tool_is_refused_for_the_agent_too()
    {
        var invoker = new Whiskers.Services.Agent.AgentToolInvoker(AgentToolTestHelpers.DefaultRegistry(),
            Whiskers.Services.Guardrails.GuardrailEngine.CreateDefault(), new NoScopes(),
            overrides: new FixedOverrides(new CapabilityAvailability(false, McpPermissionLevels.Read)));
        var context = new AgentContext("s1",
            new AgentPrincipal(AgentPrincipalKind.WebUser, "ops", McpPermissionLevels.Admin, null, UserEmail: "ops@example.com"),
            AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());

        var result = await invoker.InvokeAsync(new AgentToolCall("1", "list_servers", "{}"), context);

        Assert.True(result.IsError);
        Assert.Contains("disabled", result.Content);
    }

    internal sealed class FixedOverrides(CapabilityAvailability availability) : ICapabilityOverrides
    {
        public CapabilityAvailability Resolve(Capability capability) => availability;
        public CapabilityAvailability ResolveTool(string toolName, string codeLevel) => availability;
    }

    private sealed class NoScopes : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() =>
            throw new InvalidOperationException("A refused call must not create a scope.");
    }

    private sealed class SingleService<T>(T instance) : IServiceProvider where T : class
    {
        public object? GetService(Type serviceType) => serviceType == typeof(T) ? instance : null;
    }
}

/// <summary>INV-16 over the real wire: a key's AllowedTools list cannot lift it above its level.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class ToolAuthorizationWireTests
{
    [Fact]
    public async Task Allowed_tools_cannot_widen_a_read_key_to_a_write_tool()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-allowedtools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var previousDataDir = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
        try
        {
            await using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.UseEnvironment("Development"));
            using var client = factory.CreateClient();

            var permissions = factory.Services.GetRequiredService<IMcpPermissionService>();
            var key = await permissions.AddApiKeyAsync("narrow", McpPermissionLevels.Read);
            var data = permissions.GetPermissionData();
            data.ApiKeys.Single(k => k.Id == key.Id).AllowedTools = ["start_container", "list_servers"];
            await permissions.SavePermissionDataAsync(data);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Key);

            var session = await McpServedSurfaceTests.InitializeAsync(client);
            using var response = await McpServedSurfaceTests.PostAsync(client, session,
                """{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"start_container","arguments":{"containerId":"web-1"}}}""");
            var payload = McpServedSurfaceTests.ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync());

            var text = System.Text.Json.JsonDocument.Parse(payload).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Contains("requires 'write'", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previousDataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task A_tool_that_answers_with_an_error_text_is_failed_in_the_audit_and_an_error_for_the_client()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-toolfailure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var previousDataDir = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
        try
        {
            await using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.UseEnvironment("Development"));
            using var client = factory.CreateClient();

            // Writes run without a human here, so the call reaches the tool.
            var permissions = factory.Services.GetRequiredService<IMcpPermissionService>();
            var key = await permissions.AddApiKeyAsync("ops", McpPermissionLevels.Admin);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Key);
            var guardrails = factory.Services.GetRequiredService<Whiskers.Services.Guardrails.IGuardrailStore>();
            var config = guardrails.Config;
            var policy = config.ActivePolicy();
            policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
            policy.RequireConfirmationForWrites = false;
            await guardrails.SaveConfigAsync(config,
                new AgentPrincipal(AgentPrincipalKind.WebUser, "admin", McpPermissionLevels.Admin, null, UserEmail: "admin@example.com"));

            var session = await McpServedSurfaceTests.InitializeAsync(client);
            using var response = await McpServedSurfaceTests.PostAsync(client, session,
                """{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"create_scheduled_task","arguments":{"name":"x","cronExpression":"0 2 * * *","taskType":"Bogus"}}}""");
            var payload = McpServedSurfaceTests.ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync());

            var result = System.Text.Json.JsonDocument.Parse(payload).RootElement.GetProperty("result");
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Contains("Invalid task type", result.GetProperty("content")[0].GetProperty("text").GetString());

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Whiskers.Services.Persistence.MetricsDbContext>();
            var row = db.AuditLog.Single(r => r.Action == "scheduler.create");
            Assert.False(row.Success);
            Assert.Contains("\"status\":\"Failed\"", row.Details);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previousDataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
