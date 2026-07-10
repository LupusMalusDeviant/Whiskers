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
    public void Contributes_the_four_automation_nav_entries()
    {
        var nav = new AgentModule().NavItems;
        Assert.Equal(new[] { "agent", "guardrails", "approvals", "ai-triggers" }, nav.Select(n => n.Href).ToArray());
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
        foreach (var href in new[] { "agent", "guardrails", "approvals", "ai-triggers" })
            Assert.DoesNotContain(pseudo.NavItems, n => n.Href == href);
        Assert.DoesNotContain(typeof(AgentTools), pseudo.McpToolTypes);
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
    public void ConfigureServices_registers_the_agent_graph_and_both_warmups()
    {
        var services = new ServiceCollection();
        new AgentModule().ConfigureServices(services, Config());

        // A representative slice of the moved registrations: the agent, a guardrail service, an approval
        // service, the AI-chat advisor, and the real trigger dispatcher (which wins over the Core no-op).
        Assert.Contains(services, d => d.ServiceType == typeof(Whiskers.Services.Agent.IAgentService));
        Assert.Contains(services, d => d.ServiceType == typeof(Whiskers.Services.Agent.Guardrails.IGuardrailStore));
        Assert.Contains(services, d => d.ServiceType == typeof(Whiskers.Services.Agent.Approvals.IApprovalCoordinator));
        Assert.Contains(services, d => d.ServiceType == typeof(Whiskers.Services.AiChat.IAiChatService));
        Assert.Contains(services, d => d.ServiceType == typeof(Whiskers.Services.Agent.Triggers.IAiTriggerDispatcher)
                                       && d.ImplementationType == typeof(Whiskers.Services.Agent.Triggers.AiTriggerDispatcher));

        // Both startup warm-ups (GuardrailStore 80 + AiTriggerStore 90) moved into the module.
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(Whiskers.Services.IInitializable)));
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
