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
            NotifiedTarget = ApprovalDisplay.NotifiedTarget("container", server is null && resource is null ? null
                : new Whiskers.Services.Mutations.MutationTarget("container", resource ?? "", null, server) { Resolved = true }),
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
        // Found in the inventory: the notification tells this container from any other.
        AssertSafe(Assert.Single(notify.Sent), approval, "high", "container web @ srv-1");
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
                NotifiedTarget = ApprovalDisplay.NotifiedTarget("container",
                    new Whiskers.Services.Mutations.MutationTarget("container", "web", null, "srv-2") { Resolved = true }),
            });

        AssertSafe(Assert.Single(notify.Sent), approval, approval.RiskLevel!, "container web @ srv-2");
        Assert.Equal(["user:alice@example.com"], approval.Requesters);
        Assert.Equal(call.CorrelationId, approval.CorrelationId);
    }

    [Theory]
    [InlineData("ALTER ROLE whiskers PASSWORD 'canary-secret';")]
    [InlineData("ALTER ROLE whiskers PASSWORD E'canary-secret';")]
    [InlineData("ALTER ROLE whiskers PASSWORD 'it'' s canary-secret';")]
    [InlineData("ALTER ROLE whiskers PASSWORD e'it\\'s canary-secret';")]
    [InlineData("CREATE USER whiskers IDENTIFIED BY \"canary-secret\";")]
    public async Task An_mcp_approval_stores_the_sql_without_its_password_literal(string query)
    {
        var store = new ApprovalStore();
        var gate = new ApprovalGate(store, new Policy(), new Recorder());
        var subject = new ApprovalSubject("db.query", McpPermissionLevels.Admin, "key:k1", "ci-key", "mcp-direct",
            Args(new { serverId = "local", containerId = "db", query }))
        {
            Binding = "binding-sql",
            Requesters = ["key:k1"],
        };

        var result = await gate.CheckAsync(subject, "needs a human", []);

        var stored = store.Get(result.ApprovalId!)!;
        Assert.DoesNotContain("canary", stored.ParamsJson);
        Assert.Contains(" whiskers ", stored.ParamsJson);   // the statement stays readable
        Assert.Contains("'***';", stored.ParamsJson);
    }

    [Fact]
    public async Task A_chat_approval_stores_the_sql_without_its_password_literal()
    {
        var store = new ApprovalStore();
        var coordinator = new ApprovalCoordinator(store, new Recorder(), new Policy());
        var call = new AgentToolCall("call_0", "db_execute", JsonSerializer.Serialize(new
        {
            serverId = "local", containerId = "db", sql = "ALTER ROLE whiskers PASSWORD 'canary-secret';",
        }));

        var approval = await coordinator.RaiseAsync(new Session("chat-1"), call, Chat, "Writes need a human.",
            new GuardrailDecision(GuardrailVerdict.Confirm, "Writes need a human.", Array.Empty<string>()));

        var stored = store.Get(approval.Id)!;
        Assert.DoesNotContain("canary", stored.ParamsJson);
        Assert.Contains("ALTER ROLE whiskers PASSWORD '***'", stored.ParamsJson);
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

    private static Whiskers.Services.Mutations.MutationTarget Found(string kind, string id, string? server) =>
        new(kind, id, null, server) { Resolved = true };

    [Theory]
    [InlineData("srv\nInjected: line")]
    [InlineData("password=only-in-the-arguments")]
    [InlineData("token:abc123")]
    [InlineData("db name with spaces")]
    public void An_inventory_id_that_is_not_a_plain_identifier_is_withheld(string value)
    {
        var named = ApprovalDisplay.NotifiedTarget("container", Found("container", value, value));
        Assert.Equal("container (withheld) @ (withheld)", named);
        Assert.DoesNotContain(value, ApprovalDisplay.NotificationText(Plain(), named));
    }

    [Theory]
    // Ids and servers the resolver took from the inventory: named.
    [InlineData("container", "3f2a9c", "srv-1", "container 3f2a9c @ srv-1")]
    [InlineData("workload", "pod-7", "k3s", "workload pod-7 @ k3s")]
    [InlineData("scheduled-task", "t-42", "srv-1", "scheduled-task t-42")]      // its server may be the caller's
    [InlineData("webhook", "wh-1", "srv-1", "webhook wh-1")]
    [InlineData("server", "srv-1", "srv-1", "server srv-1")]
    // A DNS zone is checked against a list that may be empty, a host is not looked up: only the kind.
    [InlineData("dns-zone", "example.com", "caller-named-7f3c", "dns-zone")]
    [InlineData("host", "caller-named-7f3c", "caller-named-7f3c", "host")]
    public void Only_ids_the_resolver_took_from_the_inventory_are_named(string kind, string id, string server, string expected) =>
        Assert.Equal(expected, ApprovalDisplay.NotifiedTarget(kind, Found(kind, id, server)));

    [Fact]
    public void A_target_not_found_in_the_inventory_is_named_by_its_kind_only()
    {
        Assert.Equal("container", ApprovalDisplay.NotifiedTarget("container",
            new Whiskers.Services.Mutations.MutationTarget("container", "caller-named-7f3c", null, "caller-named-7f3c")));
        Assert.Equal("container", ApprovalDisplay.NotifiedTarget("container", null));
        Assert.Null(ApprovalDisplay.NotifiedTarget(null, null));
    }

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
