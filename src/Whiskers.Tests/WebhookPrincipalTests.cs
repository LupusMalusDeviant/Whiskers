using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0014 (D3, D4, INV-01): a webhook delivery is its own principal — <c>Kind = Webhook</c> on the
/// <c>Webhook</c> surface, keyed <c>webhook:&lt;id&gt;</c>, naming the secret generation its signature was checked
/// against and acting for the webhook's owner — and it decides exactly as it did when it ran as an automation.
/// </summary>
public sealed class WebhookPrincipalTests : IDisposable
{
    private readonly WebhookMutationTests _harness = new();

    public void Dispose() => _harness.Dispose();

    [Theory]
    [MemberData(nameof(WebhookMutationTests.Actions), MemberType = typeof(WebhookMutationTests))]
    public async Task A_signed_delivery_is_one_webhook_principal_naming_its_secret_generation(string action, string target)
    {
        var webhook = _harness.SeedWebhook(action, target);

        var (success, output) = await _harness.DeliverSignedAsync(webhook.WebhookId);

        Assert.True(success, output);
        Assert.True(_harness.RanAction(action));
        var delivery = Assert.Single(_harness.GatewayRequests,
            r => r.Principal.Id == "webhook:" + webhook.WebhookId && r.ParentOperationId is null);
        var p = delivery.Principal;
        // Its own nested decisions (a deployment's containers, mounts) name the same run as their session; a Git
        // deployment it starts is the app's automation, a principal of its own.
        Assert.All(_harness.GatewayRequests.Where(r => r.ParentOperationId == delivery.OperationId && r.Principal.Id == p.Id),
            nested => Assert.Equal(delivery.OperationId, nested.Principal.SessionId));
        Assert.Equal(MutationSurface.Webhook, delivery.Surface);
        Assert.Equal(MutationPrincipalKind.Webhook, p.Kind);
        Assert.Equal(MutationPrincipalKind.Webhook, PrincipalIds.KindOf(p.Id));
        Assert.Equal($"webhook-secret:{webhook.WebhookId}@1", p.CredentialId);
        Assert.Equal(PrincipalIds.User("admin@example.com"), p.OnBehalfOf?.Id);
        Assert.False(string.IsNullOrEmpty(p.SessionId));
        Assert.Equal(delivery.OperationId, p.SessionId);
        Assert.DoesNotContain(webhook.Secret, p.CredentialId!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rotated_secret_is_a_new_generation_on_the_next_delivery()
    {
        var webhook = _harness.SeedWebhook("restart", "web-1");
        Assert.Equal(1, _harness.StoredSecretVersion(webhook.WebhookId));

        await _harness.RegenerateSecretAsync(webhook.WebhookId);
        Assert.Equal(2, _harness.StoredSecretVersion(webhook.WebhookId));
        var (success, output) = await _harness.DeliverSignedAsync(webhook.WebhookId);

        Assert.True(success, output);
        var delivery = Assert.Single(_harness.GatewayRequests, r => r.Principal.Id == "webhook:" + webhook.WebhookId);
        Assert.Equal($"webhook-secret:{webhook.WebhookId}@2", delivery.Principal.CredentialId);
    }

    [Fact]
    public void The_unattended_predicate_covers_automation_and_webhook_alike()
    {
        foreach (var surface in new[] { MutationSurface.Automation, MutationSurface.Webhook })
            Assert.True(Unattended.IsSurface(surface));
        foreach (var surface in new[] { MutationSurface.Mcp, MutationSurface.Agent, MutationSurface.Ui, MutationSurface.Terminal })
            Assert.False(Unattended.IsSurface(surface));
        Assert.True(Unattended.IsPrincipal(new MutationPrincipal(MutationPrincipalKind.Webhook, "webhook:w", "w", "write")));
        Assert.True(Unattended.IsPrincipal(new MutationPrincipal(MutationPrincipalKind.Automation, "task:t", "t", "write")));
        Assert.False(Unattended.IsPrincipal(new MutationPrincipal(MutationPrincipalKind.Human, "user:a@b.c", "a", "write")));
    }

    public static TheoryData<string, string, bool, bool> Matrix()
    {
        var data = new TheoryData<string, string, bool, bool>();
        foreach (var capability in new[] { "container.restart", "container.update", "compose.deploy", "gitdeploy.deploy" })
        foreach (var policy in new[] { "safe", "read-only", "deny-tool", "autonomous" })
        foreach (var admin in new[] { false, true })
        foreach (var agentCreated in new[] { false, true })
            data.Add(capability, policy, admin, agentCreated);
        return data;
    }

    /// <summary>The equivalence of D3: the same run, once as the automation it used to be and once as a webhook, is
    /// decided alike — the policy engine (PC-D1 rules or the agent's), the unattended refusal of a confirmation, the
    /// kill switch, a denied tool and the owner's level.</summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task A_webhook_decides_exactly_as_the_automation_it_used_to_be(string capability, string policyName,
        bool admin, bool agentCreated)
    {
        var owner = PrincipalIds.User("ops@example.com");
        var webhook = AutomationActor.ForWebhook("w1", "deploy", owner, 3) with
        {
            CreatedVia = agentCreated ? "agent:sess-1" : owner,
        };
        var automation = webhook with { Kind = MutationPrincipalKind.Automation, CredentialId = null };

        var asAutomation = await RunAsync(automation, capability, policyName, admin);
        var asWebhook = await RunAsync(webhook, capability, policyName, admin);

        Assert.Equal(asAutomation.Status, asWebhook.Status);
        Assert.Equal(asAutomation.Reason, asWebhook.Reason);
        Assert.Equal(asAutomation.ReasonCode, asWebhook.ReasonCode);
    }

    [Fact]
    public async Task The_equivalence_matrix_is_not_trivial()
    {
        var owner = PrincipalIds.User("ops@example.com");
        var webhook = AutomationActor.ForWebhook("w1", "deploy", owner, 1);
        Assert.Equal(MutationStatus.Executed, (await RunAsync(webhook, "container.restart", "safe", admin: false)).Status);
        Assert.Equal(MutationStatus.Denied, (await RunAsync(webhook, "container.restart", "read-only", admin: false)).Status);
        Assert.Equal(MutationStatus.Denied,
            (await RunAsync(webhook with { CreatedVia = "agent:sess-1" }, "container.restart", "safe", admin: false)).Status);
    }

    private static async Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capability, string policyName,
        bool admin)
    {
        var policy = GuardrailPolicy.SafeDefault();
        switch (policyName)
        {
            case "read-only": policy.ReadOnlyMode = true; break;
            case "deny-tool": policy.ToolDenyList.Add(new CapabilityCatalog().Find(capability)?.McpTool ?? capability); break;
            case "autonomous": policy.MaxAutonomousLevel = McpPermissionLevels.Admin; policy.RequireConfirmationForWrites = false; break;
        }
        var f = AutomationMutationRunnerTests.Build(requireOwner: true, policy: policy);
        f.Whitelist.Allowed.Add("ops@example.com");
        f.Roles.Map["ops@example.com"] = admin ? AppRole.Admin : AppRole.Operator;
        var arguments = capability switch
        {
            "compose.deploy" => (object)new { serverId = "local", projectName = "shop", composeContent = "services: {}" },
            "gitdeploy.deploy" => new { appId = "0123456789ab" },
            _ => new { containerId = "web-1" },
        };
        return await f.Runner.RunAsync(actor, capability, arguments, (ResolvedTarget _, CancellationToken _) => Task.CompletedTask);
    }
}
