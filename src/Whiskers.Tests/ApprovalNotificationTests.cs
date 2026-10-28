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

/// <summary>R1-15 (C-22, C-19): an approval notification says which approval, how risky and on which target the
/// gateway resolved, and links to the approval's card — it never carries the arguments or a value read from them,
/// since the bell may forward it to external channels. A caller with too many pending approvals is refused instead of
/// raising another.</summary>
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

    /// <summary>The arguments carry the marker in every key a target could be read from.</summary>
    private static ApprovalSubject Call(string requester, int n = 0, string? server = null, string? resource = null) =>
        new("restart_container", McpPermissionLevels.Admin, requester, "ci-key", "mcp-direct",
            Args(new { serverId = ArgumentMarker, name = ArgumentMarker, containerId = ArgumentMarker, n }))
        {
            Binding = $"binding-{requester}-{n}",
            Requesters = [requester],
            TargetServer = server,
            TargetWorkload = resource,
            TargetKind = "container",
        };

    private static void AssertSafe(NotificationEvent sent, Approval approval, string risk, string target)
    {
        var text = sent.ImageInfo!;
        Assert.Contains(approval.Id, text);
        Assert.Contains($"risk {risk}", text);
        Assert.Contains($"target {target}", text);
        Assert.DoesNotContain(ArgumentMarker, text);
        Assert.Equal("agent_approval", sent.EventType);
        // The bell opens the approval's own card.
        Assert.Equal(approval.Id, sent.ApprovalId);
        Assert.Equal($"approvals#{approval.Id}", NotificationFormatter.LinkFor(sent));
    }

    [Fact]
    public async Task A_gateway_approval_names_the_target_kind_and_links_to_its_card()
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        var gate = new ApprovalGate(store, new Policy(), notify);

        var result = await gate.CheckAsync(Call("key:k1", server: "srv-1", resource: "web"), "needs a human", []);

        var approval = store.Get(result.ApprovalId!)!;
        AssertSafe(Assert.Single(notify.Sent), approval, "high", "container");
        // A resolved server or name never leaves the app: the card shows them, the notification does not.
        Assert.DoesNotContain("srv-1", notify.Sent[0].ImageInfo);
        Assert.Equal(["key:k1"], approval.Requesters);
        Assert.Equal(("srv-1", "web"), (approval.TargetServer, approval.TargetWorkload));
    }

    [Fact]
    public async Task A_notification_names_no_value_from_the_arguments()
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        var gate = new ApprovalGate(store, new Policy(), notify);

        var result = await gate.CheckAsync(Call("key:k1"), "needs a human", []);

        AssertSafe(Assert.Single(notify.Sent), store.Get(result.ApprovalId!)!, "high", "container");
    }

    private static AgentContext Chat => new("chat-1",
        new AgentPrincipal(AgentPrincipalKind.WebUser, "alice", McpPermissionLevels.Admin, null, UserEmail: "alice@example.com"),
        AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());

    private static AgentToolCall ChatCall() => new("call_0", "stop_container",
        JsonSerializer.Serialize(new { serverId = ArgumentMarker, containerId = ArgumentMarker, name = ArgumentMarker }));

    [Fact]
    public async Task A_chat_approval_names_the_target_kind_and_not_the_resolved_names()
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        var coordinator = new ApprovalCoordinator(store, notify, new Policy());
        var call = ChatCall();

        var approval = await coordinator.RaiseAsync(new Session("chat-1"), call, Chat, "Writes need a human.",
            new GuardrailDecision(GuardrailVerdict.Confirm, "Writes need a human.", Array.Empty<string>())
            {
                ResolvedServer = "srv-2",
                ResolvedResource = "web",
                TargetKind = "container",
            });

        AssertSafe(Assert.Single(notify.Sent), approval, approval.RiskLevel!, "container");
        Assert.DoesNotContain("srv-2", notify.Sent[0].ImageInfo);
        Assert.Equal(["user:alice@example.com"], approval.Requesters);
        Assert.Equal(call.CorrelationId, approval.CorrelationId);
    }

    [Fact]
    public async Task A_chat_approval_the_gateway_did_not_decide_names_no_target()
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        var coordinator = new ApprovalCoordinator(store, notify, new Policy());

        var approval = await coordinator.RaiseAsync(new Session("chat-1"), ChatCall(), Chat, "Writes need a human.",
            new GuardrailDecision(GuardrailVerdict.Confirm, "Writes need a human.", Array.Empty<string>()));

        AssertSafe(Assert.Single(notify.Sent), approval, approval.RiskLevel!, "—");
    }

    private static Approval Plain() => new()
    {
        SessionId = "s", ToolCallId = "c", Actor = "key:k1", ActorType = "mcp-direct", ToolName = "t", Reason = "r",
        Level = McpPermissionLevels.Write,
    };

    [Theory]
    [InlineData("srv\nInjected: line")]
    [InlineData("password=only-in-the-arguments")]
    [InlineData("token:abc123")]
    [InlineData("kind with spaces")]
    public void A_target_kind_that_is_not_a_plain_identifier_is_withheld(string kind)
    {
        var text = ApprovalDisplay.NotificationText(Plain(), kind);
        Assert.Contains("target (withheld)", text);
        Assert.DoesNotContain(kind, text);
    }

    [Theory]
    [InlineData("container")]
    [InlineData("dns-zone")]
    [InlineData("scheduled-task")]
    public void A_target_kind_is_shown(string kind) =>
        Assert.Contains($"target {kind}", ApprovalDisplay.NotificationText(Plain(), kind));

    [Theory]
    [InlineData("https://whiskers.example.com", "https://whiskers.example.com/approvals#")]
    [InlineData("https://example.com/whiskers/", "https://example.com/whiskers/approvals#")]
    public async Task With_a_configured_public_address_outbound_channels_get_the_approval_link(string configured, string prefix)
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        var publicUrl = new WhiskersPublicUrl(WhiskersPublicUrl.Parse(configured));
        var gate = new ApprovalGate(store, new Policy(), notify, publicUrl: publicUrl);

        var result = await gate.CheckAsync(Call("key:k1", server: "srv-1", resource: "web"), "needs a human", []);

        // What an e-mail, Telegram or ntfy message carries.
        var outbound = NotificationFormatter.PlainText(Assert.Single(notify.Sent));
        Assert.Contains(prefix + Uri.EscapeDataString(result.ApprovalId!), outbound);
        Assert.DoesNotContain(ArgumentMarker, outbound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://whiskers.example.com")]
    [InlineData("https://user:pw@whiskers.example.com")]
    [InlineData("https://whiskers.example.com/?next=evil")]
    [InlineData("/relative")]
    public void Without_a_valid_public_address_no_absolute_link_is_made(string? configured)
    {
        var publicUrl = new WhiskersPublicUrl(WhiskersPublicUrl.Parse(configured));
        Assert.Null(publicUrl.Base);
        var text = ApprovalDisplay.NotificationText(Plain(), null, publicUrl);
        Assert.Contains("(Whiskers → Approvals)", text);
        Assert.DoesNotContain("://", text);
    }

    [Fact]
    public void An_approval_notification_without_an_id_still_opens_the_list()
    {
        Assert.Equal("approvals", NotificationFormatter.LinkFor(new NotificationEvent { EventType = "agent_approval" }));
        Assert.Equal("approvals#a%2Fb", NotificationFormatter.LinkFor(new NotificationEvent { EventType = "agent_approval", ApprovalId = "a/b" }));
    }

    [Fact]
    public async Task A_caller_with_too_many_pending_approvals_is_refused_and_nothing_is_announced()
    {
        var store = new ApprovalStore();
        var notify = new Recorder();
        var gate = new ApprovalGate(store, new Policy(), notify);
        for (var i = 0; i < ApprovalEligibility.MaxPendingPerPrincipal; i++)
            Assert.False((await gate.CheckAsync(Call("key:k1", i), "needs a human", [])).Approved);

        var refused = await gate.CheckAsync(Call("key:k1", 999), "needs a human", []);

        Assert.False(refused.Approved);
        Assert.Null(refused.ApprovalId);
        Assert.Contains("not executed", refused.Message);
        Assert.Equal(ApprovalEligibility.MaxPendingPerPrincipal, notify.Sent.Count);
        // Another caller still gets its approval.
        Assert.NotNull((await gate.CheckAsync(Call("key:k2"), "needs a human", [])).ApprovalId);
    }
}
