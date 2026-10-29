using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

// Direct MCP tool calls (an external client, not the in-process agent) pass the same guardrail policy as the
// agent. Before, they were bounded by the key's scope only: a Write key could restart a protected container
// or run a write tool that the policy says needs a human.
public sealed class DirectMcpCallGuardTests
{
    private static readonly AgentPrincipal WriteKey =
        new(AgentPrincipalKind.McpKey, "ci", McpPermissionLevels.Write, null, McpKeyId: "k1");

    private static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] args) =>
        args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value));

    private static (DirectMcpCallGuard Guard, ApprovalStore Approvals) Build(GuardrailPolicy policy)
    {
        var approvals = new ApprovalStore();
        var store = new FixedPolicy(policy);
        return (new DirectMcpCallGuard(GuardrailEngine.CreateDefault(), store, new ApprovalGate(approvals, store)), approvals);
    }

    [Fact]
    public async Task A_read_tool_inside_the_autonomous_level_runs()
    {
        var (guard, approvals) = Build(GuardrailPolicy.SafeDefault());

        var v = await guard.EvaluateAsync(WriteKey, "list_servers", Args());

        Assert.True(v.Allowed);
        Assert.Empty(approvals.GetAll());
    }

    [Fact]
    public async Task A_protected_resource_is_denied_even_though_the_key_scope_allows_the_tool()
    {
        var (guard, approvals) = Build(GuardrailPolicy.SafeDefault());

        var v = await guard.EvaluateAsync(WriteKey, "restart_container", Args(("containerId", "whiskers")));

        Assert.False(v.Allowed);
        Assert.Equal(GuardrailVerdict.Deny, v.Verdict);
        Assert.Empty(approvals.GetAll());   // a Deny never turns into an approval request
    }

    [Fact]
    public async Task Confirm_raises_one_approval_and_the_identical_call_runs_once_after_a_human_approves()
    {
        var (guard, approvals) = Build(GuardrailPolicy.SafeDefault());
        var args = Args(("containerId", "web-1"));

        var first = await guard.EvaluateAsync(WriteKey, "restart_container", args);
        Assert.False(first.Allowed);
        Assert.Equal(GuardrailVerdict.Confirm, first.Verdict);
        var approval = Assert.Single(approvals.GetPending());
        Assert.Equal("restart_container", approval.ToolName);
        Assert.Equal("mcp-direct", approval.ActorType);

        // Retrying before the decision raises no second approval.
        Assert.False((await guard.EvaluateAsync(WriteKey, "restart_container", args)).Allowed);
        Assert.Single(approvals.GetAll());

        // A Read user may not approve a write action; a Write user may.
        Assert.False(await approvals.ResolveAsync(approval.Id, true, "viewer@x", McpPermissionLevels.Read));
        Assert.True(await approvals.ResolveAsync(approval.Id, true, "op@x", McpPermissionLevels.Write));

        // Different arguments are a different action: not covered by the approval.
        Assert.False((await guard.EvaluateAsync(WriteKey, "restart_container", Args(("containerId", "db-1")))).Allowed);
        // Another key repeating the approved call is not covered either.
        var otherKey = WriteKey with { McpKeyId = "k2" };
        Assert.False((await guard.EvaluateAsync(otherKey, "restart_container", args)).Allowed);

        Assert.True((await guard.EvaluateAsync(WriteKey, "restart_container", args)).Allowed);
        // Single use: the next identical call needs a new approval.
        Assert.False((await guard.EvaluateAsync(WriteKey, "restart_container", args)).Allowed);
    }

    [Fact]
    public async Task A_rejected_approval_lets_nothing_through()
    {
        var (guard, approvals) = Build(GuardrailPolicy.SafeDefault());
        var args = Args(("containerId", "web-1"));

        await guard.EvaluateAsync(WriteKey, "restart_container", args);
        await approvals.ResolveAsync(Assert.Single(approvals.GetPending()).Id, false, "op@x", McpPermissionLevels.Write);

        Assert.False((await guard.EvaluateAsync(WriteKey, "restart_container", args)).Allowed);
        Assert.Single(approvals.GetPending());   // a fresh request, not the rejected one revived
    }

    [Fact]
    public async Task Read_only_mode_blocks_writes_from_external_clients_too()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;
        var (guard, _) = Build(policy);

        var v = await guard.EvaluateAsync(WriteKey, "restart_container", Args(("containerId", "web-1")));

        Assert.Equal(GuardrailVerdict.Deny, v.Verdict);
    }

    private sealed class FixedPolicy(GuardrailPolicy policy) : IGuardrailStore
    {
        public GuardrailPolicy Current => policy;
        public GuardrailConfig Config { get; } = new();
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy p, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }
}

/// <summary>The same rules over the real wire: boots the app and sends tools/call to /mcp.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class DirectMcpCallGuardWireTests
{
    [Fact]
    public async Task The_running_server_applies_guardrails_to_direct_tool_calls()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-mcpguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var previousDataDir = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        try
        {
            using var bootSettings = BootSettings.Apply(BootSettings.AuthDisabled);   // read eagerly by Program
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development");
                    b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(BootSettings.AuthDisabled));
                    // The gateway resolves the container before policy runs, so the containers must exist.
                    b.ConfigureTestServices(s =>
                    {
                        s.AddSingleton<Whiskers.Services.Docker.IDockerService>(new FakeDocker(
                            new ContainerInfo { Id = "c-whiskers", Name = "whiskers" },
                            new ContainerInfo { Id = "c-web-1", Name = "web-1" }));
                        // The target resolver decides the server first (R1-06): a call without one acts on the default server.
                        s.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(
                            new ServerConfig { Id = "local", Name = "Local", IsDefault = true }));
                    });
                });
            using var client = factory.CreateClient();
            BootSettings.AssertBoot(factory.Services, BootSettings.AuthDisabled);
            var session = await McpServedSurfaceTests.InitializeAsync(client);

            // Protected resource (safe default protects whiskers*) → denied before the tool runs.
            var denied = await CallAsync(client, session, 10, "restart_container", """{"containerId":"whiskers"}""");
            Assert.Contains("Blocked by guardrails", denied);

            // Write tool under the safe default → approval required, and the approval exists.
            var confirm = await CallAsync(client, session, 11, "restart_container", """{"containerId":"web-1"}""");
            Assert.Contains("Human approval required", confirm);
            var approvals = factory.Services.GetRequiredService<IApprovalStore>();
            var approval = Assert.Single(approvals.GetPending());
            Assert.Equal("restart_container", approval.ToolName);

            // After approval the identical call reaches the tool (which may then fail for its own reasons —
            // there is no Docker here — but no longer on the guardrail).
            Assert.True(await approvals.ResolveAsync(approval.Id, true, "admin@test", McpPermissionLevels.Admin));
            var after = await CallAsync(client, session, 12, "restart_container", """{"containerId":"web-1"}""");
            Assert.DoesNotContain("Human approval required", after);
            Assert.DoesNotContain("Blocked by guardrails", after);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previousDataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static async Task<string> CallAsync(HttpClient client, string session, int id, string tool, string argsJson)
    {
        using var response = await McpServedSurfaceTests.PostAsync(client, session,
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{argsJson}}}}}""");
        Assert.True(response.IsSuccessStatusCode, $"tools/call failed with {(int)response.StatusCode}");
        return McpServedSurfaceTests.ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync());
    }
}
