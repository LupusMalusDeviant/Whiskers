using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;

namespace Whiskers.Modules.Agent;

/// <summary>The acting AI agent + AI chat (RoadToSAP Phase 1 §3.8): the multi-provider agent, the AI-chat advisor
/// widget, and the event-driven AI triggers. The guardrails and the approval store it obeys are <b>core</b> since
/// ADR-0005 step 2.1 (PC-D8) — they guard direct MCP calls too, so they must not switch off with this module. All registrations are moved <b>verbatim</b> from Program.cs
/// (relocation, not a rewrite): the guardrail/approval/tool-invoker boundary is unchanged, only where it is
/// wired.
///
/// Disabling it (<c>Features:agent:Enabled=false</c>) removes the whole surface at once: the agent/AI-chat
/// services are never registered, the <c>agent</c>/<c>ai-operations</c>/<c>ai-triggers</c> pages
/// show the "disabled" notice (their interactive views live behind <c>ModuleGuard</c> so their @inject never
/// runs), the global <c>&lt;AiChat/&gt;</c> widget is gated out in <c>MainLayout</c>, and the
/// <see cref="AgentTools"/> MCP tool (<c>instruct_agent</c>) drops off the MCP surface.
///
/// Two soft dependencies survive with the module off, both handled in Core, not via <c>DependsOn</c>:
/// (1) the notification composite lazily resolves <c>IAiTriggerDispatcher</c> → Core registers a
/// <c>NoopAiTriggerDispatcher</c> before the module loop; (2) the <c>agent-history</c> page reads the Core
/// <c>IMcpCallLogStore</c> (MCP call observability, independent of the agent), so it stays in Core.</summary>
public sealed class AgentModule : IWhiskersModule
{
    public string Id => "agent";
    public string DisplayName => "Agent";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    // The agent sidebar entries (moved verbatim from AllInOnePseudoModule); shown only while enabled.
    // "agent-history" stays in Core — it is MCP-call observability, not the acting agent.
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("agent",         "Nav_Agent",         Icons.Material.Filled.SmartToy,   "Automatisierung", AppRole.Viewer, 340),
        // WP-06: guided "Secure AI Operations" onboarding — admin-only (it can create a key + activate a preset).
        new NavItem("ai-operations", "Nav_AiOperations",  Icons.Material.Filled.Rocket,     "Automatisierung", AppRole.Admin,  345),
        new NavItem("ai-triggers", "Nav_AiTriggers", Icons.Material.Filled.Bolt,      "Automatisierung", AppRole.Viewer, 370),
    };

    public IReadOnlyList<Type> McpToolTypes { get; } = new[] { typeof(AgentTools) };

    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation, not a rewrite). RoadToSAP Phase 1 §3.8.

        // AI Chat
        services.Configure<Whiskers.Configuration.AiChatSettings>(config.GetSection(Whiskers.Configuration.AiChatSettings.SectionName));
        services.AddHttpClient<Whiskers.Services.AiChat.AiChatService>();
        services.AddSingleton<Whiskers.Services.AiChat.AiChatService>();
        services.AddSingleton<Whiskers.Services.AiChat.IAiChatService>(sp => sp.GetRequiredService<Whiskers.Services.AiChat.AiChatService>());
        services.AddSingleton<Whiskers.Services.AiChat.IChatHistoryStore, Whiskers.Services.AiChat.ChatHistoryStore>();

        // Agent (acting multi-provider agent with inescapable guardrails)
        services.Configure<Whiskers.Configuration.AgentSettings>(
            config.GetSection(Whiskers.Configuration.AgentSettings.SectionName));
        // Guardrail engine + store, tool registry, approval store + gate, direct-MCP guard and principal resolver
        // are core since ADR-0005 step 2.1 (PC-D8): see Startup/WhiskersMutationGatewayExtensions.AddGuardrailPolicy.
        services.AddSingleton<Whiskers.Services.Agent.Providers.IAgentProviderFactory,
            Whiskers.Services.Agent.Providers.AgentProviderFactory>();
        services.AddSingleton<Whiskers.Services.Agent.IAgentToolCatalog,
            Whiskers.Services.Agent.AgentToolCatalog>();
        services.AddSingleton<Whiskers.Services.Agent.IAgentToolInvoker,
            Whiskers.Services.Agent.AgentToolInvoker>();
        services.AddSingleton<Whiskers.Services.Agent.Approvals.IApprovalCoordinator,
            Whiskers.Services.Agent.Approvals.ApprovalCoordinator>();
        services.AddSingleton<Whiskers.Services.Agent.Chat.IChatWidgetParser,
            Whiskers.Services.Agent.Chat.ChatWidgetParser>();
        services.AddSingleton<Whiskers.Services.Agent.IAgentService,
            Whiskers.Services.Agent.AgentService>();
        services.AddSingleton<Whiskers.Services.Agent.IClaudeCodeRuntime,
            Whiskers.Services.Agent.ClaudeCodeRuntime>();
        services.AddSingleton<Whiskers.Services.Agent.IAgentTranscriptStore,
            Whiskers.Services.Agent.AgentTranscriptStore>();
        services.AddSingleton<Whiskers.Services.Agent.IAgentSettingsStore,
            Whiskers.Services.Agent.AgentSettingsStore>();

        // AI triggers (autonomous agent runs on events)
        services.AddSingletonWithInterface<Whiskers.Services.Agent.Triggers.AiTriggerStore, Whiskers.Services.Agent.Triggers.IAiTriggerStore>();
        services.AddSingleton<Whiskers.Services.Agent.Triggers.IAiTriggerDispatcher,
            Whiskers.Services.Agent.Triggers.AiTriggerDispatcher>();
        // R1-13: what a trigger may change, asked by the gateway for each of its mutations (PC-D6, PC-D7).
        services.AddSingleton<Whiskers.Services.Agent.Triggers.AiTriggerAuthority>();
        services.AddSingleton<Whiskers.Services.Agent.Triggers.IAiTriggerAuthority>(sp =>
            sp.GetRequiredService<Whiskers.Services.Agent.Triggers.AiTriggerAuthority>());
        services.AddSingleton<Whiskers.Services.Mutations.ITriggerAuthority>(sp =>
            sp.GetRequiredService<Whiskers.Services.Agent.Triggers.AiTriggerAuthority>());

        // Startup warm-up (only when the module is enabled). Order 90. The GuardrailStore warm-up (80) is core.
        services.AddInitializable<Whiskers.Services.Agent.Triggers.AiTriggerStore>();
    }
}
