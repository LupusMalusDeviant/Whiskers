using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
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
/// recorded as Denied with the kill switch as the deciding rule, and no terminal session opens. The exceptions are the
/// ones contract 0009 lists: E1 is switching the kill switch off — a person in the UI with Admin, audited, not an
/// Operator, not an Admin API key; E2 (first-run setup) and the blockable E4–E6 are pinned here on the booted app,
/// the rest in <see cref="KillSwitchExemptionTests"/>.
/// <para>The whole host is the production DI graph (gateway, policy, guardrail store, audit, terminal manager, SQLite);
/// only Docker, the server registry, the signed-in person and the terminal launcher are doubles, so a call that got
/// through would be seen, and no real shell could start.</para>
/// </summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class ReadOnlyKillSwitchWireTests
{
    internal const string ContainerId = "c0ffee0000a1";
    internal const string AdminEmail = "admin@example.com";

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
        // Exactly the catalog's mutating MCP tools are served and swept: a module dropping out of the host, or a tool
        // missing from the catalog, shows here instead of quietly shrinking the sweep.
        var catalogued = catalog.All
            .Where(c => c.McpTool is not null && McpPermissionLevels.Normalize(c.RequiredLevel) != McpPermissionLevels.Read)
            .Select(c => c.McpTool!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(catalogued, mutating.Select(m => m.Tool.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(47, mutating.Count);

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
        AssertFallbacksOnlyForUnknownTargets(host);
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
                (ResolvedTarget _, CancellationToken ct) => terminals.OpenHostShellAsync(new TerminalOpenRequest(TerminalSession.NewId(), "local"), ct)));
            Assert.Equal(MutationStatus.Denied, hostShell.Status);
            var containerShell = await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync("terminal.container_open",
                new { containerId = "web-1", serverId = "local" },
                (ResolvedTarget _, CancellationToken ct) => terminals.OpenContainerShellAsync(new TerminalOpenRequest(TerminalSession.NewId(), "local", "web-1"), ct)));
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
                    UiArguments, (ResolvedTarget _, CancellationToken _) => { ran.Add(capability.Id); return Task.CompletedTask; }));
                Assert.Equal(MutationStatus.Denied, refused.Status);
            }

            // Saving the guardrails is refused too, even when the save would only switch the kill switch off.
            var config = guardrails.Config;
            config.ActivePolicy().ReadOnlyMode = false;
            await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync("guardrails.save",
                new { section = "guardrails" }, (ResolvedTarget _, CancellationToken _) => guardrails.SaveConfigAsync(config, AdminEditor)));
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
        AssertFallbacksOnlyForUnknownTargets(host);
    }

    [Fact]
    public async Task Only_an_admin_in_the_ui_switches_the_kill_switch_off_and_the_switch_is_audited()
    {
        await using var host = await KillSwitchHost.StartAsync();
        var guardrails = host.Factory.Services.GetRequiredService<IGuardrailStore>();
        var gateway = host.Factory.Services.GetRequiredService<IMutationGateway>();

        // Over the wire there is no way to it at all: no served tool performs a guardrail write.
        var catalog = host.Factory.Services.GetRequiredService<ICapabilityCatalog>();
        var session0 = await McpServedSurfaceTests.InitializeAsync(host.Client);
        Assert.DoesNotContain(await ListToolsAsync(host.Client, session0),
            t => catalog.FindByMcpTool(t.Name)?.Id is "guardrails.kill_switch_off" or "guardrails.save");
        Assert.Null(catalog.Find(GuardrailMutationPolicy.KillSwitchOff)!.McpTool);

        // An Operator in the UI: refused, the switch stays on. This refusal is the Admin level of the capability
        // (decided before the policy step), not the kill switch: only an Admin may switch it off at all.
        host.User.Role = AppRole.Operator;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync("guardrails.kill_switch_off",
                new { section = "guardrails" }, (ResolvedTarget _, CancellationToken _) => guardrails.DisableKillSwitchAsync(AdminEditor)));
        }
        Assert.True(guardrails.Current.ReadOnlyMode);

        // An Admin API key is no person in the UI: handed to the host's gateway directly (no tool exists for it, see
        // above), the kill switch holds against it.
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
                (ResolvedTarget _, CancellationToken _) => guardrails.DisableKillSwitchAsync(AdminEditor));
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

    [Fact]
    public async Task An_agent_tool_call_is_denied_by_the_kill_switch_and_nothing_runs()
    {
        await using var host = await KillSwitchHost.StartAsync();
        var invoker = host.Factory.Services.GetRequiredService<Whiskers.Services.Agent.IAgentToolInvoker>();
        var guardrails = host.Factory.Services.GetRequiredService<IGuardrailStore>();

        // The session's copy of the policy predates the kill switch; the gateway decides on the live store.
        var stale = guardrails.Current.Clone();
        stale.ReadOnlyMode = false;
        var context = new AgentContext("agent-killswitch",
            new AgentPrincipal(AgentPrincipalKind.WebUser, AdminEmail, McpPermissionLevels.Admin, null, UserEmail: AdminEmail),
            AgentOrigin.WebUi, stale);

        var result = await invoker.InvokeAsync(
            new AgentToolCall("call-1", "restart_container", """{"containerId":"web-1"}"""), context);

        Assert.True(result.IsError);
        Assert.Equal(GuardrailVerdict.Deny, result.Decision.Verdict);
        Assert.Contains("read-only-mode", result.Decision.MatchedRuleIds);
        Assert.Empty(host.Docker.Actions);
        using var db = host.Db();
        var row = Assert.Single(db.Context.AuditLog.Where(r => r.Action == "container.restart").ToList());
        Assert.Equal((nameof(MutationStatus.Denied), nameof(MutationSurface.Agent), false), (row.Outcome, row.Surface, row.Success));
        Assert.Contains("read-only-mode", db.Context.PolicyDecisions.Single(d => d.Id == row.DecisionId).MatchedRules);
    }

    [Fact]
    public async Task An_approval_granted_before_the_kill_switch_does_not_carry_the_call_past_it()
    {
        await using var host = await KillSwitchHost.StartAsync(killSwitch: false, confirmWrites: true);
        var session = await McpServedSurfaceTests.InitializeAsync(host.Client);
        const string call = """{"containerId":"web-1"}""";

        // Kill switch off: the write waits for a person, who approves it.
        Assert.Contains("Human approval required", await CallToolAsync(host.Client, session, 1, "restart_container", call));
        var approvals = host.Factory.Services.GetRequiredService<Whiskers.Services.Approvals.IApprovalStore>();
        var approval = Assert.Single(approvals.GetPending());
        Assert.True(await approvals.ResolveAsync(approval.Id, true, "second-admin@example.com", McpPermissionLevels.Admin));

        // Then the kill switch goes on, and the approved call is sent again.
        await host.SetKillSwitchAsync(true);
        var retried = await CallToolAsync(host.Client, session, 2, "restart_container", call);

        Assert.Contains("Read-only mode active", retried);
        Assert.Empty(host.Docker.Actions);
        var after = approvals.Get(approval.Id)!;
        Assert.Equal(Whiskers.Models.Agent.ApprovalStatus.Approved, after.Status);
        Assert.Null(after.ConsumedAt);   // not used up by a call that did not run
        using var db = host.Db();
        var rows = db.Context.AuditLog.Where(r => r.Action == "container.restart").OrderBy(r => r.Id).ToList();
        Assert.DoesNotContain(rows, r => r.Outcome == nameof(MutationStatus.Executed));
        var last = rows[^1];
        Assert.Equal(nameof(MutationStatus.Denied), last.Outcome);
        Assert.Contains("read-only-mode", db.Context.PolicyDecisions.Single(d => d.Id == last.DecisionId).MatchedRules);
    }

    [Fact]
    public async Task Scheduled_tasks_autoupdate_and_autopatch_are_denied_by_the_kill_switch_and_nothing_runs()
    {
        await using var host = await KillSwitchHost.StartAsync();
        var owner = PrincipalIds.User(AdminEmail);
        await host.AddLocalAdminAsync(AdminEmail);
        // The owner holds Admin: a refusal below is the kill switch, not a lost authority.
        Assert.Equal(McpPermissionLevels.Admin,
            host.Factory.Services.GetRequiredService<IAutomationOwnerResolver>().Resolve(owner).PermissionLevel);

        // A scheduled task through the real executor.
        var executor = host.Factory.Services.GetRequiredService<Whiskers.Services.Scheduler.ITaskExecutor>();
        var task = new Whiskers.Models.ScheduledTaskEntity
        {
            TaskId = "t-killswitch", Name = "nightly", TaskType = Whiskers.Models.ScheduledTaskType.ContainerRestart,
            TargetId = "web-1", CreatedBy = owner, CreatedVia = owner,
        };
        var (success, _) = await executor.ExecuteAsync(task);
        Assert.False(success);
        Assert.True(task.Enabled);   // a refusal by policy does not suspend the task

        // AutoUpdate and AutoPatch, as their services call the runner (actor, capability, arguments).
        var runner = host.Factory.Services.GetRequiredService<IAutomationMutationRunner>();
        var update = await runner.RunAsync(new AutomationActor("autoupdate", "AutoUpdate", owner, owner), "container.update",
            new { containerId = ContainerId, name = "web-1", serverId = "local" },
            (ResolvedTarget _, CancellationToken _) => host.Docker.RecreateContainerAsync(ContainerId, "local"));
        var patched = false;
        var patch = await runner.RunAsync(new AutomationActor("autopatch", "AutoPatch", owner, owner), "autopatch.run",
            new { dryRun = false }, (ResolvedTarget _, CancellationToken _) => { patched = true; return Task.CompletedTask; });

        foreach (var outcome in new[] { update.Status, patch.Status })
            Assert.Equal(MutationStatus.Denied, outcome);
        Assert.Contains("read-only-mode", update.MatchedRules);
        Assert.Contains("read-only-mode", patch.MatchedRules);
        Assert.False(patched);
        Assert.Empty(host.Docker.Actions);
        using var db = host.Db();
        foreach (var capability in new[] { "container.restart", "container.update", "autopatch.run" })
        {
            var row = Assert.Single(db.Context.AuditLog.Where(r => r.Action == capability).ToList());
            Assert.Equal((nameof(MutationStatus.Denied), nameof(MutationSurface.Automation), owner),
                (row.Outcome, row.Surface, row.OnBehalfOfId));
            Assert.Contains("read-only-mode", db.Context.PolicyDecisions.Single(d => d.Id == row.DecisionId).MatchedRules);
        }
    }

    // ---- Contract 0009: the exemptions on the booted app ----------------------------------------------------------

    [Fact]
    public async Task E2_the_first_run_setup_creates_the_first_admin_while_the_kill_switch_is_on_whatever_is_blocked()
    {
        await using var host = await KillSwitchHost.StartAsync(blocked: [.. KillSwitchExemptions.Blockable]);
        var setup = host.Factory.Services.GetRequiredService<Whiskers.Services.Setup.ISetupStateService>();
        var paths = host.Factory.Services.GetRequiredService<DataPathOptions>();
        Assert.False(setup.IsSetupComplete);

        var done = await setup.CompleteSetupAsync(new Whiskers.Services.Setup.SetupAdminRequest
        {
            IsLocal = false, Email = "first@example.com", SetupToken = (await File.ReadAllTextAsync(paths.SetupTokenFile)).Trim(),
        });

        Assert.Equal(Whiskers.Services.Setup.SetupCompletionStatus.Success, done.Status);
        Assert.Equal(AppRole.Admin, host.Factory.Services.GetRequiredService<IRoleService>().GetRole("first@example.com"));
        Assert.True(host.Factory.Services.GetRequiredService<IGuardrailStore>().Current.ReadOnlyMode);
    }

    [Fact]
    public async Task E4_an_own_password_change_runs_under_the_kill_switch_and_blocked_it_is_one_denied_event()
    {
        // Not blocked: the person changes their own password while the switch is on.
        await using (var host = await KillSwitchHost.StartAsync())
        {
            await host.AddLocalAdminAsync(AdminEmail);
            var result = await host.ChangeOwnPasswordAsync();
            Assert.True(result.Success, result.Error);
            using var db = host.Db();
            var row = Assert.Single(db.Context.AuditLog.Where(r => r.Action == "account.password_change").ToList());
            Assert.Equal(nameof(MutationStatus.Executed), row.Outcome);
        }

        // Blocked beforehand: refused by the kill switch's rule, the password unchanged, one Denied event.
        await using (var host = await KillSwitchHost.StartAsync(blocked: [KillSwitchExemptions.PasswordChange]))
        {
            await host.AddLocalAdminAsync(AdminEmail);
            var refused = await Assert.ThrowsAsync<MutationRefusedException>(host.ChangeOwnPasswordAsync);
            Assert.Equal(MutationStatus.Denied, refused.Status);
            Assert.Equal([KillSwitchExemptions.RuleId], refused.MatchedRules);   // what the Account page reads (its message)
            Assert.True(await host.PasswordWorksAsync(AdminEmail, KillSwitchHost.LocalPassword));
            using var db = host.Db();
            var row = Assert.Single(db.Context.AuditLog.Where(r => r.Action == "account.password_change").ToList());
            Assert.Equal((nameof(MutationStatus.Denied), nameof(MutationSurface.Ui)), (row.Outcome, row.Surface));
            Assert.Equal(["read-only-mode"],
                JsonSerializer.Deserialize<string[]>(db.Context.PolicyDecisions.Single(d => d.Id == row.DecisionId).MatchedRules)!);
        }
    }

    [Fact]
    public async Task Blocking_an_exemption_is_refused_while_the_kill_switch_is_on()
    {
        await using var host = await KillSwitchHost.StartAsync();
        var guardrails = host.Factory.Services.GetRequiredService<IGuardrailStore>();
        var config = guardrails.Config;
        config.ActivePolicy().KillSwitchBlockedExemptions = [KillSwitchExemptions.RecoveryCode];

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync("guardrails.save",
                new { section = "guardrails" }, (ResolvedTarget _, CancellationToken _) => guardrails.SaveConfigAsync(config, AdminEditor)));
            Assert.Equal(MutationStatus.Denied, refused.Status);
        }

        Assert.Null(guardrails.Current.KillSwitchBlockedExemptions);
        using var db = host.Db();
        var row = Assert.Single(db.Context.AuditLog.Where(r => r.Action == "guardrails.save").ToList());
        Assert.Contains("read-only-mode", db.Context.PolicyDecisions.Single(d => d.Id == row.DecisionId).MatchedRules);
    }

    [Fact]
    public async Task E5_E6_blocked_on_the_booted_app_redemption_and_binding_at_sign_in_are_refused_by_the_kill_switch()
    {
        // The production DI graph: the account service and the binding store read the app's guardrail store.
        await using var host = await KillSwitchHost.StartAsync(
            blocked: [KillSwitchExemptions.RecoveryCode, KillSwitchExemptions.FederatedBindingAtSignIn]);
        await host.AddLocalAdminAsync(AdminEmail);

        // E3 + E5: without --force the recovery CLI refuses (A1.2); with it the code is issued but cannot be redeemed.
        var refusedCli = new StringWriter();
        Assert.Equal(4, await LocalAccountRecoveryCli.RunAsync(host.Factory.Services, [AdminEmail], refusedCli));
        Assert.True(await host.PasswordWorksAsync(AdminEmail, KillSwitchHost.LocalPassword));
        var forced = new StringWriter();
        Assert.Equal(0, await LocalAccountRecoveryCli.RunAsync(host.Factory.Services,
            [AdminEmail, LocalAccountRecoveryCli.ForceFlag], forced));
        var code = System.Text.RegularExpressions.Regex.Match(forced.ToString(), "([2-9A-HJ-NP-Z]{4}-){5}[2-9A-HJ-NP-Z]{4}").Value;
        Assert.False(string.IsNullOrEmpty(code));
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<ILocalAccountService>();
            var refused = await accounts.RedeemRecoveryCodeAsync(AdminEmail, code, "Battery-Staple-34");
            Assert.Equal(LocalAccountErrors.KillSwitch, refused.Error);
        }
        Assert.False(await host.PasswordWorksAsync(AdminEmail, "Battery-Staple-34"));
        using (var db = host.Db())
            Assert.Contains("rule=read-only-mode",
                Assert.Single(db.Context.AuditLog.Where(r => r.Action == LocalAccountService.RecoveryRefusedAction).ToList()).Details);

        // E6: a trusted provider's first sign-in of a configured address is refused and binds nothing.
        const string person = "ops@example.com";
        var roles = host.Factory.Services.GetRequiredService<IRoleService>();
        await GatewayArrange.RunAsync("config.roles", () => roles.SetRoleAsync(person, AppRole.Operator));
        var admission = host.Factory.Services.GetRequiredService<IFederatedAdmission>();
        var decision = await admission.AdmitAsync(new FederatedSignInClaims("google", AuthConstants.GoogleIssuer, "g-ops", person,
            "true", EmailIsStandardClaim: true));
        Assert.False(decision.Admitted);
        Assert.Equal(AuthAuditReasons.KillSwitch, decision.Reason);
        Assert.Empty(host.Factory.Services.GetRequiredService<IIdentityBindingStore>().Snapshot().Bindings);
        using (var db = host.Db())
        {
            var row = Assert.Single(db.Context.AuditLog.Where(r => r.Action == AuthAuditLog.IdentityBoundAction).ToList());
            Assert.Contains("rule=read-only-mode", row.Details);
        }
    }

    /// <summary>Arguments for the UI sweep: the keys the capability table reads a target from, each naming something
    /// that exists where it can (the container, the server, the guardrail section).</summary>
    internal static readonly object UiArguments = new
    {
        containerId = "web-1", serverId = "local", section = "guardrails", name = "web-1", networkId = "net-1",
        zone = "example.com", server = "cloud-1", taskId = "task-1", volumeName = "data", workloadId = "default/web",
        appId = "app-1", webhookId = "hook-1", account = "someone@example.com", binding = "binding-1",
    };

    internal static readonly AgentPrincipal AdminEditor =
        new(AgentPrincipalKind.WebUser, AdminEmail, McpPermissionLevels.Admin, null, UserEmail: AdminEmail);

    internal static async Task<string> CallToolAsync(HttpClient client, string session, int id, string tool, string argsJson)
    {
        using var response = await McpServedSurfaceTests.PostAsync(client, session,
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{argsJson}}}}}""");
        Assert.True(response.IsSuccessStatusCode, $"{tool}: tools/call failed with {(int)response.StatusCode}");
        return McpServedSurfaceTests.ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync());
    }

    internal static async Task<List<(string Name, JsonElement Schema)>> ListToolsAsync(HttpClient client, string session)
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
    internal static JsonObject ArgumentsFor(JsonElement schema)
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
    internal sealed class KillSwitchHost : IAsyncDisposable
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
        public EveryTargetKnown? Resolver { get; private set; }

        /// <param name="everyTargetKnown">For the sweeps: a target the inventory does not know (a cloud server, a DNS
        /// zone, a webhook) is taken as named instead of refused before the policy step, so every call reaches the
        /// policy and the kill switch must be what refuses it. A target the inventory knows resolves as always.</param>
        public static async Task<KillSwitchHost> StartAsync(bool everyTargetKnown = false, bool killSwitch = true, bool confirmWrites = false,
            string[]? blocked = null)
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
                                s.AddSingleton<IMutationTargetResolver>(sp => host.Resolver =
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
                policy.RequireConfirmationForWrites = confirmWrites;
                policy.ReadOnlyMode = killSwitch;
                policy.KillSwitchBlockedExemptions = blocked is null ? null : [.. blocked];
                await GatewayArrange.RunAsync("guardrails.save", () => guardrails.SaveConfigAsync(config, AdminEditor));
                Assert.Equal(killSwitch, guardrails.Current.ReadOnlyMode);
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        /// <summary>Switches the kill switch on or off as an arrangement (not under test here).</summary>
        public async Task SetKillSwitchAsync(bool on)
        {
            var guardrails = Factory.Services.GetRequiredService<IGuardrailStore>();
            var config = guardrails.Config;
            config.ActivePolicy().ReadOnlyMode = on;
            await GatewayArrange.RunAsync("guardrails.save", () => guardrails.SaveConfigAsync(config, AdminEditor));
            Assert.Equal(on, guardrails.Current.ReadOnlyMode);
        }

        /// <summary>A local account with the Admin role, so an item it owns runs on an Admin's authority.</summary>
        public async Task AddLocalAdminAsync(string email)
        {
            using (var scope = Factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Whiskers.Services.Persistence.AppUser>>();
                var created = await users.CreateAsync(
                    new Whiskers.Services.Persistence.AppUser { UserName = email, Email = email, EmailConfirmed = true }, LocalPassword);
                Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Code)));
            }
            var roles = Factory.Services.GetRequiredService<IRoleService>();
            await GatewayArrange.RunAsync("config.roles", () => roles.SetRoleAsync(email, AppRole.Admin));
        }

        public const string LocalPassword = "CorrectHorse12!!";

        /// <summary>The signed-in person changes their own password as the Account page does (E4).</summary>
        public async Task<LocalAccountResult> ChangeOwnPasswordAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            var accounts = scope.ServiceProvider.GetRequiredService<ILocalAccountService>();
            return await mutations.RunAsync("account.password_change", new { account = AdminEmail },
                (ResolvedTarget _, CancellationToken ct) => accounts.ChangeOwnPasswordAsync(LocalPassword, "Battery-Staple-34", ct));
        }

        public async Task<bool> PasswordWorksAsync(string email, string password)
        {
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Whiskers.Services.Persistence.AppUser>>();
            var user = await users.FindByEmailAsync(email);
            return user is not null && await users.CheckPasswordAsync(user, password);
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

    internal sealed class DbLease(IServiceScope scope, Whiskers.Services.Persistence.MetricsDbContext context) : IDisposable
    {
        public Whiskers.Services.Persistence.MetricsDbContext Context { get; } = context;
        public void Dispose() => scope.Dispose();
    }

    /// <summary>The person signed in to the UI; the role is read anew for every mutation, as the real service does.</summary>
    internal sealed class SignedInPerson : ICurrentUserService
    {
        public AppRole Role { get; set; } = AppRole.Admin;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(AdminEmail);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }

    /// <summary>The real resolver; only where it answers "not found" (a refusal with a reason, no exception), the target
    /// as the caller named it. Every fallback is recorded so the test can check it happened only for targets the
    /// doubles do not hold; a resolver that throws is not covered up (the gateway then refuses before the policy and
    /// the sweep's per-call decision check fails).</summary>
    internal sealed class EveryTargetKnown(IMutationTargetResolver inner) : IMutationTargetResolver
    {
        public Task<TargetResolution> ConfirmAsync(MutationRequest request, Capability capability, ResolvedTarget target,
            CancellationToken ct) => inner.ConfirmAsync(request, capability, target, ct);

        /// <summary>The kinds the doubles hold (one server, one container, the configuration sections).</summary>
        public static readonly string[] HeldKinds = ["container", "host", "server", "config"];

        public System.Collections.Concurrent.ConcurrentQueue<(string Capability, string Kind, string Error)> Fallbacks { get; } = new();

        public async Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct)
        {
            TargetResolution resolution;
            try
            {
                resolution = await inner.ResolveAsync(request, capability, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !HeldKinds.Contains(capability.TargetKind))
            {
                // A kind with no backend in this host (Kubernetes workloads): its lookup throws instead of answering.
                Fallbacks.Enqueue((capability.Id, capability.TargetKind, ex.GetType().Name));
                return new TargetResolution(request.Target, null);
            }
            if (resolution.Target is not null) return resolution;
            Assert.False(string.IsNullOrWhiteSpace(resolution.Error), $"{capability.Id}: refused without a reason");
            Fallbacks.Enqueue((capability.Id, capability.TargetKind, resolution.Error!));
            return new TargetResolution(request.Target, null);
        }
    }

    /// <summary>The doubles hold one server ("local") and one container ("web-1") and nothing else: a fallback is
    /// allowed only for the other kinds, and for a container action whose compose project the double cannot show
    /// (its impact is planned before the policy), and for a deployment planned as a compose project (app.deploy,
    /// compose.deploy: the double cannot read the project's .env). A capability whose target the doubles hold never falls back.</summary>
    private static void AssertFallbacksOnlyForUnknownTargets(KillSwitchHost host)
    {
        var fallbacks = host.Resolver!.Fallbacks.ToList();
        Assert.NotEmpty(fallbacks);   // the sweep did meet unknown targets: the decorator is not idle
        string[] composePlanned = ["container.env_set", "compose.deploy", "app.deploy", "container.env_file_save"];
        // Contract 0012 (D12): records inside a held kind — an MCP key, a vault secret, a registry, an endpoint check —
        // are resolved against stores the sweep does not fill.
        string[] recordsNotHeld = ["mcp.key_delete", "vault.secret_set", "vault.secret_delete", "registry.delete",
            "endpointcheck.delete", "endpointcheck.run",
            // D11: nginx sites, systemd units and firewall rules are resolved against host listings the sweep does not fill.
            "nginx.update", "nginx.site_enable", "nginx.site_disable", "systemd.manage", "firewall.rule_remove"];
        string[] held = ["container.restart", "container.stop", "container.start", "container.update", "container.remove",
            "container.exec", "terminal.host_open", "terminal.container_open", "host.exec", "config.save", "guardrails.save"];
        Assert.All(fallbacks, f => Assert.True(
            !EveryTargetKnown.HeldKinds.Contains(f.Kind) || composePlanned.Contains(f.Capability)
                || recordsNotHeld.Contains(f.Capability),
            $"{f.Capability} ({f.Kind}) fell back although the doubles hold its target: {f.Error}"));
        Assert.DoesNotContain(fallbacks, f => held.Contains(f.Capability));
    }

    /// <summary>Counts every attempt to start a shell and starts none.</summary>
    internal sealed class RefusingLauncher : ITerminalLauncher
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
