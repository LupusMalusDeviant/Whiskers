using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Scheduler;

namespace Whiskers.Modules.Scheduler;

/// <summary>Cron-style scheduled tasks (RoadToSAP Phase 1): the background scheduler + task executor, the
/// <c>/tasks</c> page and the scheduler MCP tools. Registrations are moved <b>verbatim</b> from Program.cs.
///
/// This is the first extracted module to carry <b>both</b> a nav entry and MCP tools, so it exercises the
/// framework's <see cref="NavItems"/> and <see cref="McpToolTypes"/> paths for the first time. No Core service
/// consumes <c>ISchedulerService</c> (only the /tasks page and the MCP tools do), so no no-op default is
/// needed: when the module is off the hosted scheduler simply doesn't run, <c>/tasks</c> shows a "disabled"
/// notice (via <c>ModuleGuard</c>), and the scheduler tools drop off the MCP surface. (The in-process agent's
/// tool catalog still reflects them until the AgentToolRegistry→ModuleRegistry change — a separate roadmap
/// item; calling a scheduler tool with the module off fails cleanly at call time, never at boot.)</summary>
public sealed class SchedulerModule : IWhiskersModule
{
    public string Id => "scheduler";
    public string DisplayName => "Geplante Tasks";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    // The "tasks" sidebar entry (moved verbatim from AllInOnePseudoModule); shown only while enabled.
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("tasks", "Nav_ScheduledTasks", Icons.Material.Filled.Schedule, "Automatisierung", AppRole.Viewer, 310),
    };

    public IReadOnlyList<Type> McpToolTypes { get; } = new[] { typeof(SchedulerTools) };

    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation, not a rewrite).
        services.AddSingleton<ITaskExecutor, TaskExecutor>();
        services.AddSingletonWithInterfaceAndHostedService<SchedulerService, ISchedulerService>();
    }
}
