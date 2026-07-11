using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.LogMonitor;

namespace Whiskers.Modules.LogMonitor;

/// <summary>Container-log search + the background log-pattern monitor (RoadToSAP Phase 1): the `/logs` page,
/// full-text/regex search (<c>ILogSearchService</c>), the hosted alert-rule monitor
/// (<c>ILogMonitorService</c>/<c>LogMonitorService</c>) and the log MCP tools. Registrations are moved
/// <b>verbatim</b> from Program.cs.
///
/// <c>ILogSearchService</c> has no Core consumer (only the /logs page and the MCP tools use it), so it moves
/// cleanly. <c>ILogMonitorService</c> is different: the Core AI-triggers page reads/creates log-alert rules
/// through it, so Core registers a <see cref="NoopLogMonitorService"/> default before the module loop — the
/// module's real <see cref="LogMonitorService"/> wins by last-registration when enabled, and the no-op keeps
/// the AI-triggers page working when the module is off (no log rules are persisted, nothing scans).</summary>
public sealed class LogMonitorModule : IWhiskersModule
{
    public string Id => "logmonitor";
    public string DisplayName => "Log-Suche";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    // The "logs" sidebar entry (moved verbatim from AllInOnePseudoModule); shown only while enabled.
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("logs", "Nav_LogSearch", Icons.Material.Filled.Search, "Übersicht", AppRole.Viewer, 40),
    };

    public IReadOnlyList<Type> McpToolTypes { get; } = new[] { typeof(LogTools) };

    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation, not a rewrite). The ILogMonitorService forwarder is
        // registered after Core's NoopLogMonitorService (the module loop runs after it), so it wins here.
        services.AddSingleton<ILogSearchService, LogSearchService>();
        services.AddSingletonWithInterfaceAndHostedService<LogMonitorService, ILogMonitorService>();
    }
}
