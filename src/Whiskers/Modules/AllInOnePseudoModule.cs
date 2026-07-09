using MudBlazor;
using Whiskers.Models;

namespace Whiskers.Modules;

/// <summary>
/// Phase-0 placeholder that feeds the <see cref="IModuleRegistry"/> today's hard-coded navigation, so
/// the registry can exist before any real module does. <c>NavMenu.razor</c> still renders its own
/// hard-coded links right now — this scaffold is inert. Phase 1 flips <c>NavMenu.razor</c> to read
/// <see cref="IModuleRegistry.NavItems"/> and retires the entries here as each feature becomes a real
/// module. Kept in sync with <c>Components/Layout/NavMenu.razor</c> until then.
/// </summary>
public static class AllInOnePseudoModule
{
    // Roles are permissive (Viewer): today's nav shows every link to everyone and gates on the page
    // itself (RoleGuard), so mirroring that keeps behaviour identical. LocKey holds the current German
    // label until F2 (i18n) replaces it with a real localization key.
    public static IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        // Übersicht
        new NavItem("",            "Dashboard",          Icons.Material.Filled.Dashboard,    "Übersicht",        AppRole.Viewer, 10),
        new NavItem("health",      "Statusberichte",     Icons.Material.Filled.MonitorHeart, "Übersicht",        AppRole.Viewer, 20),
        new NavItem("cves",        "CVE-Monitor",        Icons.Material.Filled.Security,     "Übersicht",        AppRole.Viewer, 30),
        new NavItem("logs",        "Log-Suche",          Icons.Material.Filled.Search,       "Übersicht",        AppRole.Viewer, 40),
        new NavItem("graph",       "Topologie",          Icons.Material.Filled.Hub,          "Übersicht",        AppRole.Viewer, 50),
        new NavItem("diff",        "Vergleichen",        Icons.Material.Filled.Compare,      "Übersicht",        AppRole.Viewer, 60),
        new NavItem("notifications", "Benachrichtigungen", Icons.Material.Filled.Notifications, "Übersicht",     AppRole.Viewer, 70),
        new NavItem("audit-log",   "Audit-Protokoll",    Icons.Material.Filled.History,      "Übersicht",        AppRole.Viewer, 80),

        // Deployment
        new NavItem("deploy",      "Bereitstellen",      Icons.Material.Filled.RocketLaunch, "Deployment",       AppRole.Viewer, 110),
        new NavItem("compose",     "Compose Editor",     Icons.Material.Filled.EditNote,     "Deployment",       AppRole.Viewer, 120),
        new NavItem("apps",        "App Store",          Icons.Material.Filled.Apps,         "Deployment",       AppRole.Viewer, 130),

        // Infrastruktur
        new NavItem("servers",     "Server",             Icons.Material.Filled.Storage,      "Infrastruktur",    AppRole.Viewer, 210),
        new NavItem("cloud",       "Cloud",              Icons.Material.Filled.CloudQueue,   "Infrastruktur",    AppRole.Viewer, 220),
        new NavItem("networks",    "Netzwerke",          Icons.Material.Filled.Hub,          "Infrastruktur",    AppRole.Viewer, 230),
        new NavItem("backups",     "Backups",            Icons.Material.Filled.Backup,       "Infrastruktur",    AppRole.Viewer, 240),

        // Automatisierung
        new NavItem("tasks",       "Geplante Tasks",     Icons.Material.Filled.Schedule,     "Automatisierung",  AppRole.Viewer, 310),
        new NavItem("webhooks",    "Webhooks",           Icons.Material.Filled.Webhook,      "Automatisierung",  AppRole.Viewer, 320),
        new NavItem("agent-history", "Agent-History",    Icons.Material.Filled.Policy,       "Automatisierung",  AppRole.Viewer, 330),
        new NavItem("agent",       "Agent",              Icons.Material.Filled.SmartToy,     "Automatisierung",  AppRole.Viewer, 340),
        new NavItem("guardrails",  "Guardrails",         Icons.Material.Filled.Shield,       "Automatisierung",  AppRole.Viewer, 350),
        new NavItem("approvals",   "Freigaben",          Icons.Material.Filled.Approval,     "Automatisierung",  AppRole.Viewer, 360),
        new NavItem("ai-triggers", "AI-Trigger",         Icons.Material.Filled.Bolt,         "Automatisierung",  AppRole.Viewer, 370),

        // Top-level (no group)
        new NavItem("settings",    "Einstellungen",      Icons.Material.Filled.Settings,     "",                 AppRole.Viewer, 900),
        new NavItem("help",        "Hilfe",              Icons.Material.Filled.MenuBook,     "",                 AppRole.Viewer, 910),
    };
}
