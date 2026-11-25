using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Approvals;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0017, amendment 5 (A2b): a privileged compose hook is decided at planning only. Every approval of a run
/// whose resolved target carries one names that limit — in <c>Approval.Reason</c> and in the notification, before the
/// link — decided on the target's exposure identities only, never on any text.
/// </summary>
public class PrivilegedHookNoticeTests
{
    private const string Sentence = ApprovalDisplay.PrivilegedHookSentence;
    private const string Link = "(Whiskers → Approvals)";

    private sealed class Recorder : INotificationService
    {
        public List<NotificationEvent> Sent { get; } = [];
        public Task SendAsync(NotificationEvent evt) { Sent.Add(evt); return Task.CompletedTask; }
        public Task SendTestAsync() => Task.CompletedTask;
    }

    private sealed class Policy : IGuardrailStore
    {
        public GuardrailPolicy Current { get; set; } = GuardrailPolicy.SafeDefault();
        public GuardrailConfig Config => GuardrailConfig.SafeDefault();
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class Session(string id) : IAgentSession
    {
        public string SessionId => id;
        public AgentRunState State => AgentRunState.AwaitingConfirmation;
        public IReadOnlyList<AgentMessage> History => [];
        public IAsyncEnumerable<AgentEvent> SendAsync(string userMessage, string? imageBase64 = null,
            string? imageMediaType = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ResolveConfirmationAsync(string operationId, bool approved, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static string Hook(string service, string hook = "post_start", int index = 0) =>
        ComposeExposures.Identity(service, ExposureKind.Privileged, ComposeExposures.HookKey(hook, index), "true");

    private static string Port(string service) =>
        ComposeExposures.Identity(service, ExposureKind.PublishedPort, "80/tcp", "0.0.0.0:8080");

    private static string PrivilegedService(string service) =>
        ComposeExposures.Identity(service, ExposureKind.Privileged, "privileged", "true");

    private static MutationTarget Target(params string[] exposures) =>
        new MutationTarget("compose-project", "shop", "shop", "local") { Resolved = true, Exposures = exposures };

    /// <summary>The subject the gateway builds for <c>compose.deploy</c> on <paramref name="target"/>.</summary>
    private static ApprovalSubject GatewaySubject(MutationTarget target, IReadOnlyList<string>? rules = null)
    {
        var capability = new CapabilityCatalog().Find("compose.deploy")!;
        var request = new MutationRequest("compose.deploy",
            new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com", McpPermissionLevels.Admin),
            MutationSurface.Mcp, target, new Dictionary<string, JsonElement>());
        return MutationGateway.ApprovalSubjectOf(request, capability, "op-1", null, rules);
    }

    private static async Task<(Approval Approval, string Notification)> RaiseAsync(ApprovalSubject subject, string reason,
        IReadOnlyList<string>? rules = null)
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        // Without a policy version the gateway binds nothing; the gate then (rightly) raises nothing.
        subject = subject with { Binding = subject.Binding ?? "binding-" + Guid.NewGuid().ToString("N") };
        var result = await new ApprovalGate(store, new Policy(), notify).CheckAsync(subject, reason, rules ?? []);
        Assert.NotNull(result.ApprovalId);
        return (store.Get(result.ApprovalId!)!, Assert.Single(notify.Sent).ImageInfo!);
    }

    [Fact]
    public async Task An_approval_for_a_run_with_a_privileged_post_start_hook_names_it_in_the_reason_and_before_the_link()
    {
        var (approval, text) = await RaiseAsync(GatewaySubject(Target(Hook("api"))), "Needs a human.");

        Assert.Equal($"Needs a human. {Sentence}", approval.Reason);
        Assert.Contains(Sentence, text);
        Assert.True(text.IndexOf(Sentence, StringComparison.Ordinal) < text.IndexOf(Link, StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_hook_raised_the_approval_the_rules_text_is_followed_by_the_sentence()
    {
        const string ruleText = "api runs a privileged post_start hook (hook:post_start:0); it is decided at planning only.";

        var (approval, _) = await RaiseAsync(GatewaySubject(Target(Hook("api"), Port("web")), [ExposureChangeRule.RuleId]),
            ruleText, [ExposureChangeRule.RuleId]);

        Assert.StartsWith(ruleText, approval.Reason);
        Assert.EndsWith(Sentence, approval.Reason);
    }

    [Fact]
    public async Task The_decision_rests_on_the_exposure_identities_never_on_a_service_name()
    {
        // A service named like the sentence, with only a published port: no sentence.
        var lookalike = Target(Port(Sentence), Port("hook:post_start:0"));
        Assert.False(GatewaySubject(lookalike).PrivilegedHook);
        var (approval, text) = await RaiseAsync(GatewaySubject(lookalike), "Needs a human.");
        Assert.Equal("Needs a human.", approval.Reason);
        Assert.DoesNotContain(Sentence, text);

        // Another service with a real privileged hook: the sentence.
        Assert.True(GatewaySubject(Target(Port(Sentence), Hook("worker", "pre_stop", 2))).PrivilegedHook);
    }

    [Fact]
    public async Task An_approval_raised_by_another_rule_names_the_hook_too()
    {
        var rules = new[] { ProtectedResourceRule.RuleId };

        var (approval, text) = await RaiseAsync(GatewaySubject(Target(Hook("api")), rules), "Touches a protected resource.", rules);

        Assert.Contains(Sentence, approval.Reason);
        Assert.Contains(Sentence, text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_without_a_hook_or_with_a_hook_that_is_not_privileged_gets_no_sentence(bool privilegedService)
    {
        // A non-privileged hook is no exposure at all; a privileged service (not a hook) is another limit.
        var target = privilegedService ? Target(PrivilegedService("api"), Port("api")) : Target(Port("api"));

        var (approval, text) = await RaiseAsync(GatewaySubject(target), "Needs a human.");

        Assert.DoesNotContain(Sentence, approval.Reason);
        Assert.DoesNotContain(Sentence, text);
    }

    [Theory]
    [InlineData("db-password-hunter2-service")]
    [InlineData("https://collector.example.com/collect")]
    public async Task The_sentence_names_neither_the_service_nor_the_hook_index(string service)
    {
        var (approval, text) = await RaiseAsync(GatewaySubject(Target(Hook(service, "post_start", 7))), "Needs a human.");

        Assert.Contains(Sentence, text);
        Assert.DoesNotContain(service, text);
        Assert.DoesNotContain("hook:post_start", text);
        Assert.DoesNotContain("post_start:7", text);
        // What the gate adds to the reason names neither either.
        Assert.Equal($"Needs a human. {Sentence}", approval.Reason);
    }

    /// <summary>What the gateway answers the agent for <paramref name="target"/>: a confirmation.</summary>
    private static GuardrailDecision AgentDecision(MutationTarget target) =>
        AgentToolInvoker.DecisionOf(
            new MutationDecisionResult("op-1", MutationVerdict.Confirm, "Writes need a human.", [], "dec-1", "Standard", null)
            {
                Binding = "binding-agent",
                Target = target,
            },
            new CapabilityCatalog().Find("compose.deploy")!);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_agent_path_takes_the_flag_from_the_gateways_target_and_adds_the_sentence(bool hook)
    {
        // AgentToolInvoker → GuardrailDecision → ApprovalCoordinator, as a chat-raised approval runs.
        var decision = AgentDecision(hook ? Target(Hook("api"), Port("web")) : Target(Port("web")));
        Assert.Equal(hook, decision.PrivilegedHook);
        var store = new ApprovalStore();
        var notify = new Recorder();
        var coordinator = new ApprovalCoordinator(store, notify, new Policy());
        var context = new AgentContext("chat-1",
            new AgentPrincipal(AgentPrincipalKind.WebUser, "alice", McpPermissionLevels.Admin, null, UserEmail: "alice@example.com"),
            AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());
        var call = new AgentToolCall("call_0", "compose_deploy", "{}");

        var approval = await coordinator.RaiseAsync(new Session("chat-1"), call, context, decision.Reason, decision);

        var text = Assert.Single(notify.Sent).ImageInfo!;
        if (hook)
        {
            Assert.Equal($"Writes need a human. {Sentence}", approval.Reason);
            Assert.True(text.IndexOf(Sentence, StringComparison.Ordinal) < text.IndexOf(Link, StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal("Writes need a human.", approval.Reason);
            Assert.DoesNotContain(Sentence, text);
        }
    }

    [Fact]
    public async Task The_stored_reason_is_the_real_rules_text_followed_by_the_sentence()
    {
        // The rule's own reason for the hook, as the agent rule set decides it.
        var exposures = new[] { Hook("api") };
        var policy = new GuardrailPolicy { MaxAutonomousLevel = McpPermissionLevels.Admin, RequireConfirmationForWrites = false };
        var request = new GuardrailRequest("deploy_compose", McpPermissionLevels.Admin,
            new Dictionary<string, JsonElement> { [ExposureChangeRule.ResolvedPrefix + "0"] = JsonSerializer.SerializeToElement(exposures[0]) },
            new AgentContext("s", new AgentPrincipal(AgentPrincipalKind.McpKey, "key", McpPermissionLevels.Admin, null),
                AgentOrigin.McpDirect, policy));
        Assert.Equal(GuardrailVerdict.Confirm, new ExposureChangeRule().Evaluate(request, out var ruleText));
        Assert.Contains("decided at planning only", ruleText);

        var (approval, _) = await RaiseAsync(GatewaySubject(Target(exposures), [ExposureChangeRule.RuleId]), ruleText,
            [ExposureChangeRule.RuleId]);

        Assert.Equal($"{ruleText} {Sentence}", approval.Reason);
    }

    [Fact]
    public void Only_a_privileged_post_start_or_pre_stop_hook_identity_counts()
    {
        Assert.True(ComposeExposures.IsPrivilegedHook(Hook("api")));
        Assert.True(ComposeExposures.IsPrivilegedHook(Hook("api", "pre_stop", 3)));
        Assert.False(ComposeExposures.IsPrivilegedHook(PrivilegedService("api")));
        Assert.False(ComposeExposures.IsPrivilegedHook(Port("api")));
        Assert.False(ComposeExposures.IsPrivilegedHook(
            ComposeExposures.Identity("api", ExposureKind.Privileged, "hook:other:0", "true")));
        Assert.False(ComposeExposures.IsPrivilegedHook(Sentence));
        Assert.False(ComposeExposures.IsPrivilegedHook(""));
    }
}
