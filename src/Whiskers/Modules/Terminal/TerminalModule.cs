using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Services.Terminal;

namespace Whiskers.Modules.Terminal;

/// <summary>The interactive container/host shell (web terminal) — the first feature extracted into a real
/// module (RoadToSAP Phase 1 pilot). Registrations are moved <b>verbatim</b> from Program.cs. It has no
/// sidebar entry (the terminal is opened contextually from a container/server) and exposes no MCP tools;
/// its two pages are gated by <c>ModuleGuard</c> so a disabled terminal shows a clean notice.</summary>
public sealed class TerminalModule : IWhiskersModule
{
    public string Id => "terminal";
    public string DisplayName => "Terminal";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();
    public IReadOnlyList<NavItem> NavItems => Array.Empty<NavItem>();
    public IReadOnlyList<Type> McpToolTypes => Array.Empty<Type>();
    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation, not a rewrite).
        services.Configure<TerminalSettings>(config.GetSection(TerminalSettings.SectionName));
        services.AddSingleton<ITerminalSessionManager, TerminalSessionManager>();
    }
}
