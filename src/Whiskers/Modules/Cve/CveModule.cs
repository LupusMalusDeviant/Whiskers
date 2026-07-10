using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Cve;

namespace Whiskers.Modules.Cve;

/// <summary>CVE monitoring (RoadToSAP Phase 1, §3 item 5): the background scanner (Trivy for containers + apt
/// for the host OS), the findings store, the CVE-age store and the `/cves` page + the CVE MCP tools.
/// Registrations are moved <b>verbatim</b> from Program.cs; <c>CveTools</c> is dedicated (not mixed), so it
/// moves with the module.
///
/// The findings store + monitor are consumed by Core pages (Dashboard, ContainerDetail, Settings), so Core
/// registers <see cref="NoopCveFindingsStore"/> + <see cref="NoopCveMonitorService"/> (+ a
/// <see cref="NoopCveAgeStore"/> for the inline-gated /cves page) before the module loop; the real services
/// win by last-registration when enabled. With the module off, those pages show no CVE data and the scanner
/// doesn't run.</summary>
public sealed class CveModule : IWhiskersModule
{
    public string Id => "cve";
    public string DisplayName => "CVE-Monitor";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("cves", "CVE-Monitor", Icons.Material.Filled.Security, "Übersicht", AppRole.Viewer, 30),
    };

    public IReadOnlyList<Type> McpToolTypes { get; } = new[] { typeof(CveTools) };

    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation). Registered after Core's Noop* CVE defaults (the module
        // loop runs after them), so the real services win here by last-registration.
        services.Configure<CveMonitorSettings>(config.GetSection(CveMonitorSettings.SectionName));
        services.AddSingleton<ICveFindingsStore, CveFindingsStore>();
        services.AddSingleton<ICveAgeStore, CveAgeStore>();
        services.AddSingleton<IOsCveScanner, OsCveScanner>();
        services.AddSingleton<ITrivyScanner, TrivyScanner>();
        // Singleton AND HostedService (same instance) so the UI can trigger manual scans.
        services.AddSingletonWithInterfaceAndHostedService<CveMonitorService, ICveMonitorService>();
    }
}
