using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Services.Server;

namespace Whiskers.Modules.HostManagement;

/// <summary>Host management (RoadToSAP Phase 1): firewall (ufw), nginx sites, systemd units and TLS
/// certificates on a server — four related host-admin features bundled into one module because they share the
/// same Core <c>IHostCommandExecutor</c> and audience. Its four pages (<c>/firewall/{id}</c>,
/// <c>/nginx/{id}</c>, <c>/services/{id}</c>, <c>/ssl/{id}</c>) are reached from the Servers page, so it
/// contributes no top-level nav. Registrations are moved <b>verbatim</b> from Program.cs.
///
/// It exposes no MCP tools of its own: the firewall/nginx/systemd/ssl tools live in <c>ServerTools</c>, which
/// also carries core server ops (list_servers, get_server_info, execute_command) and therefore can't be split
/// under the byte-gleich rule — it stays in Core. Because <c>ServerTools</c> (and the pages) still consume the
/// four services, Core registers no-op defaults (<see cref="NoopFirewallService"/> et al.) before the module
/// loop; the real services registered here win by last-registration when enabled. With the module off, those
/// tools/pages answer "module disabled" cleanly rather than failing on an unresolved service.</summary>
public sealed class HostManagementModule : IWhiskersModule
{
    public string Id => "host-management";
    public string DisplayName => "Host-Verwaltung";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public IReadOnlyList<NavItem> NavItems => Array.Empty<NavItem>();
    public IReadOnlyList<Type> McpToolTypes => Array.Empty<Type>();
    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs. Registered after Core's Noop* host services (the module loop runs
        // after them), so the real services win here by last-registration.
        services.AddSingleton<IFirewallService, FirewallService>();
        services.AddSingleton<INginxService, NginxService>();
        services.AddSingleton<ISystemdService, SystemdService>();
        services.AddSingleton<ISslCertService, SslCertService>();
    }
}
