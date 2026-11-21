using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 (D1, D5, INV-64): an onboarding with a cloud bootstrap needs a human Admin's approval on every
/// non-interactive path whatever the preset; the rule is in the agent rule set only. The approval is bound to the VM's
/// id, name and public IP and to the arguments; a VM that cannot be read refuses before any approval is raised; the
/// card names what a second person approves.
/// </summary>
public sealed class CloudBootstrapRuleTests : IDisposable
{
    private readonly TempServerStore _store = new();

    public void Dispose() => _store.Dispose();

    private static Dictionary<string, JsonElement> Args(string bootstrap = "hetzner_rescue", string? confirm = null, bool resolved = true)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["serverId"] = JsonSerializer.SerializeToElement("web-new"),
            ["bootstrap"] = JsonSerializer.SerializeToElement(bootstrap),
        };
        if (confirm is not null) args["confirmVmName"] = JsonSerializer.SerializeToElement(confirm);
        if (resolved)
        {
            args[CloudBootstrapRule.ResolvedPrefix + "Id"] = JsonSerializer.SerializeToElement("20");
            args[CloudBootstrapRule.ResolvedPrefix + "Name"] = JsonSerializer.SerializeToElement("web-new-vm");
            args[CloudBootstrapRule.ResolvedPrefix + "Ip"] = JsonSerializer.SerializeToElement("203.0.113.20");
            args[CloudBootstrapRule.ResolvedPrefix + "Created"] = JsonSerializer.SerializeToElement("2026-01-02T03:04:05+00:00");
            args[CloudBootstrapRule.ResolvedPrefix + "Labels"] = JsonSerializer.SerializeToElement("env=test");
        }
        return args;
    }

    private static GuardrailRequest Request(AgentPrincipalKind kind, AgentOrigin origin, GuardrailPolicy policy,
        Dictionary<string, JsonElement>? args = null) =>
        new("server.onboard", McpPermissionLevels.Admin, args ?? Args(),
            new AgentContext("s", new AgentPrincipal(kind, "who", McpPermissionLevels.Admin, null), origin, policy));

    private static GuardrailPolicy Permissive()
    {
        var p = GuardrailPolicy.SafeDefault();
        p.ProtectedResources = [];
        p.ForbiddenArgPatterns = [];
        p.MaxAutonomousLevel = McpPermissionLevels.Admin;
        p.RequireConfirmationForWrites = false;
        return p;
    }

    public static TheoryData<string> Callers => ["direct-mcp", "agent", "trigger"];

    private static (AgentPrincipalKind, AgentOrigin) Caller(string who) => who switch
    {
        "direct-mcp" => (AgentPrincipalKind.McpKey, AgentOrigin.McpDirect),
        "agent" => (AgentPrincipalKind.McpKey, AgentOrigin.McpTool),
        _ => (AgentPrincipalKind.Trigger, AgentOrigin.Trigger),
    };

    [Theory]
    [MemberData(nameof(Callers))]
    public void Confirms_for_direct_mcp_agent_and_trigger(string who)
    {
        var (kind, origin) = Caller(who);

        var decision = GuardrailEngine.CreateDefault().Evaluate(Request(kind, origin, Permissive()));

        Assert.Equal(GuardrailVerdict.Confirm, decision.Verdict);
        Assert.Contains(CloudBootstrapRule.RuleId, decision.MatchedRuleIds);
    }

    [Fact]
    public void No_preset_removes_the_rule()
    {
        var policies = AiOperationsPresets.Starters().Select(p => p.Policy).Append(Permissive()).Append(GuardrailPolicy.SafeDefault());
        foreach (var policy in policies)
            foreach (var who in new[] { "direct-mcp", "agent", "trigger" })
            {
                var (kind, origin) = Caller(who);
                var decision = GuardrailEngine.CreateDefault().Evaluate(Request(kind, origin, policy));
                // Never allowed: confirmed by this rule, or denied by a stricter one.
                Assert.NotEqual(GuardrailVerdict.Allow, decision.Verdict);
                if (decision.Verdict == GuardrailVerdict.Confirm) Assert.Contains(CloudBootstrapRule.RuleId, decision.MatchedRuleIds);
            }
    }

    [Fact]
    public void The_rule_is_in_CreateDefault_and_not_in_CreateForHumans()
    {
        static IReadOnlyList<IGuardrailRule> Rules(GuardrailEngine e) =>
            (IReadOnlyList<IGuardrailRule>)typeof(GuardrailEngine).GetField("_rules", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e)!;

        Assert.Contains(Rules(GuardrailEngine.CreateDefault()), r => r is CloudBootstrapRule);
        var human = Rules(GuardrailEngine.CreateForHumans());
        // Contract 0022 (D-1): the human engine also asks a second Admin for trusted-script additions.
        Assert.Equal(4, human.Count);
        Assert.Contains(human, r => r is TrustedScriptApprovalRule);
        Assert.DoesNotContain(human, r => r is CloudBootstrapRule);
        var person = GuardrailEngine.CreateForHumans().Evaluate(Request(AgentPrincipalKind.WebUser, AgentOrigin.WebUi, Permissive()));
        Assert.Equal(GuardrailVerdict.Allow, person.Verdict);   // the person decides in the confirmation dialog (PC-D1)
    }

    [Theory]
    [InlineData("none")]
    [InlineData(null)]
    public void Without_a_bootstrap_the_rule_allows(string? bootstrap)
    {
        var args = Args(resolved: false);
        if (bootstrap is null) args["bootstrap"] = JsonSerializer.SerializeToElement<string?>(null);
        else args["bootstrap"] = JsonSerializer.SerializeToElement(bootstrap);

        Assert.Equal(GuardrailVerdict.Allow,
            new CloudBootstrapRule().Evaluate(Request(AgentPrincipalKind.McpKey, AgentOrigin.McpDirect, Permissive(), args), out _));
    }

    [Fact]
    public void Card_reason_names_vm_name_created_labels_and_ip()
    {
        Assert.Equal(GuardrailVerdict.Confirm,
            new CloudBootstrapRule().Evaluate(Request(AgentPrincipalKind.McpKey, AgentOrigin.McpDirect, Permissive()), out var reason));

        Assert.Contains("web-new-vm", reason);
        Assert.Contains("id 20", reason);
        Assert.Contains("2026-01-02T03:04:05+00:00", reason);
        Assert.Contains("env=test", reason);
        Assert.Contains("203.0.113.20", reason);
    }

    // ---- binding and resolution ----

    private static readonly MutationPrincipal Key =
        new(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin, CredentialId: "k1");

    private static MutationRequest Call(string cloudVm = "20|web-new-vm|203.0.113.20", string bootstrap = "hetzner_rescue", string? confirm = null) =>
        new("server.onboard", Key, MutationSurface.Mcp,
            new MutationTarget("server", "web-new", "web-new", "web-new") { CloudVm = cloudVm }, Args(bootstrap, confirm, resolved: false));

    [Fact]
    public void Approval_binding_changes_with_vm_id_name_and_public_ip()
    {
        var capability = new CapabilityCatalog().Find("server.onboard")!;
        var baseline = ApprovalBinding.Of(Call(), capability, "v1");

        Assert.Equal(baseline, ApprovalBinding.Of(Call(), capability, "v1"));
        Assert.NotEqual(baseline, ApprovalBinding.Of(Call("21|web-new-vm|203.0.113.20"), capability, "v1"));
        Assert.NotEqual(baseline, ApprovalBinding.Of(Call("20|renamed|203.0.113.20"), capability, "v1"));
        Assert.NotEqual(baseline, ApprovalBinding.Of(Call("20|web-new-vm|203.0.113.21"), capability, "v1"));
    }

    [Fact]
    public void Approval_binding_changes_with_bootstrap_and_confirmVmName()
    {
        var capability = new CapabilityCatalog().Find("server.onboard")!;
        var baseline = ApprovalBinding.Of(Call(), capability, "v1");

        Assert.NotEqual(baseline, ApprovalBinding.Of(Call(bootstrap: "none"), capability, "v1"));
        Assert.NotEqual(baseline, ApprovalBinding.Of(Call(confirm: "web-new-vm"), capability, "v1"));
        Assert.NotEqual(ApprovalBinding.Of(Call(confirm: "web-new-vm"), capability, "v1"), ApprovalBinding.Of(Call(confirm: "other"), capability, "v1"));
    }

    private (MutationGateway Gateway, ApprovalStore Approvals) Gateway(IHetznerService hetzner, ServerConfig server)
    {
        var policy = Permissive();
        var guardrails = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var approvals = new ApprovalStore(policy: guardrails);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(guardrails),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            targets: new MutationTargetResolver(new TestServices(new FakeServerConfig(server), hetzner, _store.Vault)),
            approvals: new ApprovalGate(approvals, guardrails));
        return (gateway, approvals);
    }

    private async Task<ServerConfig> BoundAsync()
    {
        var reference = $"cloudkey:web-new:{Guid.NewGuid():N}";
        await _store.Vault.SetSecretAsync(reference, "tok-" + Guid.NewGuid().ToString("N"));
        return new ServerConfig
        {
            Id = "web-new", Name = "web-new", ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.20", SshUser = "root",
            CloudProvider = CloudProvider.Hetzner, CloudServerId = 20, CloudKeyRef = reference,
        };
    }

    private static MutationRequest Onboard()
    {
        var capability = new CapabilityCatalog().Find("server.onboard")!;
        var args = Args(resolved: false);
        args["name"] = JsonSerializer.SerializeToElement("web-new");
        return new MutationRequest("server.onboard", Key, MutationSurface.Mcp, MutationTargets.FromArguments(capability, args), args);
    }

    [Fact]
    public async Task Unreadable_vm_refuses_without_raising_an_approval()
    {
        var (hetzner, script) = ScriptedProxy<IHetznerService>.Create();
        script.On(nameof(IHetznerService.GetServerAsync), _ => throw new HttpRequestException("503 from the provider"));
        var (gateway, approvals) = Gateway(hetzner, await BoundAsync());
        var runs = 0;

        var outcome = await gateway.ExecuteAsync(Onboard(), (_, _) => Task.FromResult(++runs));

        Assert.NotEqual(MutationStatus.PendingApproval, outcome.Status);
        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.Empty(approvals.GetPending());
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task A_readable_vm_raises_an_approval_naming_it()
    {
        var (hetzner, script) = ScriptedProxy<IHetznerService>.Create();
        script.On(nameof(IHetznerService.GetServerAsync), _ => new HetznerServer
        {
            Id = 20, Name = "web-new-vm", Status = "running", Created = "2026-01-02T03:04:05+00:00",
            Labels = new Dictionary<string, string> { ["env"] = "test" },
            PublicNet = new HetznerPublicNet { Ipv4 = new HetznerIpv4 { Ip = "203.0.113.20" } },
        });
        var (gateway, approvals) = Gateway(hetzner, await BoundAsync());

        var outcome = await gateway.ExecuteAsync(Onboard(), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        var pending = Assert.Single(approvals.GetPending());
        Assert.Equal(McpPermissionLevels.Admin, pending.Level);
    }
}
