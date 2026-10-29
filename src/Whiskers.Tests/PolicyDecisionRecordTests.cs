using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>INV-22 (R1-12): every policy decision is persisted — with its principal, capability, resolved target,
/// normalized input hash, policy version, preset, matched rules and verdict — before the mutation runs; approvals and
/// audit rows name it; policy changes are new versions that leave stored ones untouched.</summary>
public sealed class PolicyDecisionRecordTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sw-decisions-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public PolicyDecisionRecordTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_dbPath);
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private static readonly MutationTarget Target = new("container", "c1", "web", "srv1") { Related = ["db"] };

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static MutationRequest Request(string argsJson = """{"containerId":"web","serverId":"srv1"}""") =>
        new("container.start", new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com",
                McpPermissionLevels.Write), MutationSurface.Ui, Target, Args(argsJson), OperationId: "op-1");

    private MutationGateway Gateway(IMutationPolicy policy, IMutationAuditSink audit, IPolicyDecisionStore? decisions = null,
        IApprovalGate? approvals = null, IMutationTargetResolver? targets = null) =>
        new(new CapabilityCatalog(), policy, audit, [], NullLogger<MutationGateway>.Instance, targets: targets,
            approvals: approvals,
            decisions: decisions ?? new DbPolicyDecisionStore(_sp.GetRequiredService<IServiceScopeFactory>()));

    private List<PolicyDecisionEntity> Decisions()
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>().PolicyDecisions.AsNoTracking().ToList();
    }

    private List<PolicySnapshotEntity> Snapshots()
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>().PolicySnapshots.AsNoTracking().ToList();
    }

    [Fact]
    public async Task The_decision_is_stored_with_every_field_before_the_mutation_runs()
    {
        var audit = new RecordingAudit();
        var policy = GuardrailPolicy.SafeDefault();
        var gateway = Gateway(new FixedPolicy(MutationVerdict.Allow, "fine", ["rule-a"], "Standard", policy), audit);
        List<PolicyDecisionEntity>? seenDuringExecution = null;

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) =>
        {
            seenDuringExecution = Decisions();
            return Task.FromResult(1);
        });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        var row = Assert.Single(seenDuringExecution!);
        Assert.Equal(outcome.DecisionId, row.Id);
        Assert.Equal("op-1", row.OperationId);
        Assert.Equal("user:ops@example.com", row.PrincipalId);
        Assert.Equal("Human", row.PrincipalKind);
        Assert.Equal("Ui", row.Surface);
        Assert.Equal("container.start", row.CapabilityId);
        Assert.Equal(McpPermissionLevels.Write, row.RequiredLevel);
        Assert.Equal(("container", "c1", "web", "srv1"), (row.TargetKind, row.TargetId, row.TargetName, row.TargetServerId));
        Assert.Equal("""["db"]""", row.TargetRelated);
        Assert.Equal(PolicyCanonicalJson.InputHash(Request().Arguments), row.InputHash);
        Assert.Equal(PolicySnapshot.Of("Standard", policy).VersionHash, row.PolicyVersion);
        Assert.Equal("Standard", row.Preset);
        Assert.Equal("""["rule-a"]""", row.MatchedRules);
        Assert.Equal("Allow", row.Verdict);
        Assert.Equal("fine", row.Reason);
        Assert.True(row.Timestamp > DateTime.UtcNow.AddMinutes(-1));
        // The policy version it names is stored too, in the canonical form it was hashed in.
        var snapshot = Assert.Single(Snapshots());
        Assert.Equal(row.PolicyVersion, snapshot.VersionHash);
        Assert.Equal(PolicyCanonicalJson.Hash(snapshot.PolicyJson), snapshot.VersionHash);
        // The audit intent and outcome name the decision.
        Assert.Equal([row.Id, row.Id], audit.DecisionIds);
    }

    [Fact]
    public async Task The_record_holds_the_resolved_target_not_the_one_the_caller_named()
    {
        // The caller names a prefix; the resolver finds the canonical container, its server and what it touches.
        var named = new MutationTarget("container", "ab", "ab", null);
        var resolved = new MutationTarget("container", "abcdef0123456789", "web-1", "srv-9")
        {
            Related = ["db-1", "cache-1"],
            Resolved = true,
        };
        var gateway = Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", []), new RecordingAudit(),
            targets: new FixedResolver(resolved));
        List<PolicyDecisionEntity>? seen = null;

        var outcome = await gateway.ExecuteAsync(Request() with { Target = named }, (_, _) =>
        {
            seen = Decisions();
            return Task.FromResult(0);
        });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        var row = Assert.Single(seen!);
        Assert.Equal(("abcdef0123456789", "web-1", "srv-9"), (row.TargetId, row.TargetName, row.TargetServerId));
        Assert.Equal("""["db-1","cache-1"]""", row.TargetRelated);
    }

    [Fact]
    public async Task A_withheld_related_value_is_decided_but_not_stored()
    {
        var resolved = new MutationTarget("host", "local", "Local", "local")
        {
            Related = ["web-1", "/srv/canary-secret"],
            Withheld = new HashSet<string> { "/srv/canary-secret" },
            Resolved = true,
        };
        var gateway = Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", []), new RecordingAudit(),
            targets: new FixedResolver(resolved));

        await gateway.ExecuteAsync(Request() with { Target = new MutationTarget("host", "local") }, (_, _) => Task.FromResult(0));

        var row = Assert.Single(Decisions());
        Assert.Equal("""["web-1","(withheld)"]""", row.TargetRelated);
    }

    [Fact]
    public async Task Credentials_in_a_resolved_target_are_never_stored()
    {
        var resolved = new MutationTarget("gitapp", "app-1", "https://deploy:hunter2@git.example.com/shop.git", "srv1")
        {
            Related = ["shop", "https://deploy:hunter2@git.example.com/shop.git", "https://ghp_tok3n0nly@github.com/acme/shop"],
            Resolved = true,
        };
        var gateway = Gateway(new FixedPolicy(MutationVerdict.Deny, "no", []), new RecordingAudit(),
            targets: new FixedResolver(resolved));

        await gateway.ExecuteAsync(Request() with { Target = new MutationTarget("gitapp", "app-1") }, (_, _) => Task.FromResult(0));

        var row = Assert.Single(Decisions());
        var stored = row.TargetId + row.TargetName + row.TargetRelated;
        Assert.DoesNotContain("hunter2", stored);
        Assert.DoesNotContain("ghp_tok3n0nly", stored);
        Assert.Contains("git.example.com/shop.git", row.TargetRelated);   // the repository itself stays readable
    }

    [Fact]
    public async Task A_rule_quoting_a_resolved_repository_url_never_stores_its_token()
    {
        // The production policy: a protected pattern matches the resolved repository URL and its reason quotes it.
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources.Add("*github.com*");
        var guardrails = new PresetStore("Day", new GuardrailPreset { Name = "Day", Policy = policy });
        var resolved = new MutationTarget("container", "c1", "web", "srv1")
        {
            Related = ["https://ghp_example123@github.com/acme/shop"],
            Resolved = true,
        };
        var gateway = Gateway(AgentToolTestHelpers.Policy(guardrails), new RecordingAudit(), targets: new FixedResolver(resolved));

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) => Task.FromResult(0));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        var row = Assert.Single(Decisions());
        Assert.Contains("github.com", row.Reason);          // the rule did quote it
        Assert.DoesNotContain("ghp_example123", row.Reason + row.TargetRelated);
    }

    [Theory]
    [InlineData("db-prod")]
    [InlineData("db-primary")]
    [InlineData("api -p 8080")]
    public async Task Ordinary_target_identities_are_stored_exactly(string identity)
    {
        var resolved = new MutationTarget("volume", identity, identity, "srv1") { Related = [identity], Resolved = true };
        var gateway = Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", []), new RecordingAudit(),
            targets: new FixedResolver(resolved));

        await gateway.ExecuteAsync(Request(), (_, _) => Task.FromResult(0));

        var row = Assert.Single(Decisions());
        Assert.Equal((identity, identity), (row.TargetId, row.TargetName));
        Assert.Equal(JsonSerializer.Serialize(new[] { identity }), row.TargetRelated);
    }

    [Fact]
    public async Task A_decision_that_cannot_be_recorded_is_refused_with_a_localized_code()
    {
        var outcome = await Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", []), new RecordingAudit(), new FailingDecisions())
            .ExecuteAsync(Request(), (_, _) => Task.FromResult(0));

        Assert.Equal(MutationReasonCodes.DecisionNotRecorded, outcome.ReasonCode);
    }

    [Fact]
    public void The_input_hash_is_normalized()
    {
        var a = PolicyCanonicalJson.InputHash(Args("""{"b":{"y":2,"x":[1,{"q":1,"p":2}]},"a":"v"}"""));
        var b = PolicyCanonicalJson.InputHash(Args("""{ "a" : "v", "b" : { "x" : [ 1, { "p":2, "q":1 } ], "y" : 2 } }"""));
        var c = PolicyCanonicalJson.InputHash(Args("""{"a":"v","b":{"x":[{"p":2,"q":1},1],"y":2}}"""));

        Assert.Equal(a, b);          // key order and whitespace do not matter
        Assert.NotEqual(a, c);       // array order does
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public async Task A_refusal_is_recorded_and_names_its_decision()
    {
        var audit = new RecordingAudit();
        var gateway = Gateway(new FixedPolicy(MutationVerdict.Deny, "protected", ["protected-resource"]), audit);
        var ran = false;

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.False(ran);
        var row = Assert.Single(Decisions());
        Assert.Equal(("Deny", outcome.DecisionId), (row.Verdict, (string?)row.Id));
        Assert.Equal([row.Id], audit.DecisionIds);
    }

    [Fact]
    public async Task No_record_no_mutation()
    {
        var audit = new RecordingAudit();
        var gateway = Gateway(new FixedPolicy(MutationVerdict.Allow, "fine", []), audit, new FailingDecisions());
        var ran = false;

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("could not be recorded", outcome.Reason);
        Assert.False(ran);
        Assert.Empty(audit.Begun);
        Assert.Null(outcome.DecisionId);
    }

    [Fact]
    public async Task An_approval_names_the_decision_and_preset_that_asked_for_it()
    {
        var approvals = new ApprovalStore();
        var gateway = Gateway(new FixedPolicy(MutationVerdict.Confirm, "writes need approval", ["confirm-writes"], "Night"),
            new RecordingAudit(), approvals: new ApprovalGate(approvals, new PresetStore("Day")));

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) => Task.FromResult(0));

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        var approval = Assert.Single(approvals.GetAll());
        Assert.Equal(outcome.DecisionId, approval.DecisionId);
        Assert.Equal("Night", approval.GuardrailPreset);   // the decision's preset, not the active one
        Assert.Equal(Assert.Single(Decisions()).Id, approval.DecisionId);
    }

    [Fact]
    public async Task A_policy_change_is_a_new_version_and_leaves_the_stored_one_unchanged()
    {
        var store = new DbPolicyDecisionStore(_sp.GetRequiredService<IServiceScopeFactory>());
        var policy = GuardrailPolicy.SafeDefault();
        await Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", [], "Standard", policy), new RecordingAudit(), store)
            .ExecuteAsync(Request(), (_, _) => Task.FromResult(0));
        var first = Assert.Single(Snapshots());

        policy.MaxActionsPerSession = 5;   // the operator edits the preset
        await Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", [], "Standard", policy), new RecordingAudit(), store)
            .ExecuteAsync(Request(), (_, _) => Task.FromResult(0));
        // The same version again adds no row.
        await Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", [], "Standard", policy), new RecordingAudit(), store)
            .ExecuteAsync(Request(), (_, _) => Task.FromResult(0));

        var snapshots = Snapshots();
        Assert.Equal(2, snapshots.Count);
        var kept = Assert.Single(snapshots, s => s.VersionHash == first.VersionHash);
        Assert.Equal(first.PolicyJson, kept.PolicyJson);
        Assert.Equal(first.CreatedAt, kept.CreatedAt);
        var decisions = Decisions().OrderBy(d => d.Timestamp).ToList();
        Assert.Equal(3, decisions.Count);
        Assert.Equal(first.VersionHash, decisions[0].PolicyVersion);
        Assert.NotEqual(first.VersionHash, decisions[1].PolicyVersion);
        Assert.Equal(decisions[1].PolicyVersion, decisions[2].PolicyVersion);
    }

    [Fact]
    public void A_snapshot_does_not_follow_later_edits_of_the_policy()
    {
        var policy = GuardrailPolicy.SafeDefault();
        var snapshot = PolicySnapshot.Of("Standard", policy);
        var pinned = PinnedPolicy.Of("Standard", policy);
        var json = snapshot.Json;

        policy.ToolDenyList.Add("stop_container");
        policy.ReadOnlyMode = true;

        Assert.Equal(json, snapshot.Json);
        Assert.Empty(pinned.Policy.ToolDenyList);
        Assert.False(pinned.Policy.ReadOnlyMode);
    }

    [Fact]
    public void A_missing_active_preset_never_selects_the_first_preset()
    {
        var config = new GuardrailConfig
        {
            ActivePreset = "Gone",
            Presets = { new GuardrailPreset { Name = "Open", Policy = new GuardrailPolicy { MaxAutonomousLevel = "admin" } } },
        };

        var policy = config.ActivePolicy();

        Assert.True(policy.ReadOnlyMode);
        Assert.Equal("read", policy.MaxAutonomousLevel);
        Assert.Contains("*", policy.ProtectedResources);
    }

    [Fact]
    public async Task The_production_policy_reports_the_active_preset_and_its_version()
    {
        var store = new PresetStore("Day", new GuardrailPreset { Name = "Day", Policy = GuardrailPolicy.SafeDefault() });
        var policy = AgentToolTestHelpers.Policy(store);

        var decision = policy.Evaluate(Request(), new CapabilityCatalog().Find("container.start")!);

        Assert.Equal("Day", decision.Policy!.Preset);
        Assert.Equal(PolicySnapshot.Of("Day", GuardrailPolicy.SafeDefault()).VersionHash, decision.Policy.VersionHash);
    }

    [Fact]
    public async Task A_version_that_cannot_be_stored_is_a_failure_not_an_orphan_decision()
    {
        using (var scope = _sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER no_snapshots BEFORE INSERT ON PolicySnapshots BEGIN SELECT RAISE(ABORT, 'read-only'); END;");
        var store = new DbPolicyDecisionStore(_sp.GetRequiredService<IServiceScopeFactory>());
        var ran = false;

        var outcome = await Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", []), new RecordingAudit(), store)
            .ExecuteAsync(Request(), (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.False(ran);
        Assert.Empty(Decisions());
    }

    [Fact]
    public async Task Concurrent_decisions_under_a_new_version_store_it_once()
    {
        var store = new DbPolicyDecisionStore(_sp.GetRequiredService<IServiceScopeFactory>());
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxActionsPerSession = 7;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", [], "Standard", policy), new RecordingAudit(), store)
                .ExecuteAsync(Request(), (_, _) => Task.FromResult(0)))));

        Assert.All(outcomes, o => Assert.Equal(MutationStatus.Executed, o.Status));
        Assert.Single(Snapshots());
        Assert.Equal(12, Decisions().Count);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("escaped")]
    public async Task The_audit_outcome_keeps_the_decision_id_and_stays_valid_json_with_a_long_reason(string kind)
    {
        var audit = new AuditLogMutationSink(_sp.GetRequiredService<IServiceScopeFactory>());
        var gateway = Gateway(new FixedPolicy(MutationVerdict.Allow, "ok", []), audit);

        // Varied text the redactor does not collapse; "escaped" serializes to several characters per character.
        var reason = string.Concat(Enumerable.Range(0, 3000).Select(i => kind == "escaped"
            ? (i % 3 == 0 ? "\u0001" : i % 3 == 1 ? "<" : "&")
            : (i % 7).ToString() + " "));
        var outcome = await gateway.ExecuteAsync<int>(Request(), (_, _) => throw new InvalidOperationException(reason));

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        using var scope = _sp.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().Single();
        using var json = JsonDocument.Parse(row.Details!);   // valid JSON
        Assert.Equal(outcome.DecisionId, json.RootElement.GetProperty("decisionId").GetString());
        Assert.Equal(outcome.DecisionId, json.RootElement.GetProperty("intent").GetProperty("decisionId").GetString());
        Assert.True(row.Details!.Length <= 4000);
    }

    [Fact]
    public async Task An_agent_approval_names_the_decision_recorded_before_the_session_paused()
    {
        var store = new DbPolicyDecisionStore(_sp.GetRequiredService<IServiceScopeFactory>());
        var guardrails = new PresetStore("Day", new GuardrailPreset { Name = "Day", Policy = GuardrailPolicy.SafeDefault() });
        var approvals = new ApprovalStore();
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(guardrails),
            new RecordingAudit(), [], NullLogger<MutationGateway>.Instance,
            approvals: new ApprovalGate(approvals, guardrails), decisions: store);
        var (session, context) = Session(gateway, guardrails, GuardrailPolicy.SafeDefault());
        var coordinator = new Whiskers.Services.Agent.Approvals.ApprovalCoordinator(approvals, new NullNotify(), guardrails);

        Approval? raised = null;
        await foreach (var ev in session.SendAsync("start web"))
            if (ev is AgentEvent.ConfirmationRequired c)
            {
                raised = await coordinator.RaiseAsync(session, c.Call, context, c.Reason, c.Decision);
                await session.ResolveConfirmationAsync(c.Call.CorrelationId, false);   // the human rejects: nothing runs
            }

        var decision = Assert.Single(Decisions());
        Assert.Equal("Confirm", decision.Verdict);
        Assert.Equal(decision.Id, raised!.DecisionId);
        Assert.Equal("Day", raised.GuardrailPreset);
    }

    [Fact]
    public async Task The_session_pauses_or_blocks_only_on_what_the_gateway_recorded()
    {
        // The session's own evaluation asks for a confirmation, but by the time the gateway decides the policy allows
        // the call (an admin relaxed it meanwhile): the recorded Allow governs — no approval naming an Allow record.
        var store = new DbPolicyDecisionStore(_sp.GetRequiredService<IServiceScopeFactory>());
        var guardrails = new PresetStore("Day", new GuardrailPreset { Name = "Day", Policy = GuardrailPolicy.SafeDefault() });
        var gateway = new MutationGateway(new CapabilityCatalog(),
            new FixedPolicy(MutationVerdict.Allow, "relaxed meanwhile", [], "Day"), new RecordingAudit(), [],
            NullLogger<MutationGateway>.Instance, decisions: store);
        var invoker = new Whiskers.Services.Agent.AgentToolInvoker(AgentToolTestHelpers.DefaultRegistry(),
            GuardrailEngine.CreateDefault(), _sp.GetRequiredService<IServiceScopeFactory>(), gateway: gateway);
        var context = new AgentContext("s1",
            new AgentPrincipal(AgentPrincipalKind.WebUser, "ops", McpPermissionLevels.Write, null, UserEmail: "ops@example.com"),
            AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());
        var session = new Whiskers.Services.Agent.AgentSession(context, new OneCall("start_container", "{\"containerId\":\"web\",\"serverId\":\"srv1\"}"),
            new NoTools(), invoker, new AlwaysConfirm(), AgentToolTestHelpers.DefaultRegistry(),
            new AgentSettings { Model = "m", MaxToolIterations = 3 }, "system", guardrailStore: guardrails);

        GuardrailDecision? proposed = null;
        var paused = false;
        await foreach (var ev in session.SendAsync("start web"))
        {
            if (ev is AgentEvent.ToolProposed p) proposed ??= p.Decision;
            if (ev is AgentEvent.ConfirmationRequired) paused = true;
        }

        Assert.False(paused);
        Assert.Equal(GuardrailVerdict.Allow, proposed!.Verdict);
        var first = Decisions().OrderBy(d => d.Timestamp).First();
        Assert.Equal(("Allow", first.Id), (first.Verdict, proposed.DecisionId));
    }

    [Fact]
    public async Task A_block_in_an_agent_session_is_on_record()
    {
        var store = new DbPolicyDecisionStore(_sp.GetRequiredService<IServiceScopeFactory>());
        var killed = GuardrailPolicy.SafeDefault();
        killed.ReadOnlyMode = true;
        var guardrails = new PresetStore("Day", new GuardrailPreset { Name = "Day", Policy = killed });
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(guardrails),
            new RecordingAudit(), [], NullLogger<MutationGateway>.Instance, decisions: store);
        var (session, _) = Session(gateway, guardrails, killed);

        string? decisionId = null;
        await foreach (var ev in session.SendAsync("start web"))
            if (ev is AgentEvent.ToolProposed p) decisionId = p.Decision.DecisionId;

        var decision = Assert.Single(Decisions());
        Assert.Equal(("Deny", decision.Id), (decision.Verdict, decisionId));
    }

    private (Whiskers.Services.Agent.AgentSession, AgentContext) Session(MutationGateway gateway, IGuardrailStore guardrails,
        GuardrailPolicy policy)
    {
        var invoker = new Whiskers.Services.Agent.AgentToolInvoker(AgentToolTestHelpers.DefaultRegistry(),
            GuardrailEngine.CreateDefault(), _sp.GetRequiredService<IServiceScopeFactory>(), gateway: gateway);
        var context = new AgentContext("s1",
            new AgentPrincipal(AgentPrincipalKind.WebUser, "ops", McpPermissionLevels.Write, null, UserEmail: "ops@example.com"),
            AgentOrigin.WebUi, policy);
        var session = new Whiskers.Services.Agent.AgentSession(context, new OneCall("start_container", "{\"containerId\":\"web\",\"serverId\":\"srv1\"}"),
            new NoTools(), invoker, GuardrailEngine.CreateDefault(), AgentToolTestHelpers.DefaultRegistry(),
            new AgentSettings { Model = "m", MaxToolIterations = 3 }, "system", guardrailStore: guardrails);
        return (session, context);
    }

    // ---- Fakes -------------------------------------------------------------

    private sealed class OneCall(string tool, string args) : Whiskers.Services.Agent.Providers.IAgentLlmProvider
    {
        private bool _done;
        public string Id => "one";

        public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(AgentCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!_done)
            {
                _done = true;
                yield return new AgentStreamDelta(ToolCallDelta: new AgentToolCall("c1", tool, args));
                yield return new AgentStreamDelta(Final: AgentStopReason.ToolCalls);
            }
            else yield return new AgentStreamDelta(Final: AgentStopReason.Stop);
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class AlwaysConfirm : IAgentGuardrailEngine
    {
        public GuardrailDecision Evaluate(GuardrailRequest request) =>
            new(GuardrailVerdict.Confirm, "confirm", Array.Empty<string>());
    }

    private sealed class NoTools : Whiskers.Services.Agent.IAgentToolCatalog
    {
        public IReadOnlyList<AgentToolDefinition> GetVisibleTools(AgentContext context) => Array.Empty<AgentToolDefinition>();
    }

    private sealed class NullNotify : Whiskers.Services.Notifications.INotificationService
    {
        public Task SendAsync(NotificationEvent evt) => Task.CompletedTask;
        public Task SendTestAsync() => Task.CompletedTask;
    }

    // ---- Fakes -------------------------------------------------------------

    private sealed class FixedPolicy(MutationVerdict verdict, string reason, IReadOnlyList<string> rules,
        string preset = "Standard", GuardrailPolicy? policy = null) : IMutationPolicy
    {
        public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability) =>
            new(verdict, reason, rules) { Policy = PolicySnapshot.Of(preset, policy ?? GuardrailPolicy.SafeDefault()) };
    }

    private sealed class FixedResolver(MutationTarget target) : IMutationTargetResolver
    {
        public Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct) =>
            Task.FromResult(new TargetResolution(target, null));
    }

    private sealed class FailingDecisions : IPolicyDecisionStore
    {
        public Task RecordAsync(PolicyDecisionRecord record, CancellationToken ct) =>
            throw new IOException("database unavailable");
    }

    internal sealed class RecordingAudit : IMutationAuditSink
    {
        public List<string?> DecisionIds { get; } = [];
        public List<string> Begun { get; } = [];
        private readonly Dictionary<long, string?> _intents = [];

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            Begun.Add(capability.Id);
            DecisionIds.Add(request.DecisionId);
            var id = (long)Begun.Count;
            _intents[id] = request.DecisionId;
            return Task.FromResult(id);
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct)
        {
            DecisionIds.Add(_intents[auditId]);
            return Task.CompletedTask;
        }

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            DecisionIds.Add(request.DecisionId);
            return Task.CompletedTask;
        }
    }

    /// <summary>A guardrail store with named presets, one of them active.</summary>
    internal sealed class PresetStore(string active, params GuardrailPreset[] presets) : IGuardrailStore
    {
        public string ActiveName { get; set; } = active;
        public List<GuardrailPreset> Presets { get; } = [.. presets];
        public string? Error { get; set; }

        public GuardrailPolicy Current => Error is not null
            ? GuardrailPolicy.FailClosed()
            : Config.ActivePolicy().Clone();
        public GuardrailConfig Config => new GuardrailConfig { ActivePreset = ActiveName, Presets = Presets }.Clone();
        public string? LoadError => Error;
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }
}
