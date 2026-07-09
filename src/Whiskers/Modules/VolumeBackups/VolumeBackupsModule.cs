using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Models;
using Whiskers.Services.Backup;

namespace Whiskers.Modules.VolumeBackups;

/// <summary>Docker-volume backup + restore (RoadToSAP Phase 1): the `/backups` page and the
/// <c>IVolumeBackupService</c>. Registration is moved <b>verbatim</b> from Program.cs. No MCP tools.
///
/// Cross-module coupling: the Scheduler module's <c>TaskExecutor</c> consumes <c>IVolumeBackupService</c> for
/// VolumeBackup tasks + their retention. Core registers a <see cref="NoopVolumeBackupService"/> default before
/// the module loop, so that graph resolves when Scheduler is on but this module is off; the real service wins
/// by last-registration when enabled. With the module off, a scheduled VolumeBackup task fails visibly (the
/// no-op throws) instead of silently — DbBackup tasks are unaffected (they use Core's database service).</summary>
public sealed class VolumeBackupsModule : IWhiskersModule
{
    public string Id => "volumebackups";
    public string DisplayName => "Backups";
    public bool EnabledByDefault => true;
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    // The "backups" sidebar entry (moved verbatim from AllInOnePseudoModule); shown only while enabled.
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("backups", "Backups", Icons.Material.Filled.Backup, "Infrastruktur", AppRole.Viewer, 240),
    };

    public IReadOnlyList<Type> McpToolTypes => Array.Empty<Type>();

    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // Moved verbatim from Program.cs (relocation, not a rewrite). Registered after Core's
        // NoopVolumeBackupService (the module loop runs after it), so the real service wins here.
        services.AddSingleton<IVolumeBackupService, VolumeBackupService>();
    }
}
