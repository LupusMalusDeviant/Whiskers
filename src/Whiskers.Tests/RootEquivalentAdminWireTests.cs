using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Whiskers.Services.Terminal;
using Host = Whiskers.Tests.ReadOnlyKillSwitchWireTests.KillSwitchHost;

namespace Whiskers.Tests;

/// <summary>
/// INV-19 on the booted application: every root-equivalent capability (<see cref="CapabilityCatalog.RootEquivalent"/>)
/// is driven through every surface that can reach it — the served <c>/mcp</c> endpoint with a read, a write and an
/// admin key; the agent's tool invoker for a Viewer, an Operator and an Admin; the host's own UI runner (pages,
/// "Run now", host and container terminals) for each role; the automation runner (scheduled runs, webhooks) for an
/// owner at each level; and the scheduler's own execution of a stored custom-command task.
/// <para>The proof that only Admin passes: the read-only kill switch is on, and the level check comes before the
/// policy step. A caller below Admin is refused by its level — no policy decision is recorded, the reason names the
/// Admin requirement. An Admin caller passes the level check and reaches the policy, where the kill switch is the rule
/// that refuses it. So nothing runs on any surface (no container action, no shell, no stored task), and the decision
/// record shows exactly who got past the level step.</para>
/// <para>The whole host is the production DI graph (gateway, policy, scheduler requirement resolver, approval gate,
/// audit, terminal manager, SQLite); only Docker, the server registry, the signed-in person and the terminal launcher
/// are doubles (<see cref="ReadOnlyKillSwitchWireTests.KillSwitchHost"/>).</para>
/// </summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class RootEquivalentAdminWireTests
{
    private const string KillSwitchRule = "read-only-mode";
    private static readonly string[] Levels = [McpPermissionLevels.Read, McpPermissionLevels.Write, McpPermissionLevels.Admin];

    [Fact]
    public async Task Over_mcp_only_an_admin_key_gets_past_the_level_of_a_root_equivalent_tool()
    {
        await using var host = await Host.StartAsync(everyTargetKnown: true);
        var commandTask = await SeedCommandTaskAsync(host, owner: PrincipalIds.Key(host.ApiKeyId));
        using var keys = await KeysAsync(host);
        var catalog = host.Factory.Services.GetRequiredService<ICapabilityCatalog>();

        var adminSession = await McpServedSurfaceTests.InitializeAsync(host.Client);
        var served = (await ReadOnlyKillSwitchWireTests.ListToolsAsync(host.Client, adminSession))
            .Where(t => catalog.FindByMcpTool(t.Name) is { } c && CapabilityCatalog.RootEquivalent.Contains(c.Id))
            .ToList();
        // Every root-equivalent capability with an MCP tool is served and swept.
        Assert.Equal(catalog.All.Where(c => c.McpTool is not null && CapabilityCatalog.RootEquivalent.Contains(c.Id))
                .Select(c => c.McpTool!).Order(StringComparer.Ordinal),
            served.Select(t => t.Name).Order(StringComparer.Ordinal));

        var id = 5000;
        foreach (var (level, keyId, client) in keys)
        {
            var session = await McpServedSurfaceTests.InitializeAsync(client);
            foreach (var (tool, schema) in served)
                await ReadOnlyKillSwitchWireTests.CallToolAsync(client, session, id++, tool,
                    McpArguments(catalog.FindByMcpTool(tool)!.Id, schema, commandTask).ToJsonString());
        }

        AssertNothingRan(host);
        using var db = host.Db();
        var decisions = db.Context.PolicyDecisions.ToList().ToDictionary(d => d.Id);
        foreach (var (level, keyId, _) in keys)
            foreach (var (tool, _) in served)
            {
                var capability = catalog.FindByMcpTool(tool)!.Id;
                var row = Assert.Single(db.Context.AuditLog
                    .Where(r => r.Action == capability && r.PrincipalId == PrincipalIds.Key(keyId)).ToList());
                AssertDecided(row, decisions, level, $"mcp {tool} as a {level} key");
                Assert.Equal(nameof(MutationSurface.Mcp), row.Surface);
            }
    }

    [Fact]
    public async Task For_the_agent_only_an_admin_principal_gets_past_the_level_of_a_root_equivalent_tool()
    {
        await using var host = await Host.StartAsync(everyTargetKnown: true);
        var commandTask = await SeedCommandTaskAsync(host, owner: PrincipalIds.Key(host.ApiKeyId));
        var catalog = host.Factory.Services.GetRequiredService<ICapabilityCatalog>();
        var invoker = host.Factory.Services.GetRequiredService<IAgentToolInvoker>();
        var registry = host.Factory.Services.GetRequiredService<IAgentToolRegistry>();
        var guardrails = host.Factory.Services.GetRequiredService<IGuardrailStore>();

        var session0 = await McpServedSurfaceTests.InitializeAsync(host.Client);
        var schemas = (await ReadOnlyKillSwitchWireTests.ListToolsAsync(host.Client, session0)).ToDictionary(t => t.Name, t => t.Schema);
        var tools = registry.Tools.Keys
            .Where(t => catalog.FindByMcpTool(t) is { } c && CapabilityCatalog.RootEquivalent.Contains(c.Id))
            .Order(StringComparer.Ordinal).ToList();
        // The agent can reach every root-equivalent tool the MCP surface serves except the provisioning tools it is
        // never given (AgentToolRegistry.NonAgentTools): none is left out of the sweep.
        Assert.Equal(catalog.All.Where(c => c.McpTool is not null && CapabilityCatalog.RootEquivalent.Contains(c.Id)
                                            && !AgentToolRegistry.NonAgentTools.Contains(c.McpTool))
            .Select(c => c.McpTool!).Order(StringComparer.Ordinal), tools);
        Assert.All(AgentToolRegistry.NonAgentTools, t => Assert.False(registry.Tools.ContainsKey(t), t));

        var sessions = new Dictionary<string, string>();
        foreach (var level in Levels)
        {
            var email = $"{level}@example.com";
            var context = new AgentContext("s-" + level, new AgentPrincipal(AgentPrincipalKind.WebUser, email, level, null,
                UserEmail: email), AgentOrigin.WebUi, guardrails.Current);
            sessions[level] = PrincipalIds.Agent(context.SessionId);
            foreach (var tool in tools)
            {
                var result = await invoker.InvokeAsync(new AgentToolCall("call-" + tool, tool,
                    McpArguments(catalog.FindByMcpTool(tool)!.Id, schemas[tool], commandTask).ToJsonString()), context);
                Assert.True(result.IsError, $"agent {tool} as {level}: ran");
                Assert.Equal(GuardrailVerdict.Deny, result.Decision.Verdict);
                if (level == McpPermissionLevels.Admin)
                    Assert.Contains(KillSwitchRule, result.Decision.MatchedRuleIds);
                else
                    Assert.DoesNotContain(KillSwitchRule, result.Decision.MatchedRuleIds);
            }
        }

        // Review m5: the two other principal kinds that reach the invoker — a key instructing the agent over MCP
        // (instruct_agent) and an AI trigger — are held to the same level: at Write every member is refused by it.
        var permissions = host.Factory.Services.GetRequiredService<IMcpPermissionService>();
        var writeKey = await GatewayArrange.RunAsync("mcp.key_create", () => permissions.AddApiKeyAsync("agent-write-key", McpPermissionLevels.Write));
        var others = new[]
        {
            new AgentContext("s-key", new AgentPrincipal(AgentPrincipalKind.McpKey, "agent-write-key", McpPermissionLevels.Write,
                null, McpKeyId: writeKey.Id), AgentOrigin.McpTool, guardrails.Current),
            new AgentContext("s-trigger", new AgentPrincipal(AgentPrincipalKind.Trigger, "AI trigger 'nightly'",
                McpPermissionLevels.Write, null, TriggerId: "trigger-1"), AgentOrigin.Trigger, guardrails.Current),
        };
        foreach (var context in others)
            foreach (var tool in tools)
            {
                var result = await invoker.InvokeAsync(new AgentToolCall("call-" + tool, tool,
                    McpArguments(catalog.FindByMcpTool(tool)!.Id, schemas[tool], commandTask).ToJsonString()), context);
                Assert.True(result.IsError, $"agent {tool} for {context.Principal.Kind}: ran");
                Assert.Equal(GuardrailVerdict.Deny, result.Decision.Verdict);
                Assert.DoesNotContain(KillSwitchRule, result.Decision.MatchedRuleIds);
                Assert.Contains("requires 'admin'", result.Decision.Reason);
            }

        AssertNothingRan(host);
        using var db = host.Db();
        var decisions = db.Context.PolicyDecisions.ToList().ToDictionary(d => d.Id);
        foreach (var context in others)
            foreach (var tool in tools)
            {
                var capability = catalog.FindByMcpTool(tool)!.Id;
                var row = Assert.Single(db.Context.AuditLog
                    .Where(r => r.Action == capability && r.PrincipalId == PrincipalIds.Agent(context.SessionId)).ToList());
                AssertDecided(row, decisions, McpPermissionLevels.Write, $"agent {tool} for {context.Principal.Kind}");
            }
        foreach (var level in Levels)
            foreach (var tool in tools)
            {
                var capability = catalog.FindByMcpTool(tool)!.Id;
                var row = Assert.Single(db.Context.AuditLog
                    .Where(r => r.Action == capability && r.PrincipalId == sessions[level]).ToList());
                AssertDecided(row, decisions, level, $"agent {tool} for a {level} user");
                Assert.Equal(nameof(MutationSurface.Agent), row.Surface);
            }
    }

    /// <summary>Pages, "Run now", both terminals: every root-equivalent capability through the host's UI runner, as a
    /// Viewer, an Operator and an Admin. The terminals are opened as the pages call them (the terminal manager's own scope guard is
    /// pinned by <c>TerminalAuditTests</c>; every call here is refused before it would run).</summary>
    [Fact]
    public async Task In_the_ui_only_an_admin_gets_past_the_level_of_a_root_equivalent_capability()
    {
        await using var host = await Host.StartAsync(everyTargetKnown: true);
        var commandTask = await SeedCommandTaskAsync(host, owner: PrincipalIds.User(ReadOnlyKillSwitchWireTests.AdminEmail));
        var terminals = host.Factory.Services.GetRequiredService<ITerminalSessionManager>();
        var ran = new List<string>();

        foreach (var role in new[] { AppRole.Viewer, AppRole.Operator, AppRole.Admin })
        {
            host.User.Role = role;
            await using var scope = host.Factory.Services.CreateAsyncScope();
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            foreach (var id in CapabilityCatalog.RootEquivalent.Order(StringComparer.Ordinal))
            {
                Task Action(CancellationToken ct) => id switch
                {
                    "terminal.host_open" => terminals.OpenHostShellAsync(new TerminalOpenRequest(TerminalSession.NewId(), "local"), ct),
                    "terminal.container_open" => terminals.OpenContainerShellAsync(
                        new TerminalOpenRequest(TerminalSession.NewId(), "local", "web-1"), ct),
                    _ => Record(id),
                };
                Task Record(string capability)
                {
                    lock (ran) ran.Add($"{role}:{capability}");
                    return Task.CompletedTask;
                }

                if (role == AppRole.Admin && id == GuardrailMutationPolicy.KillSwitchOff)
                {
                    // Contract 0009 E1: switching the kill switch off is the one write it lets through — for an Admin
                    // in the UI. Here the action is the recorder, so the Admin is seen to get past the level.
                    await mutations.RunAsync(id, UiArguments(commandTask), Action);
                    continue;
                }
                var refused = await Assert.ThrowsAsync<MutationRefusedException>(() =>
                    mutations.RunAsync(id, UiArguments(commandTask), Action));
                Assert.Equal(MutationStatus.Denied, refused.Status);
                if (role == AppRole.Admin)
                    Assert.True(refused.MatchedRules.Contains(KillSwitchRule), $"ui {id} as Admin: {refused.Message}");
                else
                {
                    Assert.DoesNotContain(KillSwitchRule, refused.MatchedRules);
                    Assert.Contains("requires 'admin'", refused.Message);
                }
            }
        }

        Assert.Equal([$"{AppRole.Admin}:{GuardrailMutationPolicy.KillSwitchOff}"], ran);
        AssertNothingRan(host);
        Assert.Empty(terminals.GetActiveSessions());
        using var db = host.Db();
        var decisions = db.Context.PolicyDecisions.ToList().ToDictionary(d => d.Id);
        foreach (var id in CapabilityCatalog.RootEquivalent)
        {
            var rows = db.Context.AuditLog.Where(r => r.Action == id && r.Surface == nameof(MutationSurface.Ui)).ToList();
            Assert.True(rows.Count == 3, $"ui {id}: {rows.Count} audit rows");
            // Two refused by their level (Viewer, Operator) before any policy decision.
            var byLevel = rows.Where(r => r.DecisionId is null).ToList();
            Assert.Equal(2, byLevel.Count);
            Assert.All(byLevel, r => Assert.True(r.Outcome == nameof(MutationStatus.Denied) && r.Details!.Contains("requires 'admin'"),
                $"ui {id}: {r.Outcome} {r.Details}"));
            // The Admin got past the level: refused by the kill switch in the policy, or — E1 only — executed.
            var admin = Assert.Single(rows, r => r.DecisionId is not null);
            if (id == GuardrailMutationPolicy.KillSwitchOff)
                Assert.Equal(nameof(MutationStatus.Executed), admin.Outcome);
            else
                Assert.True(admin.Outcome == nameof(MutationStatus.Denied) && decisions[admin.DecisionId!].MatchedRules.Contains(KillSwitchRule),
                    $"ui {id} as Admin: {admin.Outcome} {admin.Details}");
        }
    }

    /// <summary>Unattended runs — scheduled tasks and webhooks — run on their owner's authority through the
    /// automation runner: an owner below Admin is refused by its level for every root-equivalent capability.</summary>
    [Fact]
    public async Task An_unattended_run_gets_past_the_level_of_a_root_equivalent_capability_only_for_an_admin_owner()
    {
        await using var host = await Host.StartAsync(everyTargetKnown: true);
        using var keys = await KeysAsync(host);
        var commandTask = await SeedCommandTaskAsync(host, owner: PrincipalIds.Key(host.ApiKeyId));
        var automation = host.Factory.Services.GetRequiredService<IAutomationMutationRunner>();
        var ran = new List<string>();

        foreach (var (level, keyId, _) in keys)
            foreach (var id in CapabilityCatalog.RootEquivalent.Order(StringComparer.Ordinal))
            {
                var outcome = await automation.RunAsync(new AutomationActor($"run:{level}", "test run", PrincipalIds.Key(keyId)),
                    id, UiArguments(commandTask), _ => { lock (ran) ran.Add(id); return Task.CompletedTask; });
                Assert.Equal(MutationStatus.Denied, outcome.Status);
                if (level == McpPermissionLevels.Admin)
                    Assert.True(outcome.MatchedRules.Contains(KillSwitchRule), $"automation {id} for an admin owner: {outcome.Reason}");
                else
                {
                    Assert.DoesNotContain(KillSwitchRule, outcome.MatchedRules);
                    Assert.Contains("requires 'admin'", outcome.Reason);
                }
            }

        Assert.Empty(ran);
        AssertNothingRan(host);
        using var db = host.Db();
        var decisions = db.Context.PolicyDecisions.ToList().ToDictionary(d => d.Id);
        foreach (var (level, _, _) in keys)
            foreach (var id in CapabilityCatalog.RootEquivalent)
            {
                var row = Assert.Single(db.Context.AuditLog.Where(r => r.Action == id && r.PrincipalId == $"run:{level}").ToList());
                AssertDecided(row, decisions, level, $"automation {id} for a {level} owner");
                Assert.Equal(nameof(MutationSurface.Automation), row.Surface);
            }
    }

    /// <summary>The scheduler's own execution of a stored custom-command task (on schedule, and what "Run now" hands
    /// to it): the command is decided as <c>host.exec</c> on the owner's authority at run time. An Operator-owned
    /// task does not run it; an Admin-owned one gets past the level and is stopped here by the kill switch.</summary>
    [Fact]
    public async Task A_stored_custom_command_runs_only_on_an_admin_owners_authority()
    {
        await using var host = await Host.StartAsync(everyTargetKnown: true);
        using var keys = await KeysAsync(host);
        var executor = host.Factory.Services.GetRequiredService<ITaskExecutor>();

        var tasks = new Dictionary<string, ScheduledTaskEntity>();
        foreach (var (level, keyId, _) in keys)
        {
            var taskId = await SeedCommandTaskAsync(host, owner: PrincipalIds.Key(keyId));
            using var db0 = host.Db();
            tasks[level] = db0.Context.ScheduledTasks.Single(t => t.TaskId == taskId);
        }

        foreach (var (level, task) in tasks)
        {
            var (success, _) = await executor.ExecuteAsync(task);
            Assert.False(success, $"the {level}-owned command task ran");
        }

        AssertNothingRan(host);
        using var db = host.Db();
        var decisions = db.Context.PolicyDecisions.ToList().ToDictionary(d => d.Id);
        foreach (var (level, task) in tasks)
        {
            var row = Assert.Single(db.Context.AuditLog
                .Where(r => r.Action == "host.exec" && r.PrincipalId == "task:" + task.TaskId).ToList());
            Assert.Equal(nameof(MutationSurface.Automation), row.Surface);
            AssertDecided(row, decisions, level, $"stored command task owned by a {level} key");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A Denied row: below Admin refused by the level before any policy decision, Admin refused by the kill
    /// switch in the recorded decision.</summary>
    private static void AssertDecided(AuditLogEntity row,
        IReadOnlyDictionary<string, PolicyDecisionEntity> decisions, string level, string what)
    {
        Assert.True(row.Outcome == nameof(MutationStatus.Denied) && !row.Success, $"{what}: recorded as {row.Outcome ?? "(open)"}");
        if (level == McpPermissionLevels.Admin)
            Assert.True(row.DecisionId is { } d && decisions[d].MatchedRules.Contains(KillSwitchRule),
                $"{what}: did not reach the policy ({row.Details})");
        else
        {
            Assert.True(row.DecisionId is null, $"{what}: reached the policy although below Admin ({row.Details})");
            Assert.Contains("requires 'admin'", row.Details);
        }
    }

    private static void AssertNothingRan(Host host)
    {
        Assert.Empty(host.Docker.Actions);
        Assert.Equal(0, host.Launcher.Starts);
    }

    /// <summary>A read, a write and the host's admin key, each with a client of its own.</summary>
    private static async Task<KeySet> KeysAsync(Host host)
    {
        var permissions = host.Factory.Services.GetRequiredService<IMcpPermissionService>();
        var result = new KeySet(host.Client);
        foreach (var level in new[] { McpPermissionLevels.Read, McpPermissionLevels.Write })
        {
            var key = await GatewayArrange.RunAsync("mcp.key_create", () => permissions.AddApiKeyAsync(level + "-key", level));
            var client = host.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Key);
            result.Add((level, key.Id, client));
        }
        result.Add((McpPermissionLevels.Admin, host.ApiKeyId, host.Client));
        return result;
    }

    /// <summary>The keys of a test and their clients; disposing it disposes the clients it made (not the host's).</summary>
    private sealed class KeySet(HttpClient hostClient) : List<(string Level, string KeyId, HttpClient Client)>, IDisposable
    {
        public void Dispose()
        {
            foreach (var (_, _, client) in this)
                if (!ReferenceEquals(client, hostClient)) client.Dispose();
        }
    }

    /// <summary>Stores a custom-command task (an arrangement, not under test) and returns its id.</summary>
    private static async Task<string> SeedCommandTaskAsync(Host host, string owner)
    {
        var scheduler = host.Factory.Services.GetRequiredService<ISchedulerService>();
        var task = new ScheduledTaskEntity
        {
            Name = "nightly command", CronExpression = "0 2 * * *", TaskType = ScheduledTaskType.CustomCommand,
            // Off, so the hosted scheduler never fires it itself if a run spans 02:00 UTC (review m4); the paths under test
            // (the tools, "Run now", the executor) read the stored task whatever its switch says.
            Enabled = false, ServerId = "local", Config = "{\"command\":\"id\"}", CreatedBy = owner, CreatedVia = owner,
        };
        await GatewayArrange.RunAsync("scheduler.create", () => scheduler.CreateTaskAsync(task));
        return task.TaskId;
    }

    /// <summary>The tool's required arguments; the scheduler tools get a custom command to create and the stored
    /// command task to run.</summary>
    private static JsonObject McpArguments(string capabilityId, JsonElement schema, string commandTaskId)
    {
        var args = ReadOnlyKillSwitchWireTests.ArgumentsFor(schema);
        switch (capabilityId)
        {
            case "scheduler.create":
                args["name"] = "nightly";
                args["cronExpression"] = "0 2 * * *";
                args["taskType"] = "CustomCommand";
                args["command"] = "id";
                break;
            case "scheduler.run":
                args["taskId"] = commandTaskId;
                break;
        }
        return args;
    }

    /// <summary>The keys the capability table reads a target from (as in the kill-switch sweep), plus a command task.</summary>
    private static Dictionary<string, object?> UiArguments(string commandTaskId) => new()
    {
        ["containerId"] = "web-1", ["serverId"] = "local", ["section"] = "guardrails", ["name"] = "web-1",
        ["networkId"] = "net-1", ["zone"] = "example.com", ["server"] = "cloud-1", ["volumeName"] = "data",
        ["workloadId"] = "default/web", ["appId"] = "app-1", ["webhookId"] = "hook-1", ["account"] = "someone@example.com",
        ["binding"] = "binding-1", ["command"] = "id", ["query"] = "select 1",
        ["taskId"] = commandTaskId, ["taskType"] = "CustomCommand", ["cronExpression"] = "0 2 * * *",
    };
}
