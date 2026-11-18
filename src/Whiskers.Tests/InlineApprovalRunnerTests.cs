using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0019 (INV-65, INV-33, INV-30/32/35/39): the UI runner shows an INV-59 approval raised by its own call in the
/// dialog, and continues the same operation once — through the real gateway, the real approval gate and the real
/// <see cref="ApprovalStore"/> on its in-memory persistence; only the dialog (<see cref="ScriptedPrompt"/>) and the
/// work behind the call are doubles. The scripted dialog decides the way the real one does: through
/// <see cref="IApprovalStore.DecideAsync(string, bool, ApprovalResolver, ApprovalChannel)"/> as the person signed in,
/// via <see cref="ApprovalChannel.Dialog"/>.
/// </summary>
public class InlineApprovalRunnerTests
{
    internal const string AdminEmail = "admin@example.com";

    /// <summary>A host that already holds mTLS state: re-onboarding it is an INV-59 change.</summary>
    internal static ServerConfig Edge() => new()
    {
        Id = "edge", Name = "Edge", ConnectionType = ConnectionType.TCP,
        SshHost = "203.0.113.30", SshUser = "root", TcpHost = "203.0.113.30", TcpPort = 2376, TcpUseTls = true,
    };

    internal static object OnboardArgs => new { serverId = "edge", name = "Edge", bootstrap = "none", abandon = false };

    /// <summary>The person signed in; role, credential and session can change between two calls, as they can live.</summary>
    internal sealed class Person : ICurrentUserService
    {
        public string Email { get; set; } = AdminEmail;
        public AppRole Role { get; set; } = AppRole.Admin;
        public string? Credential { get; set; } = "signin-1";
        public bool Revoked { get; set; }
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Email);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Revoked ? AppRole.Viewer : Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(!Revoked && Role >= required);
        public Task<SessionCheck> CheckSessionAsync() =>
            Task.FromResult(Revoked ? SessionCheck.Revoked(SessionRevocationReasons.AccountDeleted) : SessionCheck.Allowed(Role));
        public Task<(string? CredentialId, string? SessionId)> GetPrincipalFieldsAsync() =>
            Task.FromResult<(string?, string?)>((Credential, "web-session-1"));
    }

    /// <summary>The dialog as a script: each call runs the next step, which may decide through the store as the dialog
    /// does, and answers with an outcome.</summary>
    internal sealed class ScriptedPrompt : IInlineApprovalPrompt
    {
        private readonly Queue<Func<InlineApprovalRequest, CancellationToken, Task<InlineApprovalResult>>> _steps = new();
        public List<InlineApprovalRequest> Asked { get; } = [];

        public ScriptedPrompt Then(Func<InlineApprovalRequest, CancellationToken, Task<InlineApprovalResult>> step)
        {
            _steps.Enqueue(step);
            return this;
        }

        public Task<InlineApprovalResult> AskAsync(InlineApprovalRequest request, CancellationToken ct)
        {
            Asked.Add(request);
            return _steps.Count == 0
                ? Task.FromResult(new InlineApprovalResult(InlineApprovalOutcome.LeftPending))
                : _steps.Dequeue()(request, ct);
        }
    }

    /// <summary>The real gateway, gate and store around one person.</summary>
    internal sealed class Harness
    {
        public GuardrailPolicy Policy { get; } = GuardrailPolicy.SafeDefault();
        public AgentToolTestHelpers.FixedGuardrailStore Guardrails { get; }
        public ApprovalStore Approvals { get; }
        public AgentToolTestHelpers.NullMutationAudit Audit { get; } = new();
        public MutationGateway Gateway { get; }
        public Person User { get; } = new();
        public ScriptedPrompt Prompt { get; } = new();
        public ServerConfig Server { get; } = Edge();
        public List<string> Runs { get; } = [];

        public Harness(bool secondApprover = false)
        {
            Policy.RequireSecondApprover = secondApprover;
            Guardrails = new AgentToolTestHelpers.FixedGuardrailStore(Policy);
            Approvals = new ApprovalStore(policy: Guardrails);
            Gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(Guardrails), Audit, [],
                NullLogger<MutationGateway>.Instance,
                targets: TestTargets.Resolver(new MutationTargetResolver(new TestServices(new FakeServerConfig(Server)))),
                approvals: new ApprovalGate(Approvals, Guardrails));
        }

        public UiMutationRunner Runner(bool withPrompt = true, ICurrentUserService? user = null) =>
            new(Gateway, new CapabilityCatalog(), user ?? User, prompt: withPrompt ? Prompt : null,
                approvals: withPrompt ? Approvals : null);

        public Task<string> OnboardAsync(UiMutationRunner? runner = null, CancellationToken ct = default) =>
            (runner ?? Runner()).RunAsync("server.onboard", OnboardArgs,
                (ResolvedTarget _, CancellationToken _) => { lock (Runs) Runs.Add("onboard"); return Task.FromResult("onboarded"); }, ct);

        public ApprovalResolver Admin(string email = AdminEmail) => ApprovalResolver.User(email, AppRole.Admin);

        /// <summary>What the dialog's "Approve and run" does: records the decision via the dialog, as the person.</summary>
        public Func<InlineApprovalRequest, CancellationToken, Task<InlineApprovalResult>> ApproveHere(string email = AdminEmail) =>
            async (r, _) =>
            {
                var decided = await Approvals.DecideAsync(r.ApprovalId, true, Admin(email), ApprovalChannel.Dialog);
                return decided == ApprovalDecisionResult.Decided
                    ? new InlineApprovalResult(InlineApprovalOutcome.ApprovedHere, decided)
                    : new InlineApprovalResult(InlineApprovalOutcome.LeftPending, decided);
            };

        public IEnumerable<string> Begins => Audit.Events.Where(e => e.StartsWith("begin:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pending_under_INV59_shows_the_approval_raised_for_this_call()
    {
        var h = new Harness();

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());

        var asked = Assert.Single(h.Prompt.Asked);
        var approval = h.Approvals.Get(asked.ApprovalId)!;
        Assert.Equal("server.onboard", asked.CapabilityId);
        Assert.Equal(approval.CorrelationId, asked.OperationId);
        Assert.Equal("web", approval.ActorType);
        Assert.Equal(PrincipalIds.User(AdminEmail), approval.Requesters[0]);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Equal(asked.ApprovalId, refused.ApprovalId);
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task Approve_in_dialog_continues_the_same_operation_once()
    {
        var h = new Harness();
        h.Prompt.Then(h.ApproveHere());

        Assert.Equal("onboarded", await h.OnboardAsync());

        var asked = Assert.Single(h.Prompt.Asked);
        var approval = h.Approvals.Get(asked.ApprovalId)!;
        Assert.Equal(["onboard"], h.Runs);
        // INV-39: the continuation used the operation that raised the approval; INV-35: once.
        Assert.Equal(asked.OperationId, approval.ConsumedBy);
        Assert.Equal(ApprovalChannels.Dialog, approval.DecidedVia);
        Assert.True(approval.SelfApproved);
        Assert.Equal([$"begin:server.onboard:{approval.Id}"], h.Begins);
        Assert.Contains("denied:PendingApproval", h.Audit.Events);
        Assert.Contains("complete:Executed", h.Audit.Events);
    }

    [Fact]
    public async Task Closing_the_dialog_leaves_the_approval_pending_and_runs_nothing()
    {
        var h = new Harness();
        h.Prompt.Then((_, _) => Task.FromResult(new InlineApprovalResult(InlineApprovalOutcome.LeftPending)));

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());

        Assert.Equal(MutationStatus.PendingApproval, refused.Status);
        Assert.Equal(ApprovalStatus.Pending, h.Approvals.Get(refused.ApprovalId!)!.Status);
        Assert.Contains(refused.ApprovalId!, refused.Message);
        Assert.Empty(h.Runs);
        Assert.Empty(h.Begins);
    }

    [Fact]
    public async Task Reject_in_dialog_records_rejected_and_runs_nothing()
    {
        var h = new Harness();
        h.Prompt.Then(async (r, _) =>
        {
            await h.Approvals.DecideAsync(r.ApprovalId, false, h.Admin(), ApprovalChannel.Dialog);
            return new InlineApprovalResult(InlineApprovalOutcome.Rejected);
        });

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());

        var approval = h.Approvals.Get(refused.ApprovalId!)!;
        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Equal(ApprovalStatus.Rejected, approval.Status);
        Assert.Equal(ApprovalChannels.Dialog, approval.DecidedVia);
        Assert.Contains(AdminEmail, refused.Message);
        Assert.Empty(h.Runs);
        Assert.Empty(h.Begins);
    }

    [Fact]
    public async Task An_approval_of_another_operation_or_requester_is_never_prompted()
    {
        // Another person raised it: their pending approval is not shown to this one, even for the same call.
        var h = new Harness();
        var other = new Person { Email = "other@example.com" };
        await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync(h.Runner(withPrompt: false, other)));
        Assert.Single(h.Approvals.GetPending());

        // This person's call raises its own approval; theirs is shown, never the other one.
        await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());
        var asked = Assert.Single(h.Prompt.Asked);
        Assert.Equal(PrincipalIds.User(AdminEmail), h.Approvals.Get(asked.ApprovalId)!.Requesters[0]);

        // An approval of another operation, handed to the runner as if it were this call's: never prompted.
        var foreign = h.Approvals.GetPending().Single(a => a.Requesters[0] == PrincipalIds.User("other@example.com"));
        var stub = new StubGateway(new MutationOutcome<string>("op-not-the-same", MutationStatus.PendingApproval, null,
            "pending", VerificationStatus.NotRun, ApprovalId: foreign.Id) { MatchedRules = [ProtectedResourceRule.RuleId] });
        var runner = new UiMutationRunner(stub, new CapabilityCatalog(), h.User, prompt: h.Prompt, approvals: h.Approvals);
        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => runner.RunAsync("server.onboard", OnboardArgs,
            (ResolvedTarget _, CancellationToken _) => Task.FromResult("x")));

        Assert.Equal(foreign.Id, refused.ApprovalId);
        Assert.Single(h.Prompt.Asked);
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task A_capability_outside_the_rollout_list_keeps_todays_refusal_with_the_approval_id()
    {
        var h = new Harness();

        // Wave 3 (D7): selfbackup.restore is an INV-59 capability, approved on the Approvals page only for now.
        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.Runner().RunAsync("selfbackup.restore",
            new { name = "backup-1" }, (ResolvedTarget _, CancellationToken _) => { h.Runs.Add("restore"); return Task.CompletedTask; }));

        Assert.Equal(MutationStatus.PendingApproval, refused.Status);
        Assert.NotNull(refused.ApprovalId);
        Assert.Equal(ApprovalStatus.Pending, h.Approvals.Get(refused.ApprovalId!)!.Status);
        Assert.Empty(h.Prompt.Asked);
        Assert.Empty(h.Runs);
        Assert.False(InlineApproval.Capabilities.Contains("selfbackup.restore"));
    }

    [Fact]
    public async Task Without_a_prompt_the_refusal_is_unchanged()
    {
        var h = new Harness();

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync(h.Runner(withPrompt: false)));

        Assert.Equal(MutationStatus.PendingApproval, refused.Status);
        Assert.Contains(ProtectedResourceRule.RuleId, refused.MatchedRules);
        Assert.Equal(h.Approvals.GetPending().Single().Id, refused.ApprovalId);
        Assert.Empty(h.Prompt.Asked);
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task Second_approver_policy_disables_approve_in_the_dialog()
    {
        var h = new Harness(secondApprover: true);
        ApprovalDecisionResult? canDecide = null;
        ApprovalDecisionResult? decided = null;
        h.Prompt.Then(async (r, _) =>
        {
            canDecide = h.Approvals.CanDecide(r.ApprovalId, h.Admin());
            decided = await h.Approvals.DecideAsync(r.ApprovalId, true, h.Admin(), ApprovalChannel.Dialog);
            return new InlineApprovalResult(InlineApprovalOutcome.LeftPending, decided);
        });

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());

        Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, canDecide);
        Assert.Equal(ApprovalDecisionResult.RequesterCannotDecide, decided);
        Assert.Equal(ApprovalStatus.Pending, h.Approvals.Get(refused.ApprovalId!)!.Status);
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task Approved_elsewhere_runs_only_after_Run_now()
    {
        var h = new Harness(secondApprover: true);
        // A second Admin approves on the page while the dialog is open; nothing runs until "Run now" is clicked.
        var runsBeforeRunNow = -1;
        h.Prompt.Then(async (r, _) =>
        {
            Assert.Equal(ApprovalDecisionResult.Decided,
                await h.Approvals.DecideAsync(r.ApprovalId, true, h.Admin("second@example.com"), ApprovalChannel.Page));
            runsBeforeRunNow = h.Runs.Count;
            return new InlineApprovalResult(InlineApprovalOutcome.ApprovedElsewhereRun);
        });

        Assert.Equal("onboarded", await h.OnboardAsync());

        var approval = h.Approvals.Get(h.Prompt.Asked.Single().ApprovalId)!;
        Assert.Equal(0, runsBeforeRunNow);
        Assert.Equal(["onboard"], h.Runs);
        Assert.Equal(ApprovalChannels.Page, approval.DecidedVia);
        Assert.False(approval.SelfApproved);
        Assert.NotNull(approval.ConsumedAt);
    }

    public static TheoryData<string> Drifts => ["policy saved", "host connection changed", "credential changed"];

    [Theory]
    [MemberData(nameof(Drifts))]
    public async Task Drift_after_approval_never_uses_the_old_grant(string drift)
    {
        var h = new Harness();
        h.Prompt.Then(async (r, ct) =>
        {
            var approved = await h.ApproveHere()(r, ct);
            switch (drift)
            {
                case "policy saved": h.Policy.ProtectedResources = [.. h.Policy.ProtectedResources, "billing*"]; break;
                case "host connection changed": h.Server.TcpHost = "203.0.113.31"; break;
                default: h.User.Credential = "signin-2"; break;
            }
            return approved;
        });   // the second prompt ("changed since you approved") is closed

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());

        Assert.Equal(2, h.Prompt.Asked.Count);
        var (first, second) = (h.Prompt.Asked[0], h.Prompt.Asked[1]);
        Assert.NotEqual(first.ApprovalId, second.ApprovalId);
        Assert.True(second.ChangedSinceApproval);
        var old = h.Approvals.Get(first.ApprovalId)!;
        Assert.Equal(ApprovalStatus.Approved, old.Status);
        Assert.Null(old.ConsumedAt);             // INV-30: the old grant is never used for the changed call
        Assert.Equal(second.ApprovalId, refused.ApprovalId);
        Assert.Equal(MutationStatus.PendingApproval, refused.Status);
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task A_second_drift_is_refused_without_a_third_prompt()
    {
        var h = new Harness();
        h.Prompt
            .Then(async (r, ct) => { var a = await h.ApproveHere()(r, ct); h.User.Credential = "signin-2"; return a; })
            .Then(async (r, ct) => { var a = await h.ApproveHere()(r, ct); h.User.Credential = "signin-3"; return a; });

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());

        Assert.Equal(2, h.Prompt.Asked.Count);
        Assert.Equal(MutationStatus.PendingApproval, refused.Status);
        Assert.DoesNotContain(refused.ApprovalId, h.Prompt.Asked.Select(a => a.ApprovalId));
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task A_revoked_session_between_approval_and_continuation_runs_nothing()
    {
        var h = new Harness();
        h.Prompt.Then(async (r, ct) => { var a = await h.ApproveHere()(r, ct); h.User.Revoked = true; return a; });

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());

        Assert.Equal(MutationStatus.Denied, refused.Status);
        var approval = h.Approvals.Get(h.Prompt.Asked.Single().ApprovalId)!;
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Null(approval.ConsumedAt);
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task Cancellation_runs_nothing_and_a_later_page_approval_runs_nothing()
    {
        var h = new Harness();
        using var circuit = new CancellationTokenSource();
        h.Prompt.Then((_, _) =>
        {
            circuit.Cancel();   // the page or the circuit went away while the dialog was open
            return Task.FromResult(new InlineApprovalResult(InlineApprovalOutcome.LeftPending));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.OnboardAsync(ct: circuit.Token));
        var approval = h.Approvals.Get(h.Prompt.Asked.Single().ApprovalId)!;
        Assert.Equal(ApprovalStatus.Pending, approval.Status);

        // Approved on the page afterwards: nothing runs by itself.
        Assert.Equal(ApprovalDecisionResult.Decided, await h.Approvals.DecideAsync(approval.Id, true, h.Admin(), ApprovalChannel.Page));
        await Task.Yield();
        Assert.Empty(h.Runs);
        Assert.Null(approval.ConsumedAt);
    }

    [Fact]
    public async Task Decided_then_circuit_lost_runs_nothing_and_a_repeat_click_uses_the_grant_once()
    {
        var h = new Harness();
        using var circuit = new CancellationTokenSource();
        h.Prompt.Then(async (r, ct) =>
        {
            var approved = await h.ApproveHere()(r, ct);
            circuit.Cancel();   // the circuit dropped right after the decision, before the continuation
            return approved;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.OnboardAsync(ct: circuit.Token));
        var approval = h.Approvals.Get(h.Prompt.Asked.Single().ApprovalId)!;
        Assert.Empty(h.Runs);
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Null(approval.ConsumedAt);

        // The same person repeats the identical click: the open grant is found and used once, for its operation.
        Assert.Equal("onboarded", await h.OnboardAsync());
        Assert.Equal(["onboard"], h.Runs);
        Assert.Equal(approval.CorrelationId, approval.ConsumedBy);
        Assert.Single(h.Prompt.Asked);

        // A second repeat finds no open approval and raises a new one.
        var again = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync(h.Runner(withPrompt: false)));
        Assert.NotEqual(approval.Id, again.ApprovalId);
        Assert.Equal(["onboard"], h.Runs);
    }

    [Fact]
    public async Task A_consumed_grant_reports_already_used_and_never_the_drift_text()
    {
        var h = new Harness();
        h.Prompt.Then(async (r, ct) =>
        {
            var approved = await h.ApproveHere()(r, ct);
            // Another tab of the same person continues first and uses the grant.
            Assert.Equal("onboarded", await h.OnboardAsync(h.Runner(withPrompt: false)));
            return approved;
        });

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());

        var first = h.Prompt.Asked.Single();
        Assert.Equal(["onboard"], h.Runs);
        Assert.Equal(MutationStatus.PendingApproval, refused.Status);
        Assert.NotEqual(first.ApprovalId, refused.ApprovalId);
        Assert.Contains("already used", refused.Message);
        Assert.Contains(refused.ApprovalId!, refused.Message);
        Assert.DoesNotContain("changed", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_prompt_that_throws_runs_nothing()
    {
        var h = new Harness();
        h.Prompt.Then((_, _) => throw new InvalidOperationException("no dialog provider"));

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync());

        Assert.Equal(MutationStatus.PendingApproval, refused.Status);
        Assert.Equal(ApprovalStatus.Pending, h.Approvals.Get(refused.ApprovalId!)!.Status);
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task Server_hostkey_replace_keeps_todays_refusal_with_the_approval_id()
    {
        var h = new Harness();

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.Runner().RunAsync("server.hostkey_replace",
            new { serverId = "edge", name = "Edge" },
            (ResolvedTarget _, CancellationToken _) => { h.Runs.Add("replace"); return Task.CompletedTask; }));

        Assert.Equal(MutationStatus.PendingApproval, refused.Status);
        Assert.NotNull(refused.ApprovalId);
        Assert.Empty(h.Prompt.Asked);
        Assert.Empty(h.Runs);
        Assert.False(InlineApproval.Capabilities.Contains("server.hostkey_replace"));
    }
    [Fact]
    public async Task A_circuit_dropped_after_the_decision_runs_nothing()
    {
        var h = new Harness();
        var circuit = new Whiskers.Components.Shared.InlineApprovalCircuit();
        await circuit.OnCircuitOpenedAsync(null!, default);
        h.Prompt.Then(async (r, ct) =>
        {
            var approved = await h.ApproveHere()(r, ct);
            // What the real prompt hands back: the token of the circuit the dialog ran in …
            var decided = approved with { Circuit = circuit.Lost };
            // … and the circuit drops after the decision, before the continuation. The page's own call is not cancelled.
            await circuit.OnConnectionDownAsync(null!, default);
            return decided;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.OnboardAsync());

        var approval = h.Approvals.Get(h.Prompt.Asked.Single().ApprovalId)!;
        Assert.Empty(h.Runs);
        Assert.Empty(h.Begins);
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Null(approval.ConsumedAt);   // the grant stays unused and lapses
        // A connection that comes back does not revive what was decided before the drop.
        await circuit.OnConnectionUpAsync(null!, default);
        Assert.True(circuit.IsLive);
        Assert.False(circuit.Lost.IsCancellationRequested);
    }

    public static TheoryData<string> SameOperationOtherCaller => ["another requester", "not the web ui"];

    /// <summary>The approval was raised for exactly this operation, but not by this person in the web UI: only the
    /// requester or the surface check stands between it and the dialog.</summary>
    [Theory]
    [MemberData(nameof(SameOperationOtherCaller))]
    public async Task An_approval_of_this_operation_from_another_caller_is_never_prompted(string caller)
    {
        var h = new Harness();
        var raised = await h.Approvals.CreateAsync(new ApprovalRequest("mcp-direct", "call-1",
            caller == "another requester" ? "other@example.com" : "ci",
            caller == "another requester" ? "web" : "mcp-direct", "server.onboard", McpPermissionLevels.Admin, "{}",
            "control plane", CorrelationId: "op-same",
            Requesters: [caller == "another requester" ? PrincipalIds.User("other@example.com") : PrincipalIds.User(AdminEmail)]),
            _ => Task.CompletedTask);
        var stub = new StubGateway(new MutationOutcome<string>("op-same", MutationStatus.PendingApproval, null,
            "pending", VerificationStatus.NotRun, ApprovalId: raised.Id) { MatchedRules = [ProtectedResourceRule.RuleId] });
        var runner = new UiMutationRunner(stub, new CapabilityCatalog(), h.User, prompt: h.Prompt, approvals: h.Approvals);

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => runner.RunAsync("server.onboard", OnboardArgs,
            (ResolvedTarget _, CancellationToken _) => Task.FromResult("x")));

        Assert.Equal(raised.Id, refused.ApprovalId);
        Assert.Empty(h.Prompt.Asked);
        Assert.Equal(ApprovalStatus.Pending, h.Approvals.Get(raised.Id)!.Status);
    }

    [Fact]
    public async Task An_approval_given_after_the_dialog_closed_is_not_called_left_pending()
    {
        var h = new Harness(secondApprover: true);
        // The dialog's wait ended (closed, or the expiry backstop) just as a second Admin approved it on the page.
        h.Prompt.Then(async (r, _) =>
        {
            await h.Approvals.DecideAsync(r.ApprovalId, true, h.Admin("second@example.com"), ApprovalChannel.Page);
            return new InlineApprovalResult(InlineApprovalOutcome.LeftPending);
        });

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => h.OnboardAsync(h.Runner()));

        Assert.Empty(h.Runs);
        Assert.Contains("second@example.com", refused.Message);
        Assert.DoesNotContain("left on the Approvals page", refused.Message);
    }


    /// <summary>A gateway that answers one fixed outcome and never runs anything.</summary>
    internal sealed class StubGateway(MutationOutcome<string> outcome) : IMutationGateway
    {
        public Task<MutationOutcome<T>> ExecuteAsync<T>(MutationRequest request,
            Func<MutationContext, CancellationToken, Task<T>> execute, CancellationToken ct = default) =>
            Task.FromResult(new MutationOutcome<T>(outcome.OperationId, outcome.Status, default, outcome.Reason,
                outcome.Verification, ApprovalId: outcome.ApprovalId) { MatchedRules = outcome.MatchedRules });

        public Task<MutationOutcome<object?>> RefuseAsync(MutationRequest request, string reason, CancellationToken ct = default) =>
            Task.FromResult(new MutationOutcome<object?>("op-refused", MutationStatus.Denied, null, reason, VerificationStatus.NotRun));
    }
}
