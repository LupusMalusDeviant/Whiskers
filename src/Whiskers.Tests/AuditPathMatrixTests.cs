using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Mcp;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Models.Cve;
using Whiskers.Services.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.AutoPatch;
using Whiskers.Services.Backup;
using Whiskers.Services.Cve;
using Whiskers.Services.Database;
using Whiskers.Services.Server;
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
/// for every outcome it can have (<see cref="RequiredOutcomesOf"/>), or it is an M row (one event per MCP call and
/// outcome: <see cref="Every_mutating_mcp_tool_call_is_one_complete_event_per_outcome"/>), or it names no capability
/// and is listed in <see cref="Exempt"/> with the reason and the test methods that pin its own record. A new row
/// without such proof fails <see cref="Every_path_of_the_matrix_proves_one_complete_event_per_attempt"/>.
/// <para>The U rows are driven at the runner: an attempt begins when the handler requests the capability from the
/// gateway (user decision 2026-11-07); a page returning before that changes nothing
/// (<see cref="A_ui_page_changes_nothing_itself_so_returning_before_the_gateway_is_no_attempt"/>).</para>
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

    internal static readonly string[] DefaultOutcomes = [Executed, Denied, Failed];

    /// <summary>
    /// The outcomes a row must prove: allowed, denied, failed — and waiting for an approval and running after it where a
    /// call on the row can wait for one, derived, not listed: a §7 row whose "Approval" column names an approval a
    /// person can give (not "n/a", not a terminal deny), and a person's row (A, T, U) that performs a capability no
    /// setting lifts the second Admin's approval from (<see cref="ControlPlaneResources.Capabilities"/>: the mTLS layer,
    /// the self-backup restore). A new such row, or such a capability added to a row, must prove the wait and the run.
    /// </summary>
    internal static string[] RequiredOutcomesOf(PathRow row, IReadOnlyDictionary<string, string> approvalCells) =>
        NeedsApproval(row, approvalCells) ? [.. DefaultOutcomes, Pending, Approved] : DefaultOutcomes;

    internal static bool NeedsApproval(PathRow row, IReadOnlyDictionary<string, string> approvalCells)
    {
        if (row.Class is "A" or "T" or "U" && row.Capabilities.Any(ControlPlaneResources.Capabilities.Contains)) return true;
        // Review MINOR-5: the explicit marker §7 defines, not the wording — a cell that begins with ✅ is a path on which
        // a person gives an approval; anything else ("none", "n/a", "Confirm → Deny …", a ✅ further on) is not.
        return approvalCells.TryGetValue(row.Id, out var cell) && cell.TrimStart().StartsWith(ApprovalMarker, StringComparison.Ordinal);
    }

    /// <summary>How §7 marks an "Approval" cell of a path on which a person gives an approval (its legend says so).</summary>
    internal const string ApprovalMarker = "✅";

    /// <summary>The "Approval" cell of every §7 row in a table that has that column, by row id.</summary>
    internal static IReadOnlyDictionary<string, string> ApprovalCells(string contract)
    {
        var cells = new Dictionary<string, string>(StringComparer.Ordinal);
        var column = -1;
        foreach (var line in contract.Split('\n'))
        {
            if (!line.StartsWith('|')) { column = -1; continue; }
            var parts = line.Trim().Trim('|').Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length > 0 && parts[0] == "ID")
            {
                column = Array.IndexOf(parts, "Approval");
                continue;
            }
            if (column > 0 && column < parts.Length && System.Text.RegularExpressions.Regex.IsMatch(parts[0], @"^[MASTU]-\d+$"))
                cells[parts[0]] = parts[column];
        }
        return cells;
    }

    /// <summary>A row that performs no catalog capability, why it has no gateway event of its own, and the tests that
    /// pin the record it does have, as <c>Class.Method</c>: each must exist and be a test.</summary>
    internal sealed record Exemption(string Reason, params string[] PinnedBy);

    private static string Pin(string type, string method) => $"{type}.{method}";

    internal static readonly IReadOnlyDictionary<string, Exemption> Exempt = new Dictionary<string, Exemption>(StringComparer.Ordinal)
    {
        ["A-04"] = new("Dead code (C-20); what it would call are /mcp tool calls, each the gateway's event of an M row.",
            Pin(nameof(AuditCompletenessTests), nameof(AuditCompletenessTests.Every_mutating_mcp_tool_call_is_one_authoritative_event))),
        ["S-06"] = new("Monitors change no workload, file or setting; the CVE scan's host caches are a designed exemption (R1-48).",
            Pin(nameof(MutationGatewayCoverageTests), nameof(MutationGatewayCoverageTests.Only_the_cve_monitor_runs_the_scanners))),
        ["S-08"] = new("The audit chain's own retention, outside the gateway by design: one audit.retention event per purge.",
            Pin(nameof(AuditRetentionTests), nameof(AuditRetentionTests.Pruning_is_audited_and_the_trail_still_verifies))),
        ["S-09"] = new("Startup upgrade backup before the schema is current: no audit row can exist yet; pinned outside the gateway.",
            Pin(nameof(BackupServiceTests), nameof(BackupServiceTests.A_start_up_deletion_refuses_every_backup_but_a_pre_upgrade_one))),
        ["S-10"] = new("Host access outside the app; the next start writes selfbackup.restored.",
            Pin(nameof(BackupServiceTests), nameof(BackupServiceTests.A_restore_stages_checks_and_commits_and_the_next_start_applies_it))),
        ["S-11"] = new("Host access outside the app; auth.recovery_issued is written before the change, without it nothing changes. A policy refusal of the host CLI (without --force, contract 0009) changes nothing and is no attempt: it is not recorded.",
            Pin(nameof(LocalAccountLifecycleTests),
                nameof(LocalAccountLifecycleTests.Host_recovery_needs_an_existing_account_writes_its_record_first_and_prints_the_code_only_to_the_terminal)),
            Pin(nameof(MutationGatewayCoverageTests), nameof(MutationGatewayCoverageTests.The_account_recovery_paths_have_exactly_their_callers))),
        ["S-12"] = new("Anonymous recovery page outside the gateway; auth.recovery_redeemed / auth.recovery_refused per attempt.",
            Pin(nameof(LocalAccountLifecycleTests),
                nameof(LocalAccountLifecycleTests.The_recovery_page_is_limited_per_client_and_per_account_and_audits_a_storm_once))),
        ["S-13"] = new("Designed exemption (INV-38): setup.first_admin is written before anything, fail closed.",
            Pin(nameof(SetupStateServiceTests), nameof(SetupStateServiceTests.The_first_admin_is_audited_and_without_its_record_nothing_is_written))),
        ["S-14"] = new("Designed exemption (INV-38): auth.identity_bound is written before the binding, else the sign-in is refused.",
            Pin(nameof(OidcIdentityBindingTests), nameof(OidcIdentityBindingTests.Without_its_audit_row_nothing_is_bound_and_the_sign_in_is_refused))),
        ["S-15"] = new("Host access outside the app: auth.identity_bound is written before the change.",
            Pin(nameof(MutationGatewayCoverageTests), nameof(MutationGatewayCoverageTests.The_identity_binding_exemptions_have_exactly_their_callers))),
        ["U-33"] = new("Approving or rejecting decides another operation (the approval store's own record); the operation's run " +
                       "after an approval is its own event naming approval and approver (A-01, T-01, T-02, AuditCompletenessTests).",
            Pin(nameof(ApprovalPersistenceTests), nameof(ApprovalPersistenceTests.An_approval_whose_decision_cannot_be_recorded_stays_pending)),
            Pin(nameof(AuditPathMatrixTests), nameof(A_web_agent_tool_call_is_one_complete_event_per_attempt))),
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
        Func<string, bool> testExists, IReadOnlyDictionary<string, string>? approvalCells = null)
    {
        approvalCells ??= new Dictionary<string, string>();
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
                                 $"{nameof(AuditPathMatrixTests)}.{nameof(Every_mutating_mcp_tool_call_is_one_complete_event_per_outcome)}");
                continue;
            }
            if (exempt.TryGetValue(row.Id, out var exemption))
            {
                if (row.Capabilities.Count > 0)
                    problems.Add($"{row.Id}: performs {string.Join(", ", row.Capabilities)}, so it cannot be exempt — prove it with [AuditPath]");
                if (string.IsNullOrWhiteSpace(exemption.Reason)) problems.Add($"{row.Id}: exempt without a reason");
                if (exemption.PinnedBy.Length == 0) problems.Add($"{row.Id}: exempt, but no test pins its record");
                foreach (var pinned in exemption.PinnedBy.Where(p => !testExists(p)))
                    problems.Add($"{row.Id}: the pinning test '{pinned}' does not exist or is no test");
                continue;
            }
            var proved = marks
                .Where(m => m.Mark.Row == row.Id || (m.Mark.Row == row.Class + "-*" && row.Capabilities.Count > 0))
                .SelectMany(m => m.Mark.Outcomes).ToHashSet(StringComparer.Ordinal);
            var required = RequiredOutcomesOf(row, approvalCells);
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

    /// <summary>Whether <c>Class.Method</c> names a test (a <c>[Fact]</c> or <c>[Theory]</c>) of this assembly.</summary>
    private static bool TestExists(string pin)
    {
        var dot = pin.LastIndexOf('.');
        if (dot <= 0) return false;
        var (type, method) = (pin[..dot], pin[(dot + 1)..]);
        return typeof(AuditPathMatrixTests).Assembly.GetTypes().Where(t => t.Name == type)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Any(m => m.Name == method && m.GetCustomAttributes<FactAttribute>(true).Any());
    }

    [Fact]
    public void Every_path_of_the_matrix_proves_one_complete_event_per_attempt()
    {
        var rows = MatrixRows();
        var approvals = ApprovalCells(GateFiles.Require(GateFiles.Contract));

        var problems = CoverageProblems(rows, Marks(), Exempt, McpTheoryTools(), Mutates, TestExists, approvals);

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Contains(rows, r => r.Class == "S");   // the reader really saw the matrix
        // The approval column was read: the web chat and the control-plane terminal wait for approvals.
        Assert.True(NeedsApproval(rows.Single(r => r.Id == "A-01"), approvals));
        Assert.True(NeedsApproval(rows.Single(r => r.Id == "T-01"), approvals));
        Assert.False(NeedsApproval(rows.Single(r => r.Id == "A-03"), approvals));
        // The legend defines the marker the reader relies on.
        Assert.Contains("an \"Approval\" cell that begins with ✅", GateFiles.Require(GateFiles.Contract));
    }

    /// <summary>Review MINOR-5: the marker decides, not words in the cell — a terminal deny further on, a ✅ that does not
    /// lead, or a "none" never turn a path into one that waits for an approval, and a leading ✅ always does.</summary>
    [Theory]
    [InlineData("✅ approval by a second Admin; trigger Confirm → Deny", true)]
    [InlineData(" ✅ open + one close event", true)]
    [InlineData("Confirm → Deny in the gateway ✅; Allow runs unattended", false)]
    [InlineData("none; a key-owned `Confirm` → Deny", false)]
    [InlineData("approve-then-retry", false)]
    [InlineData("—", false)]
    public void Only_the_leading_marker_makes_a_path_wait_for_an_approval(string cell, bool waits)
    {
        var row = new PathRow("S-98", "S", null, "", ["container.restart"], null, 1);
        Assert.Equal(waits, NeedsApproval(row, new Dictionary<string, string> { ["S-98"] = cell }));
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
        var exempt = new Dictionary<string, Exemption> { ["S-06"] = new("monitors", "Nope.Gone"), ["S-99"] = new("x") };

        var problems = CoverageProblems(rows, marks, exempt, new HashSet<string>(), _ => true, n => n != "Nope.Gone");

        Assert.Contains(problems, p => p.StartsWith("S-99: performs host.exec", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("S-99: exempt, but no test pins", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("U-01: no [AuditPath] test proves the outcome(s) Failed", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("T.c: [AuditPath(\"S-42\")] names no row", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("M-01: 'restart_container' mutates but is not driven", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'Nope.Gone' does not exist", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.StartsWith("S-01", StringComparison.Ordinal));

        var unproved = CoverageProblems([Row("S-50", "autopatch.run")], [], new Dictionary<string, Exemption>(),
            new HashSet<string>(), _ => true, _ => true);
        Assert.Equal("S-50: no [AuditPath] test proves one complete audit event per attempt on this path (PRODUCT_CONTRACT.md:1)",
            Assert.Single(unproved));

        // A row that can wait for an approval — by its §7 "Approval" column, or a person's control-plane capability —
        // must prove the wait and the run after it; a pin must name a test method, not just a class.
        var waits = CoverageProblems([Row("A-09", "container.restart"), Row("U-98", "selfbackup.restore")],
            [("T.d", new AuditPathAttribute("A-09", Executed, Denied, Failed)), ("T.e", new AuditPathAttribute("U-98", Executed, Denied, Failed))],
            new Dictionary<string, Exemption>(), new HashSet<string>(), _ => true, _ => true,
            new Dictionary<string, string> { ["A-09"] = "✅ approval bound to the operation" });
        Assert.Contains(waits, p => p == "A-09: no [AuditPath] test proves the outcome(s) PendingApproval, Approved");
        Assert.Contains(waits, p => p == "U-98: no [AuditPath] test proves the outcome(s) PendingApproval, Approved");
        Assert.False(TestExists(nameof(AuditPathMatrixTests)));
        Assert.False(TestExists(Pin(nameof(AuditPathMatrixTests), nameof(Dispose))));
        Assert.True(TestExists(Pin(nameof(AuditPathMatrixTests), nameof(A_row_without_proof_or_with_a_capability_behind_an_exemption_fails_the_matrix))));
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
        taskId = "task-1", volumeName = "vol-1", backupId = "bk-1", ruleId = "rule-1", workloadId = "wl-1", appId = "app-1", webhookId = "wh-1",
        section = "general", account = "someone@example.com", binding = "binding-1", checkId = "check-1",
        // Payloads that must never reach an event (redaction, INV-51).
        password = Probe, token = Probe,
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
    // The rows with a control-plane capability (self-backup restore, the mTLS layer): their capability waits, and the
    // run after the second Admin's approval names it (asserted above for exactly those capabilities).
    [AuditPath("U-25", Pending, Approved)]
    [AuditPath("U-37", Pending, Approved)]
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
        var waits = 0;

        // One attempt, decided: a wait for an approval is its own event, then the run after a second Admin approved.
        async Task<AuditLogEntity> DecidedAsync(Func<Task> attempt, string expected)
        {
            var rows = await AttemptAsync(_db.Scopes, attempt);
            if (rows is not [{ Outcome: Pending } pending])
                return One(rows, expected, capabilityId, "Ui");
            waits++;
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
        // Exactly the capabilities no setting lifts the second Admin's approval from wait for it (RequiredOutcomesOf).
        Assert.Equal(ControlPlaneResources.Capabilities.Contains(capabilityId), waits > 0);

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
        Assert.Equal(capability.TargetKind, One(refused, Denied, capabilityId, "Ui", decided: false).TargetType);

        // Refused by the gateway before the policy step: a Viewer holds read only.
        if (capability.RequiredLevel != McpPermissionLevels.Read)
        {
            var below = await AttemptAsync(_db.Scopes,
                () => As(AppRole.Viewer).RunAsync(capabilityId, args, _ => { runs++; return Task.FromResult(true); }));
            One(below, Denied, capabilityId, "Ui", decided: false);
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

    /// <summary>
    /// INV-45, user decision 2026-11-07: an attempt begins when the handler requests the capability from the gateway
    /// (<c>RunAsync</c>/<c>RefuseAsync</c> or the surface's equivalent); a page returning before that — invalid input,
    /// a cancelled confirmation, an object not found — changes nothing and is no attempt. That holds because no page
    /// changes state itself: no component reaches the database, the file system or a process directly, so a change
    /// can only go through a guarded service, which refuses outside a gateway decision
    /// (<c>MutationScopeGuardTests</c>, <c>MutationEnforcementBootTests</c>, <c>MutationGatewayCoverageTests</c>).
    /// </summary>
    [Fact]
    public void A_ui_page_changes_nothing_itself_so_returning_before_the_gateway_is_no_attempt()
    {
        var components = Path.Combine(GateFiles.Root, "src", "Whiskers", "Components");
        var direct = new System.Text.RegularExpressions.Regex(
            @"\bMetricsDbContext\b|\bIDbContextFactory\b|\bSaveChanges(Async)?\(|\bExecute(Update|Delete)(Async)?\(|" +
            @"\bFile\.(Write|Append|Delete|Move|Copy|Create|Replace)\w*\(|\bDirectory\.(Delete|Move|Create)\w*\(|" +
            @"\bProcess\.Start\(|\bnew\s+(FileStream|StreamWriter|FileInfo|DirectoryInfo)\(|" +
            // Review MINOR-3: what else writes without the gateway — Identity (it writes through its own context), a
            // file opened for writing (not a browser upload's OpenReadStream), any DbContext, an outgoing HTTP call.
            @"\b(UserManager|SignInManager|RoleManager)<|\bWhiskersIdentityDbContext\b|\bDbContext\b|" +
            @"(?<![\w.])File\.Open(Write|Handle)?\(|\.OpenWrite\(|\bI?HttpClient(Factory)?\b");
        var files = Directory.EnumerateFiles(components, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(files);
        var offenders = files.SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (line, i))
                .Where(x => direct.IsMatch(x.line))
                .Select(x => $"{Path.GetRelativePath(components, f)}:{x.i + 1}: {x.line.Trim()}"))
            .ToList();
        Assert.True(offenders.Count == 0, "A page changes state itself, outside a gateway decision:\n" + string.Join("\n", offenders));
    }

    // ------------------------------------------------------------------------------------------------ M rows

    public static TheoryData<string> McpTools() => AuditCompletenessTests.MutatingMcpTools();

    /// <summary>
    /// M-xx: a direct MCP call of every mutating tool is one complete event per attempt — executed, failed as the tool
    /// reported it, failed by throwing, refused before the policy step (a key below the tool's level), refused by the
    /// kill switch where it applies. The tool's body is whatever runs inside <see cref="McpGuardrailFilter"/>: the
    /// filter wraps every tool call (<see cref="The_mcp_server_runs_every_tool_call_through_the_guardrail_filter"/>),
    /// so every return path of a tool — an early "not found" included — is inside the call's one event, and a tool's
    /// error answer is that event's failure (<c>McpCallOutcomeTests</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(McpTools))]
    [AuditPath("M-*", Executed, Denied, Failed)]
    public async Task Every_mutating_mcp_tool_call_is_one_complete_event_per_outcome(string tool)
    {
        var capability = new CapabilityCatalog().FindByMcpTool(tool);
        Assert.True(capability is not null, $"'{tool}' mutates but names no capability");
        using var mcp = new McpFilterHarness();
        async Task<List<AuditLogEntity>> CallAsync(Func<CancellationToken, ValueTask<ModelContextProtocol.Protocol.CallToolResult>> body)
        {
            var before = (await mcp.EventsAsync()).Select(r => r.Id).ToHashSet();
            try { await mcp.CallAsync(tool, body, ArgumentsFor()); }
            catch (InvalidOperationException) { /* the tool threw: rethrown as before the gateway existed */ }
            return (await mcp.EventsAsync()).Where(r => !before.Contains(r.Id)).ToList();
        }
        var ran = 0;
        ValueTask<ModelContextProtocol.Protocol.CallToolResult> Ok(CancellationToken t) { ran++; return McpFilterHarness.Ok(t); }

        var first = Assert.Single(await CallAsync(Ok));
        if (ran == 0)
        {
            // Refused by a rule no setting lifts (the control plane): still exactly one decided event.
            AssertComplete(first, Denied, capability!.Id, "Mcp");
            return;
        }
        AssertComplete(first, Executed, capability!.Id, "Mcp");

        One(await CallAsync(_ => ValueTask.FromResult(new ModelContextProtocol.Protocol.CallToolResult
        {
            IsError = true,
            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = "not found" }],
        })), Failed, capability.Id, "Mcp");
        One(await CallAsync(_ => throw new InvalidOperationException("the host did not answer")), Failed, capability.Id, "Mcp");

        if (capability.RequiredLevel != McpPermissionLevels.Read)
        {
            mcp.Principal = new AgentPrincipal(AgentPrincipalKind.McpKey, "read-key", McpPermissionLevels.Read, null, McpKeyId: "k2");
            One(await CallAsync(Ok), Denied, capability.Id, "Mcp", decided: false);
            mcp.Principal = McpFilterHarness.Key;
        }

        mcp.Policy.ReadOnlyMode = true;
        var killed = Assert.Single(await CallAsync(Ok));
        AssertComplete(killed, killed.Outcome!, capability.Id, "Mcp");
        if (killed.Outcome != Denied)
            Assert.Contains(capability.Id, new[] { "guardrails.kill_switch_off", "guardrails.save", "account.password_change" });
    }

    /// <summary>M-xx: the MCP server runs every tool call through <see cref="McpGuardrailFilter"/> — the one place the
    /// call's event is written — so no tool body runs outside it. A tool registered another way, or the filter taken
    /// out, fails here.</summary>
    [Fact]
    public void The_mcp_server_runs_every_tool_call_through_the_guardrail_filter()
    {
        var hosting = File.ReadAllText(Path.Combine(GateFiles.Root, "src", "Whiskers", "Startup", "WhiskersHostingExtensions.cs"));
        Assert.Contains(".WithRequestFilters(filters => filters.AddCallToolFilter(McpGuardrailFilter.Create))", hosting);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(hosting, @"AddCallToolFilter\("));
    }

    // ------------------------------------------------------------------------------------------------ cancelled attempts

    /// <summary>A host whose container listing does not answer until its caller gives up, behind the production target
    /// resolver.</summary>
    private static (FakeDocker Docker, MutationTargetResolver Targets) HangingTargets()
    {
        var docker = new FakeDocker(new ContainerInfo { Id = "web-1", Name = "web", ServerId = "local", State = "running" },
            new ContainerInfo { Id = "whiskers-1", Name = "whiskers", ServerId = "local", State = "running" })
        {
            ListContainersHangs = true,
        };
        return (docker, new MutationTargetResolver(new TestServices(docker,
            new FakeServerConfig(new ServerConfig { Id = "local", Name = "Local", IsDefault = true }))));
    }

    /// <summary>M-xx (review B-1): an MCP client that goes away while the gateway resolves the target has begun its
    /// attempt — it asked for the capability — so the attempt is one Denied event, "cancelled before its decision",
    /// recorded although the call's token is cancelled; the tool never runs and the caller still sees the
    /// cancellation.</summary>
    [Fact]
    public async Task An_mcp_call_cancelled_while_its_target_resolves_is_one_denied_event()
    {
        var (docker, targets) = HangingTargets();
        using var mcp = new McpFilterHarness { Targets = targets };
        using var gone = new CancellationTokenSource();
        var ran = 0;

        var call = mcp.CallAsync("restart_container", t => { ran++; return McpFilterHarness.Ok(t); },
            new { containerId = "web", serverId = "local" }, ct: gone.Token);
        await docker.ListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await gone.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        var row = One(await mcp.EventsAsync(), Denied, "container.restart", "Mcp", decided: false);
        Assert.Contains(MutationGateway.CancelledBeforeDecision, row.Details);
        Assert.Equal(0, ran);
    }

    /// <summary>Review B-1 and round 3, every step before the intent: a call cancelled while its target resolves, while
    /// the gate raises its approval (the event names that approval), after its decision and before its intent, or
    /// while its intent is written (the race: the intent ends as the refusal) is one Denied event, nothing runs and the
    /// caller sees its cancellation; a call cancelled while it runs is one Failed outcome — never a refusal and an
    /// outcome both.</summary>
    [Theory]
    [InlineData("target")]
    [InlineData("approval")]
    [InlineData("decided")]
    [InlineData("intent")]
    [InlineData("run")]
    public async Task A_gateway_call_cancelled_at_any_step_is_exactly_one_event(string step)
    {
        var (docker, hanging) = HangingTargets();
        docker.ListContainersHangs = step == "target";
        var policy = Permissive();
        // The control plane waits for an Admin's approval (INV-59), for a person at the UI too.
        var container = step == "approval" ? "whiskers-1" : "web-1";
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        using var gone = new CancellationTokenSource();
        var approvalStore = new ApprovalStore(policy: store, persistence: _db.Persistence());
        var approvals = new CancelAfterRaising(new ApprovalGate(approvalStore, store), gone);
        IMutationAuditSink sink = new AuditLogMutationSink(_db.Scopes);
        if (step == "intent") sink = new CancelOnIntent(sink, gone);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), sink, [],
            NullLogger<MutationGateway>.Instance, targets: hanging, approvals: approvals,
            decisions: step == "decided" ? new CancelOnRecord(new DbPolicyDecisionStore(_db.Scopes), gone)
                : new DbPolicyDecisionStore(_db.Scopes),
            journal: new DbOperationJournal(_db.Scopes));
        var ui = new UiMutationRunner(gateway, new CapabilityCatalog(), new Person(AppRole.Admin, Admin));
        var ran = 0;
        var cancelled = false;

        var rows = await AttemptAsync(_db.Scopes, async () =>
        {
            var attempt = ui.RunAsync("container.restart", new { containerId = container, serverId = "local" }, async t =>
            {
                ran++;
                await gone.CancelAsync();
                t.ThrowIfCancellationRequested();
                return true;
            }, gone.Token);
            if (step == "target")
            {
                await docker.ListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await gone.CancelAsync();
            }
            try { await attempt; }
            catch (OperationCanceledException) { cancelled = true; }
        });

        if (step == "run")
        {
            Assert.Equal(1, ran);
            Assert.Contains("cancelled while it ran", One(rows, Failed, "container.restart", "Ui").Details);
            return;
        }
        Assert.True(cancelled, $"{step}: the caller did not see its cancellation");
        var row = One(rows, Denied, "container.restart", "Ui", decided: step != "target");
        var expected = step == "target" ? MutationGateway.CancelledBeforeDecision : MutationGateway.CancelledBeforeRun;
        Assert.True(row.Details!.Contains(expected, StringComparison.Ordinal), $"{step}: {row.Details}");
        Assert.Equal(0, ran);
        if (step == "approval")
        {
            // The gate had raised (persisted) the approval before the cancellation: the event names it.
            Assert.NotNull(approvals.Raised);
            Assert.Equal(approvals.Raised, row.ApprovalId);
            Assert.NotNull(approvalStore.Get(approvals.Raised!));
        }
        else
        {
            Assert.Null(row.ApprovalId);
        }
    }

    /// <summary>The real approval gate, except that once it has raised (and persisted) this call's approval, the caller
    /// is gone: the answer never reaches the gateway, which sees the cancellation.</summary>
    private sealed class CancelAfterRaising(ApprovalGate inner, CancellationTokenSource caller) : IApprovalGate
    {
        public string? Raised { get; private set; }

        public async Task<ApprovalGateResult> CheckAsync(ApprovalSubject subject, string reason,
            IReadOnlyList<string> matchedRules, CancellationToken ct)
        {
            var answer = await inner.CheckAsync(subject, reason, matchedRules, CancellationToken.None);
            Raised = answer.ApprovalId;
            await caller.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return answer;
        }

        public string? OpenOperationOf(string binding) => null;

        public string? OpenApprovalOf(string binding, string operationId) => inner.OpenApprovalOf(binding, operationId);

        public Task<bool> TryConsumeAsync(string approvalId, string operationId, CancellationToken ct) =>
            inner.TryConsumeAsync(approvalId, operationId, ct);
    }

    /// <summary>A decision store that records the decision, then finds its caller gone.</summary>
    private sealed class CancelOnRecord(IPolicyDecisionStore inner, CancellationTokenSource caller) : IPolicyDecisionStore
    {
        public async Task RecordAsync(PolicyDecisionRecord record, CancellationToken ct)
        {
            await inner.RecordAsync(record, ct);
            await caller.CancelAsync();
        }
    }

    /// <summary>An audit sink whose caller goes away while the intent is written (the race after the gateway's last
    /// check before the intent).</summary>
    private sealed class CancelOnIntent(IMutationAuditSink inner, CancellationTokenSource caller) : IMutationAuditSink
    {
        public async Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            await caller.CancelAsync();
            return await inner.BeginAsync(operationId, request, capability, approval, ct);
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct) =>
            inner.CompleteAsync(auditId, operationId, status, verification, reason, duration, ct);

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, IReadOnlyList<MutationDomainResult>? results, CancellationToken ct) =>
            inner.CompleteAsync(auditId, operationId, status, verification, reason, duration, results, ct);

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct) =>
            inner.RecordDeniedAsync(operationId, request, capability, status, reason, ct);

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, string? approvalId, CancellationToken ct) =>
            inner.RecordDeniedAsync(operationId, request, capability, status, reason, approvalId, ct);
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
            ["run_scheduled_task"] = new("run_scheduled_task", McpPermissionLevels.Write, "Scheduler",
                typeof(AuditPathMatrixTests).GetMethod(nameof(RunScheduledTask), BindingFlags.NonPublic | BindingFlags.Static)!,
                new AgentToolDefinition("run_scheduled_task", "Runs a scheduled task now.", JsonDocument.Parse("{}").RootElement)),
        };
    }

    /// <summary>What <c>run_scheduled_task</c> runs: set by the test, on its own async flow.</summary>
    private static readonly AsyncLocal<Func<Task<string>>?> NestedRun = new();

    /// <summary>The tool body of <c>run_scheduled_task</c>: the task's run, nested in the tool call's operation.</summary>
    private static Task<string> RunScheduledTask([Description("The task")] string taskId) =>
        NestedRun.Value?.Invoke() ?? Task.FromResult($"no task {taskId}");

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
        _agentGateway = gateway;
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

    /// <summary>The gateway of the last <see cref="Agent"/>: a nested automation run goes through it, as in the app.</summary>
    private MutationGateway? _agentGateway;

    /// <summary>A-03 (review MINOR 7): a mutation nested in an AI trigger's tool call — a scheduled task the trigger
    /// runs — is one event of its own, linked to the call (parent operation, trigger), decided against the trigger's
    /// pinned preset: run, or refused by it. The call itself is one event too.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [AuditPath("A-03", Executed, Denied)]
    public async Task A_mutation_nested_in_an_ai_trigger_call_is_one_event_of_its_own(bool protectedByThePreset)
    {
        var (invoker, policy, _) = Agent();
        // The trigger's pinned preset protects the container: the nested restart is refused by it (decided), the call
        // itself still ran — one event each.
        if (protectedByThePreset) policy.ProtectedResources = ["web*"];
        var f = AutomationMutationRunnerTests.Build(requireOwner: true, gateway: _agentGateway);
        f.Whitelist.Allowed.Add(Admin);
        f.Roles.Map[Admin] = AppRole.Admin;
        var world = new TaskWorld(works: true);
        NestedRun.Value = async () => (await world.Executor(f, policy).ExecuteAsync(TaskOf(ScheduledTaskType.ContainerRestart))).Output;
        var trigger = new AgentPrincipal(AgentPrincipalKind.Trigger, "AI trigger 'restart'", McpPermissionLevels.Write, null,
            TriggerId: "t1");
        var context = new AgentContext("trigger-run-2", trigger, AgentOrigin.Trigger, policy, new PinnedPolicy("custom", policy));
        var call = new AgentToolCall("call", "run_scheduled_task", JsonSerializer.Serialize(new { taskId = "t1" }));

        string? output = null;
        var ran = await AttemptAsync(_db.Scopes, async () => output = (await invoker.InvokeAsync(call, context)).ToString());

        Assert.True(ran.Count == 2, output + " | " + string.Join("; ", ran.Select(r => $"{r.Action} {r.Outcome} {r.Details}")));
        var outer = ran.Single(r => r.Action == "scheduler.run");
        AssertComplete(outer, Executed, "scheduler.run", "Agent");
        var inner = ran.Single(r => r.Action == "container.restart");
        AssertComplete(inner, protectedByThePreset ? Denied : Executed, "container.restart", "Automation", targetId: "web-1");
        Assert.Equal(outer.OperationId, inner.ParentOperationId);
        Assert.Equal(outer.TriggerId, inner.TriggerId);
        Assert.False(string.IsNullOrEmpty(inner.TriggerId));
        Assert.Equal(protectedByThePreset ? 0 : 1, world.Docker.Actions.Count);
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

    /// <summary>A stand-in for an interface whose members a test scripts by name; any other member throws.</summary>
    public class Scripted<T> : DispatchProxy where T : class
    {
        public Dictionary<string, Func<object?[], object?>> On { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            On.TryGetValue(method!.Name, out var answer) ? answer(args ?? []) : throw new NotSupportedException(method.Name);

        public static (T Service, Scripted<T> Script) Create()
        {
            var service = DispatchProxy.Create<T, Scripted<T>>();
            return (service, (Scripted<T>)(object)service);
        }
    }

    /// <summary>A host whose commands all end with <see cref="ExitCode"/>.</summary>
    private sealed class ScriptedHost : IHostCommandExecutor
    {
        public int ExitCode { get; set; }
        public List<string> Commands { get; } = [];
        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add(command);
            return Task.FromResult(new CommandResult { ExitCode = ExitCode, Output = "ok", Error = ExitCode == 0 ? "" : "exit 1" });
        }
    }

    /// <summary>What a scheduled task acts on: a web container, a database container (its environment holds a
    /// password), a stopped container a cleanup removes, a volume, the self-backup and the host. <see cref="Works"/>
    /// decides whether each of them does its job.</summary>
    private sealed class TaskWorld
    {
        public FakeDocker Docker { get; } = new(
            new ContainerInfo { Id = "web-1", Name = "web", Image = "nginx:1", State = "running" },
            new ContainerInfo { Id = "db-1", Name = "db", Image = "postgres:16", State = "running" },
            new ContainerInfo { Id = "mongo-1", Name = "mongo", Image = "mongo:7", State = "running" },
            new ContainerInfo { Id = "old-1", Name = "old", Image = "busybox:1", State = "exited" })
        {
            Env = [new("POSTGRES_USER", "app"), new("POSTGRES_PASSWORD", Probe), new("POSTGRES_DB", "app")],
        };
        public ScriptedHost Host { get; } = new();
        public List<string> Ran { get; } = [];
        private readonly IDatabaseService _db;
        private readonly IVolumeBackupService _volumes;
        private readonly IBackupService _self;

        public TaskWorld(bool works)
        {
            Docker.AllowMutations = works;
            Host.ExitCode = works ? 0 : 1;
            var (db, dbScript) = Scripted<IDatabaseService>.Create();
            dbScript.On[nameof(IDatabaseService.BackupDatabaseAsync)] = _ =>
            {
                Ran.Add("db.backup");
                return Task.FromResult(works ? (true, "/backups/app.sql.gz", 2048L, "") : (false, "", 0L, "pg_dump failed"));
            };
            var (volumes, volumeScript) = Scripted<IVolumeBackupService>.Create();
            volumeScript.On[nameof(IVolumeBackupService.BackupVolumeAsync)] = a =>
            {
                Ran.Add("volume.backup");
                return works
                    ? Task.FromResult(new VolumeBackupEntity { VolumeName = (string)a[0]!, FileName = "vol.tar.gz" })
                    : Task.FromException<VolumeBackupEntity>(new IOException("the disk is full"));
            };
            var (self, selfScript) = Scripted<IBackupService>.Create();
            selfScript.On[nameof(IBackupService.CreateBackupAsync)] = _ =>
            {
                Ran.Add("selfbackup.create");
                return works
                    ? Task.FromResult(new SelfBackupInfo { FileName = "self.tar.gz" })
                    : Task.FromException<SelfBackupInfo>(new IOException("the disk is full"));
            };
            (_db, _volumes, _self) = (db, volumes, self);
        }

        public TaskExecutor Executor(AutomationMutationRunnerTests.Fixture f, GuardrailPolicy policy,
            IAutomationMutationRunner? runner = null) =>
            new(Docker, _db, _volumes, _self, Host, runner ?? f.Runner, NullLogger<TaskExecutor>.Instance,
                notify: new FakeNotifications(), guardrails: new AgentToolTestHelpers.FixedGuardrailStore(policy));
    }

    /// <summary>A task of <paramref name="type"/> that can run, owned by the Admin.</summary>
    private static ScheduledTaskEntity TaskOf(ScheduledTaskType type, bool owned = true)
    {
        var task = new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "nightly", TaskType = type, CreatedBy = owned ? AdminId : "", CreatedVia = owned ? AdminId : null,
        };
        switch (type)
        {
            case ScheduledTaskType.ContainerRestart: task.TargetId = "web-1"; task.TargetName = "web"; break;
            case ScheduledTaskType.DbBackup: task.TargetId = "db-1"; task.TargetName = "db"; break;
            case ScheduledTaskType.VolumeBackup: task.TargetId = "vol-1"; task.TargetName = "web"; break;
            // The command carries a credential: the event keeps none of it.
            case ScheduledTaskType.CustomCommand: task.Config = JsonSerializer.Serialize(new { command = $"curl -u admin:{Probe} https://example.com" }); break;
        }
        return task;
    }

    /// <summary>The target the task's event names when it runs.</summary>
    private static string? TargetOf(ScheduledTaskType type) => type switch
    {
        ScheduledTaskType.ContainerRestart => "web-1",
        ScheduledTaskType.DbBackup => "db-1",
        ScheduledTaskType.VolumeBackup => "vol-1",
        _ => null,
    };

    /// <summary>Every way a task of <paramref name="type"/> is refused before it asks the gateway, and the event's
    /// target: each must be the gateway's Denied event of the capability, without a decision.</summary>
    private static IEnumerable<(string Case, Action<ScheduledTaskEntity, TaskWorld, GuardrailPolicy> Set, string TargetId)>
        PreGatewayRefusals(ScheduledTaskType type)
    {
        switch (type)
        {
            case ScheduledTaskType.ContainerRestart:
                yield return ("no container", (t, _, _) => t.TargetId = null, "");
                break;
            case ScheduledTaskType.DbBackup:
                yield return ("no container", (t, _, _) => t.TargetId = null, "");
                yield return ("container not found", (t, _, _) => t.TargetId = "gone", "gone");
                yield return ("not a database", (t, _, _) => t.TargetId = "web-1", "web-1");
                yield return ("listing throws", (_, w, _) => w.Docker.ListContainersThrows = true, "db-1");
                yield return ("environment throws", (_, w, _) => w.Docker.Env = null, "db-1");
                // A Docker call that timed out escapes the lookup; the dispatcher's backstop records it (review round 11).
                yield return ("listing cancelled", (_, w, _) => w.Docker.ListContainersException = new TaskCanceledException("The request timed out."), "db-1");
                // INV-19's name check (review round 10, BLOCKER-1): refused before the gateway, never without an event.
                yield return ("unsafe database name", (t, _, _) => t.Config = "{\"database\":\"-x\"}", "db-1");
                yield return ("unsafe default name from the container",
                    (_, w, _) => w.Docker.Env = [new("POSTGRES_USER", "app"), new("POSTGRES_DB", "-x")], "db-1");
                yield return ("dotted MongoDB name",
                    (t, _, _) => { t.TargetId = "mongo-1"; t.TargetName = "mongo"; t.Config = "{\"database\":\"app.x\"}"; }, "mongo-1");
                break;
            case ScheduledTaskType.VolumeBackup:
                yield return ("no volume", (t, _, _) => t.TargetId = null, "");
                break;
            case ScheduledTaskType.CustomCommand:
                yield return ("no command", (t, _, _) => t.Config = "{}", "local");
                break;
            case ScheduledTaskType.Cleanup:
                yield return ("protected stopped container", (_, _, p) => p.ProtectedResources = ["old*"], "local");
                yield return ("listing throws", (_, w, _) => w.Docker.ListContainersThrows = true, "local");
                yield return ("listing cancelled", (_, w, _) => w.Docker.ListContainersException = new TaskCanceledException("The request timed out."), "local");
                break;
        }
    }

    /// <summary>The real runner, with faults around it: it throws synchronously before it hands a run to the gateway
    /// (as its owner resolution can), or after the gateway wrote the run's event or a refusal.</summary>
    private sealed class FaultingRunner(IAutomationMutationRunner inner) : IAutomationMutationRunner
    {
        public Task<MutationOutcome<T>> RunAsync<TTarget, T>(AutomationActor actor, string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            RunAsync(actor, capabilityId, arguments, token => action(TestTargets.Of<TTarget>(capabilityId, arguments), token), ct);

        public Task<MutationOutcome<bool>> RunAsync<TTarget>(AutomationActor actor, string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            RunAsync(actor, capabilityId, arguments, token => action(TestTargets.Of<TTarget>(capabilityId, arguments), token), ct);

        /// <summary>The next run throws before the gateway sees it — once.</summary>
        public bool ThrowBeforeGatewayOnce { get; set; }
        /// <summary>Thrown once — the first time the gateway has executed a run (its event is written).</summary>
        public Exception? AfterRun { get; set; }
        /// <summary>Thrown once — the first time the gateway has recorded a refusal: a second refusal would be on record.</summary>
        public Exception? AfterRefusal { get; set; }

        public Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            if (ThrowBeforeGatewayOnce)
            {
                ThrowBeforeGatewayOnce = false;
                throw new InvalidOperationException("The owner's key store could not be read.");
            }
            return Then(inner.RunAsync(actor, capabilityId, arguments, action, ct), Take(() => AfterRun, () => AfterRun = null));
        }

        public Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task> action, CancellationToken ct = default)
        {
            if (ThrowBeforeGatewayOnce)
            {
                ThrowBeforeGatewayOnce = false;
                throw new InvalidOperationException("The owner's key store could not be read.");
            }
            return Then(inner.RunAsync(actor, capabilityId, arguments, action, ct), Take(() => AfterRun, () => AfterRun = null));
        }

        public Task<MutationOutcome<object?>> RefuseAsync(AutomationActor actor, string capabilityId, object arguments,
            string reason, CancellationToken ct = default) =>
            Then(inner.RefuseAsync(actor, capabilityId, arguments, reason, ct), Take(() => AfterRefusal, () => AfterRefusal = null));

        public string? LostAuthority(AutomationActor actor, string capabilityId) => inner.LostAuthority(actor, capabilityId);

        private static Exception? Take(Func<Exception?> read, Action clear)
        {
            var fault = read();
            clear();
            return fault;
        }

        private static async Task<TOut> Then<TOut>(Task<TOut> written, Exception? fault)
        {
            var outcome = await written;
            if (fault is not null) throw fault;
            return outcome;
        }
    }

    /// <summary>S-01, the dispatcher's backstop (review round 12, MINOR-2 and MINOR-3): a run is one event whatever
    /// throws, and never two. A runner that throws before it hands the run to the gateway leaves the backstop's one
    /// Denied event; an exception after the gateway executed the run, or an <see cref="OperationCanceledException"/>
    /// after the gateway recorded a refusal, adds none.</summary>
    [Fact]
    public async Task A_scheduled_run_is_one_event_whatever_throws_around_the_gateway()
    {
        var policy = Permissive();
        var f = Automation(policy: policy);

        // Thrown synchronously before the gateway: the run never reached it — one Denied event, nothing ran.
        var before = new TaskWorld(works: true);
        var beforeRunner = new FaultingRunner(f.Runner) { ThrowBeforeGatewayOnce = true };
        var beforeRows = await AttemptAsync(_db.Scopes,
            () => before.Executor(f, policy, beforeRunner).ExecuteAsync(TaskOf(ScheduledTaskType.ContainerRestart)));
        var backstop = One(beforeRows, Denied, "container.restart", "Automation", decided: false, targetId: "web-1");
        Assert.Contains("The run ended before its decision (InvalidOperationException).", backstop.Details);
        Assert.Empty(before.Docker.Actions);

        // Thrown after the gateway executed the run: its event is the gateway's, no second one.
        var after = new TaskWorld(works: true);
        var afterRunner = new FaultingRunner(f.Runner) { AfterRun = new InvalidOperationException("after the run") };
        var (ok, output) = (false, "");
        var afterRows = await AttemptAsync(_db.Scopes, async () =>
            (ok, output) = await after.Executor(f, policy, afterRunner).ExecuteAsync(TaskOf(ScheduledTaskType.ContainerRestart)));
        One(afterRows, Executed, "container.restart", "Automation", targetId: "web-1");
        Assert.False(ok);
        Assert.Equal("after the run", output);

        // A cancellation after the gateway recorded the run's refusal: that refusal is its one event.
        var refused = new TaskWorld(works: true);
        var refusedRunner = new FaultingRunner(f.Runner) { AfterRefusal = new OperationCanceledException("after the refusal") };
        var task = TaskOf(ScheduledTaskType.DbBackup);
        task.TargetId = null;
        var refusedRows = await AttemptAsync(_db.Scopes, () => refused.Executor(f, policy, refusedRunner).ExecuteAsync(task));
        var refusal = One(refusedRows, Denied, "db.backup", "Automation", decided: false, targetId: "");
        Assert.Contains("No container ID specified", refusal.Details);
        Assert.Empty(refused.Ran);

        // The owner's store fails on every read (review round 13): not a throw before the gateway any more, but a
        // principal without authority — one Denied event naming the cause, nothing ran.
        f.Accounts.Unreadable = true;
        var unreadable = new TaskWorld(works: true);
        var unreadableRows = await AttemptAsync(_db.Scopes,
            () => unreadable.Executor(f, policy).ExecuteAsync(TaskOf(ScheduledTaskType.ContainerRestart)));
        var denied = One(unreadableRows, Denied, "container.restart", "Automation", decided: false, targetId: "web-1");
        Assert.Contains("owner unreadable", denied.Details);
        Assert.Contains("holds no authority", denied.Details);
        Assert.Equal(AdminId, denied.OnBehalfOfId);
        Assert.Empty(unreadable.Docker.Actions);
    }

    public static TheoryData<ScheduledTaskType> TaskTypes()
    {
        var data = new TheoryData<ScheduledTaskType>();
        foreach (var type in Enum.GetValues<ScheduledTaskType>()) data.Add(type);
        return data;
    }

    /// <summary>S-01: every task type's run is one event on its owner's authority — executed, failed, refused by the
    /// kill switch, refused because the owner lost the role, refused because it has no owner — and every way the
    /// executor refuses a run before it asks the gateway (no target or command, a database container that is missing,
    /// is none or cannot be read, a database name INV-19 refuses, a protected stopped container in a cleanup, a listing that fails) is the gateway's
    /// Denied event of that capability, without a decision. Found by this matrix (INV-45): those refusals wrote no
    /// event.</summary>
    [Theory]
    [MemberData(nameof(TaskTypes))]
    [AuditPath("S-01", Executed, Denied, Failed)]
    public async Task Every_scheduled_task_type_is_one_complete_event_per_attempt(ScheduledTaskType type)
    {
        var capability = TaskExecutor.CapabilityOf(type);
        var policy = Permissive();
        var f = Automation(policy: policy);
        Task Run(bool works, ScheduledTaskEntity? task = null, TaskWorld? world = null) =>
            (world ?? new TaskWorld(works)).Executor(f, policy).ExecuteAsync(task ?? TaskOf(type));

        var executed = new TaskWorld(works: true);
        var row = One(await AttemptAsync(_db.Scopes, () => Run(true, world: executed)), Executed, capability, "Automation",
            targetId: TargetOf(type));
        Assert.Equal("task:t1", row.PrincipalId);
        Assert.Equal(AdminId, row.OnBehalfOfId);

        One(await AttemptAsync(_db.Scopes, () => Run(works: false)), Failed, capability, "Automation", targetId: TargetOf(type));

        policy.ReadOnlyMode = true;
        var killed = new TaskWorld(works: true);
        One(await AttemptAsync(_db.Scopes, () => Run(true, world: killed)), Denied, capability, "Automation",
            targetId: TargetOf(type));
        policy.ReadOnlyMode = false;

        if (type == ScheduledTaskType.DbBackup)
        {
            // An environment that repeats a key in another case (Docker's client proxy configuration sets HTTP_PROXY
            // and http_proxy) is read, not a failure: the backup runs, one event (review round 11, B-1).
            var duplicated = new TaskWorld(works: true);
            duplicated.Docker.Env = [new("POSTGRES_USER", "app"), new("POSTGRES_PASSWORD", Probe), new("POSTGRES_DB", "app"),
                new("HTTP_PROXY", "http://proxy:3128"), new("http_proxy", "http://proxy:3128")];
            One(await AttemptAsync(_db.Scopes, () => Run(true, world: duplicated)), Executed, capability, "Automation",
                targetId: TargetOf(type));
            Assert.Equal(["db.backup"], duplicated.Ran);
        }

        foreach (var (name, set, targetId) in PreGatewayRefusals(type))
        {
            var world = new TaskWorld(works: true);
            var task = TaskOf(type);
            set(task, world, policy);
            var refused = await AttemptAsync(_db.Scopes, () => Run(true, task, world));
            Assert.True(refused.Count == 1, $"{type}, {name}: {refused.Count} events");
            var refusal = One(refused, Denied, capability, "Automation", decided: false, targetId: targetId);
            // The resolved name and the reason (review round 11, m-3); the backstop names the exception's type only.
            switch (name)
            {
                case "unsafe database name" or "unsafe default name from the container":
                    Assert.Contains("\"database\":\"-x\"", refusal.Arguments);
                    Assert.Contains("Invalid database '-x'", refusal.Details);
                    break;
                case "dotted MongoDB name":
                    Assert.Contains("\"database\":\"app.x\"", refusal.Arguments);
                    Assert.Contains("Invalid database 'app.x'", refusal.Details);
                    Assert.Contains("no '.'", refusal.Details);
                    break;
                case "listing cancelled":
                    Assert.Contains("The run ended before its decision (TaskCanceledException).", refusal.Details);
                    break;
            }
            Assert.Empty(world.Ran);
            Assert.Empty(world.Host.Commands);
            Assert.Empty(world.Docker.Actions);
            policy.ProtectedResources = [];
        }

        var unowned = new TaskWorld(works: true);
        Assert.Equal("unowned:task:t1", One(await AttemptAsync(_db.Scopes, () => Run(true, TaskOf(type, owned: false), unowned)),
            Denied, capability, "Automation", owned: false, decided: false, targetId: TargetOf(type)).PrincipalId);

        f.Roles.Map[Admin] = AppRole.Viewer;
        var demoted = new TaskWorld(works: true);
        One(await AttemptAsync(_db.Scopes, () => Run(true, world: demoted)), Denied, capability, "Automation",
            decided: false, targetId: TargetOf(type));

        // Nothing ran but the two runs that were allowed.
        Assert.Empty(killed.Ran.Concat(unowned.Ran).Concat(demoted.Ran));
        Assert.Empty(killed.Host.Commands.Concat(unowned.Host.Commands).Concat(demoted.Host.Commands));
    }

    /// <summary>S-01: a type none of the six (a row written by a newer or broken version) is refused before any
    /// decision — one Denied event under its raw name, which names no capability, so nothing can run.</summary>
    [Fact]
    [AuditPath("S-01", Denied)]
    public async Task A_task_of_an_unknown_type_is_one_denied_event()
    {
        var f = Automation();
        var task = TaskOf(ScheduledTaskType.ContainerRestart);
        task.TaskType = (ScheduledTaskType)99;

        var refused = await AttemptAsync(_db.Scopes, () => new TaskWorld(true).Executor(f, Permissive()).ExecuteAsync(task));

        var row = One(refused, Denied, "scheduler.task_type.99", "Automation", decided: false, targetId: "");
        Assert.Equal("unknown", row.TargetType);
    }

    /// <summary>S-01: retention after a backup deletes on its own decision — each deletion is one event of its own
    /// capability (<c>volume.backup_delete</c> per backup, <c>selfbackup.prune</c>, <c>db.backup_delete</c>), nested
    /// in nothing, next to the backup's event.</summary>
    [Theory]
    [InlineData(ScheduledTaskType.VolumeBackup, "volume.backup_delete", 2)]
    [InlineData(ScheduledTaskType.SelfBackup, "selfbackup.prune", 1)]
    [InlineData(ScheduledTaskType.DbBackup, "db.backup_delete", 1)]
    [AuditPath("S-01", Executed)]
    public async Task Each_retention_delete_of_a_scheduled_backup_is_one_complete_event(ScheduledTaskType type, string delete,
        int deletions)
    {
        var f = Automation();
        var world = new TaskWorld(works: true);
        var (volumes, volumeScript) = Scripted<IVolumeBackupService>.Create();
        volumeScript.On[nameof(IVolumeBackupService.BackupVolumeAsync)] = _ =>
            Task.FromResult(new VolumeBackupEntity { VolumeName = "vol-1", FileName = "new.tar.gz" });
        volumeScript.On[nameof(IVolumeBackupService.ListBackupsAsync)] = _ => Task.FromResult(Enumerable.Range(0, 3)
            .Select(i => new VolumeBackupEntity { BackupId = $"b{i}", VolumeName = "vol-1", CreatedAt = DateTime.UtcNow.AddDays(-i) })
            .ToList());
        volumeScript.On[nameof(IVolumeBackupService.DeleteBackupAsync)] = _ => Task.CompletedTask;
        var (self, selfScript) = Scripted<IBackupService>.Create();
        selfScript.On[nameof(IBackupService.CreateBackupAsync)] = _ => Task.FromResult(new SelfBackupInfo { FileName = "s.tar.gz" });
        selfScript.On[nameof(IBackupService.PruneAsync)] = _ => Task.CompletedTask;
        var (db, dbScript) = Scripted<IDatabaseService>.Create();
        dbScript.On[nameof(IDatabaseService.BackupDatabaseAsync)] = _ => Task.FromResult((true, "/backups/app.sql.gz", 1L, ""));
        var executor = new TaskExecutor(world.Docker, db, volumes, self, world.Host, f.Runner, NullLogger<TaskExecutor>.Instance,
            notify: new FakeNotifications());
        var task = TaskOf(type);
        task.Config = JsonSerializer.Serialize(new { maxBackups = "1", database = "app" });

        var rows = await AttemptAsync(_db.Scopes, () => executor.ExecuteAsync(task));

        AssertComplete(Assert.Single(rows, r => r.Action == TaskExecutor.CapabilityOf(type)), Executed,
            TaskExecutor.CapabilityOf(type), "Automation");
        var deletes = rows.Where(r => r.Action == delete).ToList();
        Assert.Equal(deletions, deletes.Count);
        Assert.Equal(deletions + 1, rows.Count);
        foreach (var d in deletes) AssertComplete(d, Executed, delete, "Automation");
        Assert.Equal(rows.Count, rows.Select(r => r.OperationId).Distinct().Count());
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
        Assert.Equal("unowned:autopatch",
            One(unowned, Denied, "autopatch.run", "Automation", owned: false, decided: false).PrincipalId);

        // A suspended entry keeps its owner: the refusal is on record on their behalf.
        owners.Blocked.Add(IAutomationOwnerStore.AutoPatch);
        var blocked = await AttemptAsync(_db.Scopes, () => AutoPatch(f, owners, none).RunNightlyAsync(CancellationToken.None));
        Assert.Equal(AdminId, One(blocked, Denied, "autopatch.run", "Automation", decided: false).OnBehalfOfId);

        // An owner file that could not be read names nobody.
        var unreadable = new MemoryAutomationOwners();
        unreadable.Unreadable(IAutomationOwnerStore.AutoPatch);
        var unread = await AttemptAsync(_db.Scopes, () => AutoPatch(f, unreadable, none).RunNightlyAsync(CancellationToken.None));
        Assert.Equal("unowned:autopatch", One(unread, Denied, "autopatch.run", "Automation", owned: false, decided: false).PrincipalId);
        Assert.Empty(none.Calls);
    }
}
