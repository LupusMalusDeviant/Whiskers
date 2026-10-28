using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Approvals;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>R1-15 (C-22, C-19): an approval notification says which approval, how risky, on what, and where to decide
/// it — and never carries the arguments, since the bell may forward it to external channels. A caller with too many
/// pending approvals is refused instead of raising another.</summary>
public class ApprovalNotificationTests
{
    // Stands for anything in the arguments that must not leave Whiskers.
    private const string ArgumentMarker = "only-in-the-arguments-7f3c";

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

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static ApprovalSubject DirectCall(string requester, int n = 0) =>
        new("execute_command", McpPermissionLevels.Admin, requester, "ci-key", "mcp-direct",
            Args(new { serverId = "srv-1", command = $"echo {ArgumentMarker} {n}" }))
        {
            Binding = $"binding-{requester}-{n}",
            Requesters = [requester],
        };

    private static void AssertSafe(NotificationEvent sent, Approval approval, string risk, string target)
    {
        var text = sent.ImageInfo!;
        Assert.Contains(approval.Id, text);
        Assert.Contains($"risk {risk}", text);
        Assert.Contains($"target {target}", text);
        Assert.Contains($"/approvals#{approval.Id}", text);
        Assert.DoesNotContain(ArgumentMarker, text);
        Assert.Equal("agent_approval", sent.EventType);
    }

    [Fact]
    public async Task A_raised_direct_call_approval_is_announced_with_id_risk_target_and_link_but_no_arguments()
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        var gate = new ApprovalGate(store, new Policy(), notify);

        var result = await gate.CheckAsync(DirectCall("key:k1"), "needs a human", []);

        var approval = store.Get(result.ApprovalId!)!;
        AssertSafe(Assert.Single(notify.Sent), approval, "high", "srv-1 / —");
        Assert.Equal(["key:k1"], approval.Requesters);
    }

    [Fact]
    public async Task A_chat_approval_is_announced_the_same_way()
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        var coordinator = new ApprovalCoordinator(store, notify, new Policy());
        var context = new AgentContext("chat-1",
            new AgentPrincipal(AgentPrincipalKind.WebUser, "alice", McpPermissionLevels.Admin, null, UserEmail: "alice@example.com"),
            AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());
        var call = new AgentToolCall("call_0", "stop_container",
            JsonSerializer.Serialize(new { serverId = "srv-2", containerId = "web", reason = ArgumentMarker }));

        var approval = await coordinator.RaiseAsync(new Session("chat-1"), call, context, "Writes need a human.",
            new GuardrailDecision(GuardrailVerdict.Confirm, "Writes need a human.", Array.Empty<string>()));

        AssertSafe(Assert.Single(notify.Sent), approval, approval.RiskLevel!, "srv-2 / web");
        Assert.Equal(["user:alice@example.com"], approval.Requesters);
        Assert.Equal(call.CorrelationId, approval.CorrelationId);
    }

    [Theory]
    [InlineData("srv\nInjected: line")]
    [InlineData("password=only-in-the-arguments")]
    [InlineData("token:abc123")]
    [InlineData("db name with spaces")]
    public void A_target_that_is_not_a_plain_identifier_is_withheld(string target)
    {
        var approval = new Approval
        {
            SessionId = "s", ToolCallId = "c", Actor = "key:k1", ActorType = "mcp-direct", ToolName = "t", Reason = "r",
            Level = McpPermissionLevels.Write, TargetServer = target, TargetWorkload = new string('w', 500),
        };
        var text = ApprovalDisplay.NotificationText(approval);
        Assert.Contains("target (withheld) / (withheld)", text);
        Assert.DoesNotContain(target, text);
        Assert.DoesNotContain("ww", text);
    }

    [Theory]
    [InlineData("srv-1")]
    [InlineData("web_1.internal")]
    [InlineData("ghcr.io/org/app:1.2")]
    public void A_plain_identifier_is_shown(string target)
    {
        var approval = new Approval
        {
            SessionId = "s", ToolCallId = "c", Actor = "key:k1", ActorType = "mcp-direct", ToolName = "t", Reason = "r",
            Level = McpPermissionLevels.Write, TargetWorkload = target,
        };
        Assert.Contains($"target — / {target}", ApprovalDisplay.NotificationText(approval));
    }

    [Fact]
    public async Task A_caller_with_too_many_pending_approvals_is_refused_and_nothing_is_announced()
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        var gate = new ApprovalGate(store, new Policy(), notify);
        for (var i = 0; i < ApprovalEligibility.MaxPendingPerPrincipal; i++)
            Assert.False((await gate.CheckAsync(DirectCall("key:k1", i), "needs a human", [])).Approved);

        var refused = await gate.CheckAsync(DirectCall("key:k1", 999), "needs a human", []);

        Assert.False(refused.Approved);
        Assert.Null(refused.ApprovalId);
        Assert.Contains("not executed", refused.Message);
        Assert.Equal(ApprovalEligibility.MaxPendingPerPrincipal, notify.Sent.Count);
        // Another caller still gets its approval.
        Assert.NotNull((await gate.CheckAsync(DirectCall("key:k2"), "needs a human", [])).ApprovalId);
    }
}
