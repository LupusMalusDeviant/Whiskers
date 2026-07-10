using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Mcp.Tools;
using Whiskers.Models;

namespace Whiskers.Modules;

/// <summary>
/// Transitional "everything that isn't a real module yet" bucket (RoadToSAP Phase 1). It carries today's
/// full navigation and MCP tool set so the module pipeline (discovery → nav registry → MCP tools) can be
/// wired up <b>behaviour-neutrally</b> before any feature is extracted.
///
/// Its <see cref="ConfigureServices"/> is a no-op on purpose: the service registrations still live inline
/// in <c>Program.cs</c> and move out one module PR at a time (Terminal first), at which point the matching
/// nav/tool entries here are removed. This class is retired entirely once every feature is a real module.
/// Kept in sync with <c>Components/Layout/NavMenu.razor</c> until NavMenu reads the registry.
/// </summary>
public sealed class AllInOnePseudoModule : IWhiskersModule
{
    public string Id => "all-in-one";
    public string DisplayName => "Whiskers";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    // No-op: registrations remain inline in Program.cs until each feature is extracted into its own module.
    public void ConfigureServices(IServiceCollection services, IConfiguration config) { }

    // No-op: the 9 per-service warm-ups still run through the IInitializable loop in Program.cs.
    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    // Today's MCP tool set — mirrors the .WithTools<>() block in Program.cs. Entries move to their module
    // as each feature is extracted; disabling that module then removes its tools everywhere.
    public IReadOnlyList<Type> McpToolTypes { get; } = new[]
    {
        typeof(ContainerTools), typeof(ServerTools), typeof(MonitoringTools),
        typeof(NetworkTools), typeof(DatabaseTools),
        // Scheduler/Log/Cve tools → their modules; Cloud+Hetzner tools → Modules/CloudControl;
        // AgentTools (instruct_agent) → Modules/Agent (RoadToSAP Phase 1).
    };

    // Roles are permissive (Viewer): today's nav shows every link to everyone and gates on the page
    // itself (RoleGuard), so mirroring that keeps behaviour identical. LocKey holds the current German
    // label until F2 (i18n) replaces it with a real localization key.
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        // Übersicht
        new NavItem("",            "Dashboard",          Icons.Material.Filled.Dashboard,    "Übersicht",        AppRole.Viewer, 10),
        new NavItem("health",      "Statusberichte",     Icons.Material.Filled.MonitorHeart, "Übersicht",        AppRole.Viewer, 20),
        // "cves" → Modules/Cve, "logs" → Modules/LogMonitor (RoadToSAP Phase 1).
        new NavItem("graph",       "Topologie",          Icons.Material.Filled.Hub,          "Übersicht",        AppRole.Viewer, 50),
        new NavItem("diff",        "Vergleichen",        Icons.Material.Filled.Compare,      "Übersicht",        AppRole.Viewer, 60),
        new NavItem("notifications", "Benachrichtigungen", Icons.Material.Filled.Notifications, "Übersicht",     AppRole.Viewer, 70),
        new NavItem("audit-log",   "Audit-Protokoll",    Icons.Material.Filled.History,      "Übersicht",        AppRole.Viewer, 80),

        // Deployment ("deploy" + "apps" extracted to Modules/Deployment; "compose" stays Core — RoadToSAP Phase 1)
        new NavItem("compose",     "Compose Editor",     Icons.Material.Filled.EditNote,     "Deployment",       AppRole.Viewer, 120),

        // Infrastruktur
        new NavItem("servers",     "Server",             Icons.Material.Filled.Storage,      "Infrastruktur",    AppRole.Viewer, 210),
        // "cloud" extracted to Modules/CloudControl (RoadToSAP Phase 1).
        new NavItem("networks",    "Netzwerke",          Icons.Material.Filled.Hub,          "Infrastruktur",    AppRole.Viewer, 230),
        // "backups" extracted to Modules/VolumeBackups (RoadToSAP Phase 1).

        // Automatisierung  ("tasks" → Modules/Scheduler, "webhooks" → Modules/Webhooks; agent + guardrails +
        // approvals + ai-triggers → Modules/Agent — RoadToSAP Phase 1). "agent-history" stays Core: it reads
        // the Core IMcpCallLogStore (MCP-call observability), which is independent of the acting agent.
        new NavItem("agent-history", "Agent-History",    Icons.Material.Filled.Policy,       "Automatisierung",  AppRole.Viewer, 330),

        // Top-level (no group)
        new NavItem("settings",    "Einstellungen",      Icons.Material.Filled.Settings,     "",                 AppRole.Viewer, 900),
        new NavItem("help",        "Hilfe",              Icons.Material.Filled.MenuBook,     "",                 AppRole.Viewer, 910),
    };
}
