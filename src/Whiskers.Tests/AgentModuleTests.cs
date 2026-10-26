using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Mcp.Tools;
using Whiskers.Modules;
using Whiskers.Modules.Agent;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 §3.8 — the Agent + AI-chat module move (the last and largest §3 extraction).
/// Covers the module metadata (the four Automatisierung nav entries + the AgentTools MCP tool), that those
/// moved out of the transitional pseudo-module while "agent-history" stayed Core, that ConfigureServices
/// registers the agent service graph plus the two IInitializable warm-ups (moved verbatim from the Core init
/// block), and the ModuleCatalog enable/disable gate. Full-graph DI resolution is covered by the app boot-gate
/// (the agent pulls Docker/DB/HTTP Core services, so it isn't reconstructed here); the individual agent
/// services keep their own unit tests.</summary>
public class AgentModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Metadata_is_the_agent_module()
    {
        var m = new AgentModule();
        Assert.Equal("agent", m.Id);
        Assert.True(m.EnabledByDefault);
        Assert.Empty(m.DependsOn);
    }

    [Fact]
    public void Contributes_the_automation_nav_entries()
    {
        var nav = new AgentModule().NavItems;
        Assert.Equal(new[] { "agent", "ai-operations", "ai-triggers" }, nav.Select(n => n.Href).ToArray());
        Assert.All(nav, n => Assert.Equal("Automatisierung", n.Group));
    }

    [Fact]
    public void Exposes_the_agent_mcp_tool()
    {
        Assert.Equal(new[] { typeof(AgentTools) }, new AgentModule().McpToolTypes);
    }

    [Fact]
    public void Agent_nav_and_tool_moved_out_of_the_pseudo_module()
    {
        var pseudo = new AllInOnePseudoModule();
        foreach (var href in new[] { "agent", "ai-triggers" })
            Assert.DoesNotContain(pseudo.NavItems, n => n.Href == href);
        Assert.DoesNotContain(typeof(AgentTools), pseudo.McpToolTypes);
    }

    [Fact]
    public void Guardrails_and_approvals_are_core_pages()
    {
        // ADR-0005 step 2.1 (PC-D8): they govern direct MCP calls, so they must not disappear with the agent.
        var pseudo = new AllInOnePseudoModule();
        foreach (var href in new[] { "guardrails", "approvals" })
        {
            Assert.Contains(pseudo.NavItems, n => n.Href == href);
            Assert.DoesNotContain(new AgentModule().NavItems, n => n.Href == href);
        }
    }

    [Fact]
    public void Agent_history_stays_in_core()
    {
        // agent-history reads the Core IMcpCallLogStore (MCP-call observability), independent of the acting
        // agent, so it must NOT move into the module — it stays in the transitional bucket.
        Assert.Contains(new AllInOnePseudoModule().NavItems, n => n.Href == "agent-history");
        Assert.DoesNotContain(new AgentModule().NavItems, n => n.Href == "agent-history");
    }

    // --- Registration shape (no heavy resolve; the app boot-gate resolves the full graph) -----------------

    [Fact]
    public void ConfigureServices_registers_the_agent_graph_but_not_the_core_policy()
    {
        var services = new ServiceCollection();
        new AgentModule().ConfigureServices(services, Config());

        // A representative slice of the moved registrations: the agent, the approval coordinator, the AI-chat
        // advisor, and the real trigger dispatcher (which wins over the Core no-op). Guardrails and the approval
        // store are core since ADR-0005 step 2.1 and must NOT come from the module.
        Assert.Contains(services, d => d.ServiceType == typeof(Whiskers.Services.Agent.IAgentService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Whiskers.Services.Guardrails.IGuardrailStore));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Whiskers.Services.Approvals.IApprovalStore));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Whiskers.Services.Guardrails.IDirectMcpCallGuard));
        Assert.Contains(services, d => d.ServiceType == typeof(Whiskers.Services.Agent.Approvals.IApprovalCoordinator));
        Assert.Contains(services, d => d.ServiceType == typeof(Whiskers.Services.AiChat.IAiChatService));
        Assert.Contains(services, d => d.ServiceType == typeof(Whiskers.Services.Agent.Triggers.IAiTriggerDispatcher)
                                       && d.ImplementationType == typeof(Whiskers.Services.Agent.Triggers.AiTriggerDispatcher));

        // Only the AiTriggerStore warm-up (90) is the module's; the GuardrailStore warm-up (80) is core.
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(Whiskers.Services.IInitializable)));
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "agent");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:agent:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "agent");
        // Other modules are unaffected by the agent flag.
        Assert.Contains(enabled, m => m.Id == "all-in-one");
        Assert.Contains(enabled, m => m.Id == "notifications");
    }
}
