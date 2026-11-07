using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Models.Cve;
using Whiskers.Services.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.AutoPatch;
using Whiskers.Services.Cve;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Whiskers.Tests.ReleaseGate;
using static Whiskers.Tests.AuditTrail;

namespace Whiskers.Tests;

/// <summary>
/// INV-45 (WP5): every mutation attempt — allowed, denied, waiting for approval, failed — is exactly one complete audit
/// event, on every path. The paths are the rows of the <c>PRODUCT_CONTRACT.md</c> §7 matrix, read from the document:
/// each row is proved by tests marked <see cref="AuditPathAttribute"/> that drive it against a real <c>AuditLog</c>
/// for every outcome it can have, or it is an M row (one event per MCP call:
/// <see cref="AuditCompletenessTests.Every_mutating_mcp_tool_call_is_one_authoritative_event"/>), or it names no
/// capability and is listed in <see cref="Exempt"/> with the reason and the tests that pin its own record. A new row
/// without such proof fails <see cref="Every_path_of_the_matrix_proves_one_complete_event_per_attempt"/>.
/// <para>Approve-then-execute is two attempts: the one that waited (one <c>PendingApproval</c> event naming the
/// approval) and the run after the approval (one <c>Executed</c> event naming the approval and the approver).</para>
/// </summary>
public sealed class AuditPathMatrixTests : IDisposable
{
    private const string Admin = "admin@example.com";
    private static readonly string AdminId = PrincipalIds.User(Admin);
    private const string Ops = "ops@example.com";
    private static readonly string OpsId = PrincipalIds.User(Ops);

    private readonly ApprovalDatabase _db = new();

    public void Dispose() => _db.Dispose();

    // ------------------------------------------------------------------------------------------------ the gate

    /// <summary>The outcomes a row must prove when it is not the default (allowed, denied, failed): the rows on which
    /// a call can wait for a human approval (§7 "Approval" column).</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> RequiredOutcomes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // A-01: a confirmation is an approval bound to the operation, decided in the chat or on the Approvals page.
        ["A-01"] = [Executed, Denied, Failed, Pending, Approved],
        // T-01, T-02: a shell in a control-plane container opens only after an explicit Admin approval (INV-59).
        ["T-01"] = [Executed, Denied, Failed, Pending, Approved],
        ["T-02"] = [Executed, Denied, Failed, Pending, Approved],
        // S-16: a probe changes nothing; a target that fails is the run's answer, recorded as an executed run
        // (EndpointCheckGatingTests.A_failing_target_is_still_one_executed_event_its_outcome_is_the_runs). Only a run
        // the gateway could not finish is Failed, and that is proved too.
    };

    internal static readonly string[] DefaultOutcomes = [Executed, Denied, Failed];

    /// <summary>A row that performs no catalog capability, why it has no gateway event of its own, and the tests that
    /// pin the record it does have.</summary>
    internal sealed record Exemption(string Reason, params string[] PinnedBy);

    internal static readonly IReadOnlyDictionary<string, Exemption> Exempt = new Dictionary<string, Exemption>(StringComparer.Ordinal)
    {
        ["A-04"] = new("Dead code (C-20); what it would call are /mcp tool calls, each the gateway's event of an M row.",
            nameof(AuditCompletenessTests)),
        ["S-06"] = new("Monitors change no workload, file or setting; the CVE scan's host caches are a designed exemption (R1-48).",
            nameof(MutationGatewayCoverageTests)),
        ["S-08"] = new("The audit chain's own retention, outside the gateway by design: one audit.retention event per purge.",
            nameof(AuditRetentionTests)),
        ["S-09"] = new("Startup upgrade backup before the schema is current: no audit row can exist yet; pinned outside the gateway.",
            nameof(MutationGatewayCoverageTests)),
        ["S-10"] = new("Host access outside the app; the next start writes selfbackup.restored.",
            nameof(BackupServiceTests), nameof(MutationGatewayCoverageTests)),
        ["S-11"] = new("Host access outside the app; auth.recovery_issued is written before the change, without it nothing changes.",
            nameof(LocalAccountLifecycleTests)),
        ["S-12"] = new("Anonymous recovery page outside the gateway; auth.recovery_redeemed / auth.recovery_refused per attempt.",
            nameof(LocalAccountLifecycleTests)),
        ["S-13"] = new("Designed exemption (INV-38): setup.first_admin is written before anything, fail closed.",
            nameof(SetupStateServiceTests)),
        ["S-14"] = new("Designed exemption (INV-38): auth.identity_bound is written before the binding, else the sign-in is refused.",
            nameof(OidcIdentityBindingTests)),
        ["S-15"] = new("Host access outside the app: auth.identity_bound is written before the change.",
            nameof(OidcIdentityBindingTests)),
        ["U-33"] = new("Approving or rejecting decides another operation (the approval store's own record); the operation's run " +
                       "after an approval is its own event naming approval and approver (A-01, T-01, T-02, AuditCompletenessTests).",
            nameof(AuditCompletenessTests), nameof(ApprovalPersistenceTests)),
    };

    /// <summary>The MCP tools <see cref="AuditCompletenessTests.MutatingMcpTools"/> drives.</summary>
    private static IReadOnlySet<string> McpTheoryTools() =>
        AuditCompletenessTests.MutatingMcpToolNames().ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a tool mutates: above <c>read</c>, or performing a catalog capability.</summary>
    private static bool Mutates(string tool) =>
        (McpPermissionLevels.DefaultToolLevels.TryGetValue(tool, out var level) && level != McpPermissionLevels.Read)
        || new CapabilityCatalog().FindByMcpTool(tool) is not null;

    internal static IReadOnlyList<(string Test, AuditPathAttribute Mark)> Marks() =>
        typeof(AuditPathMatrixTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(m => m.GetCustomAttributes<AuditPathAttribute>()
                .Select(a => ($"{m.DeclaringType!.Name}.{m.Name}", a, m)))
            .Select(x =>
            {
                Assert.True(x.m.GetCustomAttributes<FactAttribute>(inherit: true).Any(), $"{x.Item1} is marked [AuditPath] but is no test");
                return (x.Item1, x.a);
            })
            .ToList();

    /// <summary>Every problem of the matrix's audit proof; empty when every §7 row is proved.</summary>
    internal static List<string> CoverageProblems(IReadOnlyList<PathRow> rows, IReadOnlyList<(string Test, AuditPathAttribute Mark)> marks,
        IReadOnlyDictionary<string, Exemption> exempt, IReadOnlySet<string> mcpTheory, Func<string, bool> mutates,
        Func<string, bool> testClassExists)
    {
        var problems = new List<string>();
        var ids = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var classes = rows.Select(r => r.Class).ToHashSet(StringComparer.Ordinal);
        foreach (var (test, mark) in marks)
        {
            var wildcard = mark.Row.EndsWith("-*", StringComparison.Ordinal);
            if (wildcard ? !classes.Contains(mark.Row[..^2]) : !ids.Contains(mark.Row))
                problems.Add($"{test}: [AuditPath(\"{mark.Row}\")] names no row of §7");
            if (mark.Outcomes.Count == 0) problems.Add($"{test}: [AuditPath(\"{mark.Row}\")] proves no outcome");
            foreach (var o in mark.Outcomes.Where(o => !AuditTrail.Outcomes.Contains(o)))
                problems.Add($"{test}: '{o}' is no outcome");
        }
        foreach (var row in rows)
        {
            if (row.Class == "M")
            {
                if (row.McpTool is null) problems.Add($"{row.Id}: the M row names no tool");
                else if (mutates(row.McpTool) && !mcpTheory.Contains(row.McpTool))
                    problems.Add($"{row.Id}: '{row.McpTool}' mutates but is not driven by " +
                                 $"{nameof(AuditCompletenessTests)}.{nameof(AuditCompletenessTests.Every_mutating_mcp_tool_call_is_one_authoritative_event)}");
                continue;
            }
            if (exempt.TryGetValue(row.Id, out var exemption))
            {
                if (row.Capabilities.Count > 0)
                    problems.Add($"{row.Id}: performs {string.Join(", ", row.Capabilities)}, so it cannot be exempt — prove it with [AuditPath]");
                if (string.IsNullOrWhiteSpace(exemption.Reason)) problems.Add($"{row.Id}: exempt without a reason");
                foreach (var pinned in exemption.PinnedBy.Where(p => !testClassExists(p)))
                    problems.Add($"{row.Id}: the pinning test class '{pinned}' does not exist");
                continue;
            }
            var proved = marks
                .Where(m => m.Mark.Row == row.Id || (m.Mark.Row == row.Class + "-*" && row.Capabilities.Count > 0))
                .SelectMany(m => m.Mark.Outcomes).ToHashSet(StringComparer.Ordinal);
            var required = RequiredOutcomes.TryGetValue(row.Id, out var r) ? r : DefaultOutcomes;
            var missing = required.Where(o => !proved.Contains(o)).ToList();
            if (missing.Count > 0)
                problems.Add(proved.Count == 0
                    ? $"{row.Id}: no [AuditPath] test proves one complete audit event per attempt on this path (PRODUCT_CONTRACT.md:{row.Line})"
                    : $"{row.Id}: no [AuditPath] test proves the outcome(s) {string.Join(", ", missing)}");
        }
        foreach (var id in exempt.Keys.Where(id => !ids.Contains(id)))
            problems.Add($"{id}: exempt, but no row of §7");
        return problems;
    }

    private static IReadOnlyList<PathRow> MatrixRows()
    {
        var problems = new List<string>();
        var rows = PathMatrixParser.Parse(GateFiles.Require(GateFiles.Contract), GateFiles.Read(GateFiles.PathCapabilities), problems);
        Assert.Empty(problems);
        return rows;
    }

    private static bool TestClassExists(string name) =>
        typeof(AuditPathMatrixTests).Assembly.GetTypes().Any(t => t.Name == name && t.GetMethods().Any(m => m.GetCustomAttributes<FactAttribute>(true).Any()));

    [Fact]
    public void Every_path_of_the_matrix_proves_one_complete_event_per_attempt()
    {
        var rows = MatrixRows();

        var problems = CoverageProblems(rows, Marks(), Exempt, McpTheoryTools(), Mutates, TestClassExists);

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Contains(rows, r => r.Class == "S");   // the reader really saw the matrix
    }

    [Fact]
    public void A_row_without_proof_or_with_a_capability_behind_an_exemption_fails_the_matrix()
    {
        PathRow Row(string id, params string[] caps) => new(id, id[..1], null, "", caps, caps.Length == 0 ? "— none" : null, 1);
        var rows = new[] { Row("S-01", "host.exec"), Row("S-99", "host.exec"), Row("S-06"), Row("U-01", "container.start"),
            new PathRow("M-01", "M", "restart_container", "", [], null, 1) };
        var marks = new List<(string, AuditPathAttribute)>
        {
            ("T.a", new AuditPathAttribute("S-01", Executed, Denied, Failed)),
            ("T.b", new AuditPathAttribute("U-*", Executed, Denied)),
            ("T.c", new AuditPathAttribute("S-42", Executed)),
        };
        var exempt = new Dictionary<string, Exemption> { ["S-06"] = new("monitors", "Nope"), ["S-99"] = new("x") };

        var problems = CoverageProblems(rows, marks, exempt, new HashSet<string>(), _ => true, n => n != "Nope");

        Assert.Contains(problems, p => p.StartsWith("S-99: performs host.exec", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("U-01: no [AuditPath] test proves the outcome(s) Failed", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("T.c: [AuditPath(\"S-42\")] names no row", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("M-01: 'restart_container' mutates but is not driven", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'Nope' does not exist", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.StartsWith("S-01", StringComparison.Ordinal));

        var unproved = CoverageProblems([Row("S-50", "autopatch.run")], [], new Dictionary<string, Exemption>(),
            new HashSet<string>(), _ => true, _ => true);
        Assert.Equal("S-50: no [AuditPath] test proves one complete audit event per attempt on this path (PRODUCT_CONTRACT.md:1)",
            Assert.Single(unproved));
    }

    // ------------------------------------------------------------------------------------------------ shared doubles

    private static GuardrailPolicy Permissive()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        return policy;
    }

    private sealed class Person(AppRole role, string email = Ops) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(email);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    // ------------------------------------------------------------------------------------------------ U and T rows

    /// <summary>Every (row, capability) of the U and T rows, read from §7 and PATH_CAPABILITIES.md: a row or a
    /// capability added there is driven here without a change to this test.</summary>
    public static TheoryData<string, string> UiCapabilities()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in MatrixRows().Where(r => r.Class is "U" or "T"))
            foreach (var capability in row.Capabilities)
                data.Add(row.Id, capability);
        return data;
    }

    /// <summary>Arguments naming a target of every kind (<see cref="MutationTargets.FromArguments"/> takes the one its
    /// capability acts on).</summary>
    private static object ArgumentsFor() => new
    {
        serverId = "local", containerId = "web", name = "web", networkId = "net-1", zone = "example.com", server = "cx-1",
        taskId = "task-1", volumeName = "vol-1", workloadId = "wl-1", appId = "app-1", webhookId = "wh-1",
        section = "general", account = "someone@example.com", binding = "binding-1", checkId = "check-1",
    };

    /// <summary>
    /// U-xx, T-xx: a UI action — an infrastructure page, a configuration page or a terminal's open — is one complete
    /// gateway event per attempt for every capability its row performs: run (executed), refused by the page's role
    /// check (the page's <c>RefuseAsync</c>, which pages call in front of their dialogs), refused by the gateway for a
    /// role below the capability, refused by the kill switch where it applies, and failed. A capability a person may
    /// only run after a second Admin's approval (a self-backup restore, the mTLS layer) waits first — one
    /// PendingApproval event — and the run after the approval is one event naming approval and approver. The pages
    /// write no row of their own (<see cref="UiAuditCompletenessTests"/>: source scans of every page).
    /// </summary>
    [Theory]
    [MemberData(nameof(UiCapabilities))]
    [AuditPath("U-*", Executed, Denied, Failed)]
    [AuditPath("T-*", Executed, Denied, Failed)]
    public async Task Every_ui_and_terminal_capability_is_one_complete_event_per_attempt(string row, string capabilityId)
    {
        var capability = new CapabilityCatalog().Find(capabilityId);
        Assert.True(capability is not null, $"{row}: '{capabilityId}' is no capability");
        var policy = Permissive();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var approvals = new ApprovalStore(policy: store, persistence: _db.Persistence());
        var gateway = AuditTrail.Gateway(_db.Scopes, store, approvals);
        UiMutationRunner As(AppRole role) => new(gateway, new CapabilityCatalog(), new Person(role));
        var args = ArgumentsFor();
        var runs = 0;

        // One attempt, decided: a wait for an approval is its own event, then the run after a second Admin approved.
        async Task<AuditLogEntity> DecidedAsync(Func<Task> attempt, string expected)
        {
            var rows = await AttemptAsync(_db.Scopes, attempt);
            if (rows is not [{ Outcome: Pending } pending])
                return One(rows, expected, capabilityId, "Ui");
            AssertComplete(pending, Pending, capabilityId, "Ui");
            Assert.Equal(ApprovalDecisionResult.Decided, await approvals.DecideAsync(pending.ApprovalId!, true,
                ApprovalResolver.User("second@example.com", AppRole.Admin)));
            var after = One(await AttemptAsync(_db.Scopes, attempt), expected == Executed ? Approved : expected,
                capabilityId, "Ui");
            Assert.Equal(pending.ApprovalId, after.ApprovalId);
            return after;
        }

        // Allowed and run.
        var ran = await DecidedAsync(
            () => As(AppRole.Admin).RunAsync(capabilityId, args, _ => { runs++; return Task.FromResult(true); }), Executed);
        Assert.Equal(capability!.TargetKind, ran.TargetType);
        Assert.Equal(OpsId, ran.PrincipalId);
        Assert.Equal(1, runs);

        // Failed: the action threw.
        await DecidedAsync(() => Assert.ThrowsAnyAsync<Exception>(() =>
            As(AppRole.Admin).RunAsync<bool>(capabilityId, args, _ => throw new InvalidOperationException("the host did not answer"))),
            Failed);

        // Failed as reported: the action answered with a result that says it did not succeed (a deploy or onboarding
        // result object, R1-21) — not "executed" because the delegate returned.
        await DecidedAsync(() => As(AppRole.Admin).RunAsync(capabilityId, args, _ => Task.FromResult(new { Success = false })),
            Failed);

        // Refused by the page's own role check: nothing runs.
        var refused = await AttemptAsync(_db.Scopes, () => As(AppRole.Viewer).RefuseAsync(capabilityId, args, "Requires the Admin role."));
        Assert.Equal(capability.TargetKind, One(refused, Denied, capabilityId, "Ui").TargetType);

        // Refused by the gateway: a Viewer holds read only.
        if (capability.RequiredLevel != McpPermissionLevels.Read)
        {
            var below = await AttemptAsync(_db.Scopes,
                () => As(AppRole.Viewer).RunAsync(capabilityId, args, _ => { runs++; return Task.FromResult(true); }));
            One(below, Denied, capabilityId, "Ui");
        }
        Assert.Equal(1, runs);

        // Refused by the kill switch, for every capability it stops.
        policy.ReadOnlyMode = true;
        var killed = await AttemptAsync(_db.Scopes,
            () => As(AppRole.Admin).RunAsync(capabilityId, args, _ => { runs++; return Task.FromResult(true); }));
        var stopped = Assert.Single(killed);
        // A capability the kill switch leaves (switching it off) runs, or waits for its approval as before.
        AssertComplete(stopped, stopped.Outcome!, capabilityId, "Ui");
        Assert.Equal(stopped.Outcome == Executed ? 2 : 1, runs);
        if (stopped.Outcome != Denied)
            // Switching the kill switch off, and changing one's own password (read level) stay possible under it.
            Assert.Contains(capabilityId, new[] { "guardrails.kill_switch_off", "guardrails.save", "account.password_change" });
    }

    /// <summary>T-01, T-02: a shell in a control-plane container waits for an explicit Admin approval — one
    /// PendingApproval event naming the approval — and opens once after it: one event naming approval and approver.</summary>
    [Fact]
    [AuditPath("T-01", Pending, Approved)]
    [AuditPath("T-02", Pending, Approved)]
    public async Task A_control_plane_shell_is_one_event_waiting_and_one_event_opening_after_the_approval()
    {
        var approvals = new ApprovalStore(policy: new AgentToolTestHelpers.FixedGuardrailStore(Permissive()),
            persistence: _db.Persistence());
        var gateway = AuditTrail.Gateway(_db.Scopes, new AgentToolTestHelpers.FixedGuardrailStore(Permissive()), approvals);
        var ui = new UiMutationRunner(gateway, new CapabilityCatalog(), new Person(AppRole.Admin, Admin));
        var args = new { containerId = "whiskers", serverId = "local" };
        var opens = 0;

        var waiting = await AttemptAsync(_db.Scopes,
            () => ui.RunAsync("terminal.container_open", args, _ => { opens++; return Task.FromResult(true); }));
        var pending = One(waiting, Pending, "terminal.container_open", "Ui");
        Assert.Equal(0, opens);

        Assert.Equal(ApprovalDecisionResult.Decided,
            await approvals.DecideAsync(pending.ApprovalId!, true, ApprovalResolver.User("second@example.com", AppRole.Admin)));
        var opened = await AttemptAsync(_db.Scopes,
            () => ui.RunAsync("terminal.container_open", args, _ => { opens++; return Task.FromResult(true); }));

        var row = One(opened, Approved, "terminal.container_open", "Ui");
        Assert.Equal(pending.ApprovalId, row.ApprovalId);
        Assert.Equal(1, opens);
    }

    // ------------------------------------------------------------------------------------------------ A rows

    /// <summary>A registry with one mutating tool, <c>restart_container</c>, whose body is <see cref="RestartContainer"/>.</summary>
    private sealed class OneTool : IAgentToolRegistry
    {
        public IReadOnlyDictionary<string, AgentToolEntry> Tools { get; } = new Dictionary<string, AgentToolEntry>
        {
            ["restart_container"] = new("restart_container", McpPermissionLevels.Write, "Container",
                typeof(AuditPathMatrixTests).GetMethod(nameof(RestartContainer), BindingFlags.NonPublic | BindingFlags.Static)!,
                new AgentToolDefinition("restart_container", "Restarts a container.", JsonDocument.Parse("{}").RootElement)),
        };
    }

    /// <summary>The tool body: "boom" fails, anything else is restarted.</summary>
    private static string RestartContainer([Description("The container")] string containerId) =>
        containerId == "boom" ? throw new InvalidOperationException("the daemon did not answer") : $"restarted {containerId}";

    private sealed class AllowAllTriggers : ITriggerAuthority
    {
        public Task<string?> RefusalAsync(string triggerId, Capability capability, MutationTarget target, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private (AgentToolInvoker Invoker, GuardrailPolicy Policy, ApprovalStore Approvals) Agent()
    {
        var policy = Permissive();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var approvals = new ApprovalStore(policy: store, persistence: _db.Persistence());
        var gateway = AuditTrail.Gateway(_db.Scopes, store, approvals, triggers: new AllowAllTriggers());
        var scopes = new ServiceCollection().AddHttpContextAccessor().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new AgentToolInvoker(new OneTool(), GuardrailEngine.CreateDefault(), scopes, gateway: gateway), policy, approvals);
    }

    private static AgentToolCall Restart(string container) =>
        new("call", "restart_container", JsonSerializer.Serialize(new { containerId = container }));

    /// <summary>A-01: the web agent chat. Each tool call is one event: run, refused by the policy, waiting for the
    /// approval, run after it (naming approval and approver), failed.</summary>
    [Fact]
    [AuditPath("A-01", Executed, Denied, Failed, Pending, Approved)]
    public async Task A_web_agent_tool_call_is_one_complete_event_per_attempt()
    {
        var (invoker, policy, approvals) = Agent();
        var context = new AgentContext("chat-1",
            new AgentPrincipal(AgentPrincipalKind.WebUser, Admin, McpPermissionLevels.Admin, null, UserEmail: Admin),
            AgentOrigin.WebUi, policy);
        await AgentOutcomesAsync(invoker, policy, context, AdminId);

        policy.RequireConfirmationForWrites = true;
        var call = Restart("web");
        var waiting = await AttemptAsync(_db.Scopes, () => invoker.InvokeAsync(call, context));
        var pending = One(waiting, Pending, "container.restart", "Agent");
        await approvals.DecideAsync(pending.ApprovalId!, true, ApprovalResolver.User("second@example.com", AppRole.Admin));

        var ran = await AttemptAsync(_db.Scopes, () => invoker.InvokeAsync(call, context));

        var row = One(ran, Approved, "container.restart", "Agent");
        Assert.Equal(pending.ApprovalId, row.ApprovalId);
        Assert.Equal(pending.OperationId, row.OperationId);   // the same operation, continued
        Assert.Equal("second@example.com", row.ApprovedBy);
    }

    /// <summary>A-02: <c>instruct_agent</c> — the agent acts for the calling key; its tool calls are the same events.</summary>
    [Fact]
    [AuditPath("A-02", Executed, Denied, Failed)]
    public async Task An_instructed_agent_tool_call_is_one_complete_event_per_attempt()
    {
        var (invoker, policy, _) = Agent();
        var context = new AgentContext("mcp-run-1",
            new AgentPrincipal(AgentPrincipalKind.McpKey, "ci", McpPermissionLevels.Write, null, McpKeyId: "k1"),
            AgentOrigin.McpTool, policy);

        await AgentOutcomesAsync(invoker, policy, context, "key:k1");
    }

    /// <summary>A-03: an AI trigger's run, within its grant; a call that would need a confirmation is a terminal
    /// refusal — one Denied event, no approval raised.</summary>
    [Fact]
    [AuditPath("A-03", Executed, Denied, Failed)]
    public async Task An_ai_trigger_tool_call_is_one_complete_event_per_attempt()
    {
        var (invoker, policy, approvals) = Agent();
        var trigger = new AgentPrincipal(AgentPrincipalKind.Trigger, "AI trigger 'restart'", McpPermissionLevels.Write, null,
            TriggerId: "t1");
        var context = new AgentContext("trigger-run-1", trigger, AgentOrigin.Trigger, policy, new PinnedPolicy("custom", policy));

        await AgentOutcomesAsync(invoker, policy, context, "trigger:t1");

        policy.RequireConfirmationForWrites = true;
        var confirm = await AttemptAsync(_db.Scopes, () => invoker.InvokeAsync(Restart("web"), context));
        One(confirm, Denied, "container.restart", "Agent");
        Assert.Empty(approvals.GetAll());
    }

    private async Task AgentOutcomesAsync(AgentToolInvoker invoker, GuardrailPolicy policy, AgentContext context, string onBehalfOf)
    {
        var executed = await AttemptAsync(_db.Scopes, () => invoker.InvokeAsync(Restart("web"), context));
        var row = One(executed, Executed, "container.restart", "Agent");
        Assert.Equal(onBehalfOf, row.OnBehalfOfId);
        Assert.Equal("web", row.TargetId);

        var failed = await AttemptAsync(_db.Scopes, () => invoker.InvokeAsync(Restart("boom"), context));
        One(failed, Failed, "container.restart", "Agent");

        policy.ForbiddenArgPatterns = ["^forbidden$"];
        var denied = await AttemptAsync(_db.Scopes, () => invoker.InvokeAsync(Restart("forbidden"), context));
        One(denied, Denied, "container.restart", "Agent");
        policy.ForbiddenArgPatterns = [];
    }

    // ------------------------------------------------------------------------------------------------ S-01 scheduler

    private AutomationMutationRunnerTests.Fixture Automation(bool requireOwner = true, GuardrailPolicy? policy = null)
    {
        var f = AutomationMutationRunnerTests.Build(requireOwner, policy ?? Permissive(), _db.Scopes);
        f.Whitelist.Allowed.Add(Ops);
        f.Roles.Map[Ops] = AppRole.Operator;
        f.Whitelist.Allowed.Add(Admin);
        f.Roles.Map[Admin] = AppRole.Admin;
        return f;
    }

    private static ScheduledTaskEntity RestartTask(bool owned = true) => new()
    {
        TaskId = "t1", Name = "nightly restart", TaskType = ScheduledTaskType.ContainerRestart, TargetId = "web-1",
        TargetName = "web", CreatedBy = owned ? OpsId : "", CreatedVia = owned ? OpsId : null,
    };

    private static TaskExecutor Executor(AutomationMutationRunnerTests.Fixture f, bool dockerWorks) =>
        new(new FakeDocker { AllowMutations = dockerWorks }, null!, null!, null!, null!, f.Runner,
            NullLogger<TaskExecutor>.Instance, notify: new FakeNotifications());

    /// <summary>S-01: a scheduled run is one event on its owner's authority — executed, failed, refused by the kill
    /// switch, refused because the owner lost the role, refused because it has no owner.</summary>
    [Fact]
    [AuditPath("S-01", Executed, Denied, Failed)]
    public async Task A_scheduled_run_is_one_complete_event_per_attempt()
    {
        var f = Automation();
        var executed = await AttemptAsync(_db.Scopes, () => Executor(f, dockerWorks: true).ExecuteAsync(RestartTask()));
        var row = One(executed, Executed, "container.restart", "Automation");
        Assert.Equal("task:t1", row.PrincipalId);
        Assert.Equal(OpsId, row.OnBehalfOfId);

        var failed = await AttemptAsync(_db.Scopes, () => Executor(f, dockerWorks: false).ExecuteAsync(RestartTask()));
        One(failed, Failed, "container.restart", "Automation");

        var killSwitch = Permissive();
        killSwitch.ReadOnlyMode = true;
        var killed = await AttemptAsync(_db.Scopes,
            () => Executor(Automation(policy: killSwitch), dockerWorks: true).ExecuteAsync(RestartTask()));
        One(killed, Denied, "container.restart", "Automation");

        f.Roles.Map[Ops] = AppRole.Viewer;
        var demoted = await AttemptAsync(_db.Scopes, () => Executor(f, dockerWorks: true).ExecuteAsync(RestartTask()));
        One(demoted, Denied, "container.restart", "Automation");

        var unowned = await AttemptAsync(_db.Scopes,
            () => Executor(f, dockerWorks: true).ExecuteAsync(RestartTask(owned: false)));
        Assert.Equal("unowned:task:t1", One(unowned, Denied, "container.restart", "Automation", owned: false).PrincipalId);
    }

    // ------------------------------------------------------------------------------------------------ S-03 AutoPatch

    private static AutoPatchService AutoPatch(AutomationMutationRunnerTests.Fixture f, MemoryAutomationOwners owners,
        AutoPatchTests.RecordingRunner runner)
    {
        var store = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        store.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        return AutoPatchTests.NewService(runner, store, f.Runner, owners,
            new AutoPatchSettings { Enabled = true, DryRun = false }, AutoPatchTests.Compose("a"));
    }

    /// <summary>S-03: a night is one <c>autopatch.run</c> event, and each container it patches one
    /// <c>container.update</c> event inside it — executed or failed; a night refused by the kill switch, without an
    /// owner, or with an unreadable owner entry is one Denied event and patches nothing.</summary>
    [Fact]
    [AuditPath("S-03", Executed, Denied, Failed)]
    public async Task A_nightly_autopatch_is_one_complete_event_per_attempt()
    {
        var f = Automation();
        var owners = new MemoryAutomationOwners();
        await owners.SetAsync(IAutomationOwnerStore.AutoPatch, AdminId);

        var patched = new AutoPatchTests.RecordingRunner();
        var night = await AttemptAsync(_db.Scopes, () => AutoPatch(f, owners, patched).RunNightlyAsync(CancellationToken.None));
        Assert.Equal(2, night.Count);
        var outer = night.Single(r => r.Action == "autopatch.run");
        AssertComplete(outer, Executed, "autopatch.run", "Automation");
        var inner = night.Single(r => r.Action == "container.update");
        AssertComplete(inner, Executed, "container.update", "Automation");
        Assert.Equal(outer.OperationId, inner.ParentOperationId);
        Assert.Equal(AdminId, outer.OnBehalfOfId);
        Assert.Single(patched.Calls);

        var rolledBack = new AutoPatchTests.RecordingRunner { Verdicts = { ["a"] = AutoPatchResultKind.RolledBack } };
        var failed = await AttemptAsync(_db.Scopes, () => AutoPatch(f, owners, rolledBack).RunNightlyAsync(CancellationToken.None));
        Assert.Equal(2, failed.Count);
        AssertComplete(failed.Single(r => r.Action == "autopatch.run"), Failed, "autopatch.run", "Automation");
        AssertComplete(failed.Single(r => r.Action == "container.update"), Failed, "container.update", "Automation");

        var killSwitch = Permissive();
        killSwitch.ReadOnlyMode = true;
        var none = new AutoPatchTests.RecordingRunner();
        var killed = await AttemptAsync(_db.Scopes,
            () => AutoPatch(Automation(policy: killSwitch), owners, none).RunNightlyAsync(CancellationToken.None));
        One(killed, Denied, "autopatch.run", "Automation");

        // Found by this matrix (INV-45): a night without an owner, or with an unreadable owner entry, wrote no event.
        var unowned = await AttemptAsync(_db.Scopes,
            () => AutoPatch(f, new MemoryAutomationOwners(), none).RunNightlyAsync(CancellationToken.None));
        Assert.Equal("unowned:autopatch", One(unowned, Denied, "autopatch.run", "Automation", owned: false).PrincipalId);

        owners.Blocked.Add(IAutomationOwnerStore.AutoPatch);
        var blocked = await AttemptAsync(_db.Scopes, () => AutoPatch(f, owners, none).RunNightlyAsync(CancellationToken.None));
        One(blocked, Denied, "autopatch.run", "Automation", owned: false);
        Assert.Empty(none.Calls);
    }
}
