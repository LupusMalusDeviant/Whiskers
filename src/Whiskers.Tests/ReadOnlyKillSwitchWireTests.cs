using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Docker;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Terminal;

namespace Whiskers.Tests;

/// <summary>
/// INV-21 on the booted application (WP6): with the read-only kill switch on, every mutating MCP tool sent over the real
/// <c>/mcp</c> endpoint and every mutating UI capability run through the host's own UI runner is denied before it runs,
/// recorded as Denied with the kill switch as the deciding rule, and no terminal session opens. The one exception is
/// switching the kill switch off: a person in the UI with Admin, audited — not an Operator, not an Admin API key.
/// <para>The whole host is the production DI graph (gateway, policy, guardrail store, audit, terminal manager, SQLite);
/// only Docker, the server registry, the signed-in person and the terminal launcher are doubles, so a call that got
/// through would be seen, and no real shell could start.</para>
/// <para>Read-level capabilities are outside the kill switch by decision: the own password change (contract 0002,
/// decision 4) keeps a person's own access, as recovery codes and the host CLI do (decision 8).</para>
/// </summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class ReadOnlyKillSwitchWireTests
{
    private const string ContainerId = "c0ffee0000a1";
    private const string AdminEmail = "admin@example.com";

    [Fact]
    public async Task Over_mcp_a_mutating_tool_is_denied_by_the_kill_switch_before_it_runs_and_recorded_as_denied()
    {
        await using var host = await KillSwitchHost.StartAsync();
        var session = await McpServedSurfaceTests.InitializeAsync(host.Client);

        var calls = new (string Tool, string Capability, string Args)[]
        {
            ("restart_container", "container.restart", """{"containerId":"web-1"}"""),
            ("stop_container", "container.stop", """{"containerId":"web-1"}"""),
            ("create_scheduled_task", "scheduler.create",
                """{"name":"nightly","cronExpression":"0 2 * * *","taskType":"ContainerRestart","targetId":"web-1"}"""),
        };
        var id = 100;
        foreach (var (tool, _, args) in calls)
        {
            var text = await CallToolAsync(host.Client, session, id++, tool, args);
            Assert.Contains("Read-only mode active", text);
        }

        // Nothing ran: no container action, no task stored.
        Assert.Empty(host.Docker.Actions);
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Whiskers.Services.Persistence.MetricsDbContext>();
            Assert.Empty(db.ScheduledTasks);
            foreach (var (_, capability, _) in calls)
            {
                var row = Assert.Single(db.AuditLog.Where(r => r.Action == capability).ToList());
                Assert.Equal(nameof(MutationStatus.Denied), row.Outcome);
                Assert.False(row.Success);
                Assert.Equal(nameof(MutationSurface.Mcp), row.Surface);
                Assert.Equal(PrincipalIds.Key(host.ApiKeyId), row.PrincipalId);
                Assert.Contains("Read-only mode active", row.Details);
                // The recorded decision names the kill switch as the rule that denied it (INV-22).
                var decision = db.PolicyDecisions.Single(d => d.Id == row.DecisionId);
                Assert.Equal(nameof(MutationVerdict.Deny), decision.Verdict);
                Assert.Contains("read-only-mode", decision.MatchedRules);
            }
        }

        // Reads still answer: the switch stops writes, not the server.
        var listing = await CallToolAsync(host.Client, session, id, "list_containers", "{}");
        Assert.Contains("web-1", listing);
    }

    [Fact]
    public async Task Over_mcp_no_mutating_tool_runs_while_the_kill_switch_is_on()
    {
        await using var host = await KillSwitchHost.StartAsync(everyTargetKnown: true);
        var session = await McpServedSurfaceTests.InitializeAsync(host.Client);
        var catalog = host.Factory.Services.GetRequiredService<ICapabilityCatalog>();

        // Every served tool that performs a mutating capability, called with arguments its own schema asks for.
        var tools = await ListToolsAsync(host.Client, session);
        var mutating = tools
            .Select(t => (Tool: t, Capability: catalog.FindByMcpTool(t.Name)))
            .Where(t => t.Capability is { } c && McpPermissionLevels.Normalize(c.RequiredLevel) != McpPermissionLevels.Read)
            .ToList();
        Assert.True(mutating.Count >= 30, $"only {mutating.Count} mutating tools are served — the sweep would prove little");

        var id = 1000;
        foreach (var (tool, _) in mutating)
            await CallToolAsync(host.Client, session, id++, tool.Name, ArgumentsFor(tool.Schema).ToJsonString());

        Assert.Empty(host.Docker.Actions);
        Assert.Equal(0, host.Launcher.Starts);
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Whiskers.Services.Persistence.MetricsDbContext>();
        Assert.Empty(db.ScheduledTasks);
        var rows = db.AuditLog.Where(r => r.Surface == nameof(MutationSurface.Mcp)).ToList();
        var decisions = db.PolicyDecisions.ToList().ToDictionary(d => d.Id);
        foreach (var (tool, capability) in mutating)
        {
            // Each call is on record as Denied — none ran, none failed while running, none is left open.
            var own = rows.Where(r => r.Action == capability!.Id).ToList();
            Assert.True(own.Count > 0, $"{tool.Name}: no audit event");
            Assert.All(own, r => Assert.True(r.Outcome == nameof(MutationStatus.Denied) && !r.Success,
                $"{tool.Name}: recorded as {r.Outcome ?? "(open)"}"));
            // Every call reached the policy, and the kill switch is the rule that denied it.
            Assert.All(own, r => Assert.True(r.DecisionId is { } d && decisions[d].Verdict == nameof(MutationVerdict.Deny)
                && decisions[d].MatchedRules.Contains("read-only-mode"), $"{tool.Name}: not denied by the kill switch ({r.Details})"));
        }
        Assert.All(rows, r => Assert.Equal(nameof(MutationStatus.Denied), r.Outcome));
    }

    [Fact]
    public async Task No_terminal_session_opens_while_the_kill_switch_is_on()
    {
        await using var host = await KillSwitchHost.StartAsync();
        var terminals = host.Factory.Services.GetRequiredService<ITerminalSessionManager>();

        // As ServerTerminalView and TerminalView open them: through the UI runner, by an Admin.
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            var hostShell = await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync("terminal.host_open",
                new { serverId = "local" },
                ct => terminals.OpenHostShellAsync(new TerminalOpenRequest(TerminalSession.NewId(), "local"), ct)));
            Assert.Equal(MutationStatus.Denied, hostShell.Status);
            var containerShell = await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync("terminal.container_open",
                new { containerId = "web-1", serverId = "local" },
                ct => terminals.OpenContainerShellAsync(new TerminalOpenRequest(TerminalSession.NewId(), "local", "web-1"), ct)));
            Assert.Equal(MutationStatus.Denied, containerShell.Status);
        }

        Assert.Equal(0, host.Launcher.Starts);
        Assert.Empty(terminals.GetActiveSessions());
        using var db = host.Db();
        foreach (var capability in new[] { "terminal.host_open", "terminal.container_open" })
        {
            var row = Assert.Single(db.Context.AuditLog.Where(r => r.Action == capability).ToList());
            Assert.Equal(nameof(MutationStatus.Denied), row.Outcome);
            Assert.Equal(nameof(MutationSurface.Ui), row.Surface);
            Assert.Equal(PrincipalIds.User(AdminEmail), row.PrincipalId);
            Assert.Contains("read-only-mode", db.Context.PolicyDecisions.Single(d => d.Id == row.DecisionId).MatchedRules);
        }
    }

    [Fact]
    public async Task In_the_ui_every_mutation_but_switching_the_kill_switch_off_is_refused_for_an_admin()
    {
        await using var host = await KillSwitchHost.StartAsync(everyTargetKnown: true);
        var catalog = host.Factory.Services.GetRequiredService<ICapabilityCatalog>();
        var guardrails = host.Factory.Services.GetRequiredService<IGuardrailStore>();
        var ui = catalog.All
            .Where(c => McpPermissionLevels.Normalize(c.RequiredLevel) != McpPermissionLevels.Read
                        && c.Id != GuardrailMutationPolicy.KillSwitchOff)
            .ToList();

        var ran = new List<string>();
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            foreach (var capability in ui)
            {
                var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync(capability.Id,
                    UiArguments, _ => { ran.Add(capability.Id); return Task.CompletedTask; }));
                Assert.Equal(MutationStatus.Denied, refused.Status);
            }

            // Saving the guardrails is refused too, even when the save would only switch the kill switch off.
            var config = guardrails.Config;
            config.ActivePolicy().ReadOnlyMode = false;
            await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync("guardrails.save",
                new { section = "guardrails" }, _ => guardrails.SaveConfigAsync(config, AdminEditor)));
        }

        Assert.Empty(ran);
        Assert.True(guardrails.Current.ReadOnlyMode);
        Assert.Empty(host.Docker.Actions);
        using var db = host.Db();
        var rows = db.Context.AuditLog.Where(r => r.Surface == nameof(MutationSurface.Ui)).ToList();
        var decisions = db.Context.PolicyDecisions.ToList().ToDictionary(d => d.Id);
        foreach (var capability in ui)
        {
            var own = rows.Where(r => r.Action == capability.Id).ToList();
            Assert.True(own.Count > 0, $"{capability.Id}: no audit event");
            Assert.All(own, r => Assert.Equal(nameof(MutationStatus.Denied), r.Outcome));
            Assert.All(own, r => Assert.True(r.DecisionId is { } d && decisions[d].Verdict == nameof(MutationVerdict.Deny)
                && decisions[d].MatchedRules.Contains("read-only-mode"), $"{capability.Id}: not denied by the kill switch ({r.Details})"));
        }
    }

    [Fact]
    public async Task Only_an_admin_in_the_ui_switches_the_kill_switch_off_and_the_switch_is_audited()
    {
        await using var host = await KillSwitchHost.StartAsync();
        var guardrails = host.Factory.Services.GetRequiredService<IGuardrailStore>();
        var gateway = host.Factory.Services.GetRequiredService<IMutationGateway>();

        // An Operator in the UI: refused, the switch stays on.
        host.User.Role = AppRole.Operator;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync("guardrails.kill_switch_off",
                new { section = "guardrails" }, _ => guardrails.DisableKillSwitchAsync(AdminEditor)));
        }
        Assert.True(guardrails.Current.ReadOnlyMode);

        // An Admin API key is no person in the UI: the kill switch holds against it.
        var key = new MutationPrincipal(MutationPrincipalKind.ApiKey, PrincipalIds.Key(host.ApiKeyId), "ops",
            McpPermissionLevels.Admin, CredentialId: host.ApiKeyId);
        var viaKey = await gateway.ExecuteAsync(new MutationRequest("guardrails.kill_switch_off", key, MutationSurface.Mcp,
                new MutationTarget("config", "guardrails"),
                new Dictionary<string, JsonElement> { ["section"] = JsonSerializer.SerializeToElement("guardrails") }),
            async (_, ct) => { await guardrails.DisableKillSwitchAsync(AdminEditor, ct); return true; });
        Assert.Equal(MutationStatus.Denied, viaKey.Status);
        Assert.True(guardrails.Current.ReadOnlyMode);

        // An Admin in the UI: switched off, nothing else changed, on record.
        host.User.Role = AppRole.Admin;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            await mutations.RunAsync("guardrails.kill_switch_off", new { section = "guardrails" },
                _ => guardrails.DisableKillSwitchAsync(AdminEditor));
        }
        Assert.False(guardrails.Current.ReadOnlyMode);
        Assert.Equal(McpPermissionLevels.Admin, guardrails.Current.MaxAutonomousLevel);
        Assert.False(guardrails.Current.RequireConfirmationForWrites);

        using (var db = host.Db())
        {
            var rows = db.Context.AuditLog.Where(r => r.Action == "guardrails.kill_switch_off").OrderBy(r => r.Id).ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal((nameof(MutationStatus.Denied), nameof(MutationSurface.Ui), PrincipalIds.User(AdminEmail)),
                (rows[0].Outcome, rows[0].Surface, rows[0].PrincipalId));
            Assert.Equal((nameof(MutationStatus.Denied), nameof(MutationSurface.Mcp)), (rows[1].Outcome, rows[1].Surface));
            Assert.Contains("read-only-mode", db.Context.PolicyDecisions.Single(d => d.Id == rows[1].DecisionId).MatchedRules);
            Assert.Equal((nameof(MutationStatus.Executed), nameof(MutationSurface.Ui), PrincipalIds.User(AdminEmail), true),
                (rows[2].Outcome, rows[2].Surface, rows[2].PrincipalId, rows[2].Success));
        }

        // With the switch off the same MCP call that was refused runs.
        var session = await McpServedSurfaceTests.InitializeAsync(host.Client);
        var text = await CallToolAsync(host.Client, session, 7, "restart_container", """{"containerId":"web-1"}""");
        Assert.DoesNotContain("Read-only mode active", text);
        Assert.Equal(("restart", ContainerId, (string?)"local"), Assert.Single(host.Docker.Actions));
    }

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Arguments for the UI sweep: the keys the capability table reads a target from, each naming something
    /// that exists where it can (the container, the server, the guardrail section).</summary>
    private static readonly object UiArguments = new
    {
        containerId = "web-1", serverId = "local", section = "guardrails", name = "web-1", networkId = "net-1",
        zone = "example.com", server = "cloud-1", taskId = "task-1", volumeName = "data", workloadId = "default/web",
        appId = "app-1", webhookId = "hook-1", account = "someone@example.com", binding = "binding-1",
    };

    private static readonly AgentPrincipal AdminEditor =
        new(AgentPrincipalKind.WebUser, AdminEmail, McpPermissionLevels.Admin, null, UserEmail: AdminEmail);

    private static async Task<string> CallToolAsync(HttpClient client, string session, int id, string tool, string argsJson)
    {
        using var response = await McpServedSurfaceTests.PostAsync(client, session,
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{argsJson}}}}}""");
        Assert.True(response.IsSuccessStatusCode, $"{tool}: tools/call failed with {(int)response.StatusCode}");
        return McpServedSurfaceTests.ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<(string Name, JsonElement Schema)>> ListToolsAsync(HttpClient client, string session)
    {
        using var response = await McpServedSurfaceTests.PostAsync(client, session,
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
        var payload = McpServedSurfaceTests.ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => (t.GetProperty("name").GetString()!, t.GetProperty("inputSchema").Clone()))
            .ToList();
    }

    /// <summary>A value for every required parameter of a tool's input schema, naming the existing container and
    /// server where the parameter asks for one.</summary>
    private static JsonObject ArgumentsFor(JsonElement schema)
    {
        var args = new JsonObject();
        if (!schema.TryGetProperty("properties", out var properties)) return args;
        var required = schema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var property in properties.EnumerateObject().Where(p => required.Contains(p.Name)))
            args[property.Name] = ValueFor(property.Name, property.Value);
        return args;
    }

    private static JsonNode? ValueFor(string name, JsonElement schema)
    {
        if (schema.TryGetProperty("enum", out var values) && values.GetArrayLength() > 0)
            return JsonNode.Parse(values[0].GetRawText());
        var type = schema.TryGetProperty("type", out var t)
            ? t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(e => e.GetString()).First(s => s != "null")
                : t.GetString()
            : "string";
        return type switch
        {
            "integer" or "number" => JsonValue.Create(1),
            "boolean" => JsonValue.Create(false),
            "array" => new JsonArray(),
            "object" => new JsonObject(),
            _ when name.Contains("container", StringComparison.OrdinalIgnoreCase) => JsonValue.Create("web-1"),
            _ when name.Equals("serverId", StringComparison.OrdinalIgnoreCase) => JsonValue.Create("local"),
            _ => JsonValue.Create("x"),
        };
    }

    /// <summary>The booted app with the kill switch on under an otherwise permissive policy (writes up to Admin, no
    /// confirmation): whatever is refused is refused by the kill switch, not by another rule.</summary>
    private sealed class KillSwitchHost : IAsyncDisposable
    {
        private readonly string _dataDir;
        private readonly string? _previousDataDir;

        private KillSwitchHost(string dataDir, string? previousDataDir)
        {
            _dataDir = dataDir;
            _previousDataDir = previousDataDir;
        }

        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public FakeDocker Docker { get; } = new(new ContainerInfo { Id = ContainerId, Name = "web-1", ServerId = "local" })
        {
            AllowMutations = true,
        };
        public RefusingLauncher Launcher { get; } = new();
        public SignedInPerson User { get; } = new();
        public string ApiKeyId { get; private set; } = "";

        /// <param name="everyTargetKnown">For the sweeps: a target the inventory does not know (a cloud server, a DNS
        /// zone, a webhook) is taken as named instead of refused before the policy step, so every call reaches the
        /// policy and the kill switch must be what refuses it. A target the inventory knows resolves as always.</param>
        public static async Task<KillSwitchHost> StartAsync(bool everyTargetKnown = false)
        {
            var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-killswitch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);
            var host = new KillSwitchHost(dataDir, Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR"));
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
            try
            {
                host.Factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
                    .WithWebHostBuilder(b =>
                    {
                        b.UseEnvironment("Development");
                        b.ConfigureTestServices(s =>
                        {
                            s.AddSingleton<IDockerService>(host.Docker);
                            s.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(
                                new ServerConfig { Id = "local", Name = "Local", IsDefault = true }));
                            s.AddSingleton<ITerminalLauncher>(host.Launcher);
                            s.AddScoped<ICurrentUserService>(_ => host.User);
                            if (everyTargetKnown)
                                s.AddSingleton<IMutationTargetResolver>(sp =>
                                    new EveryTargetKnown(ActivatorUtilities.CreateInstance<MutationTargetResolver>(sp)));
                        });
                    });
                host.Client = host.Factory.CreateClient();

                var permissions = host.Factory.Services.GetRequiredService<IMcpPermissionService>();
                var key = await GatewayArrange.RunAsync("mcp.key_create", () => permissions.AddApiKeyAsync("ops", McpPermissionLevels.Admin));
                host.ApiKeyId = key.Id;
                host.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Key);

                var guardrails = host.Factory.Services.GetRequiredService<IGuardrailStore>();
                var config = guardrails.Config;
                var policy = config.ActivePolicy();
                policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
                policy.RequireConfirmationForWrites = false;
                policy.ReadOnlyMode = true;
                await GatewayArrange.RunAsync("guardrails.save", () => guardrails.SaveConfigAsync(config, AdminEditor));
                Assert.True(guardrails.Current.ReadOnlyMode);
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public DbLease Db()
        {
            var scope = Factory.Services.CreateScope();
            return new DbLease(scope, scope.ServiceProvider.GetRequiredService<Whiskers.Services.Persistence.MetricsDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (Factory is not null) await Factory.DisposeAsync();
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", _previousDataDir);
            TestSqlite.ClearPools(_dataDir);
            try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class DbLease(IServiceScope scope, Whiskers.Services.Persistence.MetricsDbContext context) : IDisposable
    {
        public Whiskers.Services.Persistence.MetricsDbContext Context { get; } = context;
        public void Dispose() => scope.Dispose();
    }

    /// <summary>The person signed in to the UI; the role is read anew for every mutation, as the real service does.</summary>
    private sealed class SignedInPerson : ICurrentUserService
    {
        public AppRole Role { get; set; } = AppRole.Admin;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(AdminEmail);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }

    /// <summary>The real resolver; where it finds no target, the target as the caller named it.</summary>
    private sealed class EveryTargetKnown(IMutationTargetResolver inner) : IMutationTargetResolver
    {
        public async Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct)
        {
            try
            {
                var resolution = await inner.ResolveAsync(request, capability, ct);
                if (resolution.Target is not null) return resolution;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unknown to the inventory doubles; taken as named below.
            }
            return new TargetResolution(request.Target, null);
        }
    }

    /// <summary>Counts every attempt to start a shell and starts none.</summary>
    private sealed class RefusingLauncher : ITerminalLauncher
    {
        private int _starts;
        public int Starts => Volatile.Read(ref _starts);

        public ITerminalProcess Start(TerminalLaunch launch)
        {
            Interlocked.Increment(ref _starts);
            throw new InvalidOperationException("No shell may start in this test.");
        }
    }
}
