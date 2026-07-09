using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Modules;
using Whiskers.Modules.VolumeBackups;
using Whiskers.Services.Backup;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 — the VolumeBackups module move. Covers the module metadata (nav "backups", no
/// MCP tools), the registration, the ModuleCatalog gate, and the soft-dependency no-op: the Scheduler module's
/// TaskExecutor consumes IVolumeBackupService, so a NoopVolumeBackupService default must resolve when this
/// module is off and be overridden by the real service when on. Crucially the no-op must NOT fake a successful
/// backup — its mutating operations throw so a scheduled VolumeBackup task records a visible failure.</summary>
public class VolumeBackupsModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // --- Module metadata ---------------------------------------------------------------------------------

    [Fact]
    public void Contributes_the_backups_nav_entry_and_no_tools()
    {
        var module = new VolumeBackupsModule();
        var nav = Assert.Single(module.NavItems);
        Assert.Equal("backups", nav.Href);
        Assert.Equal("Infrastruktur", nav.Group);
        Assert.Equal(240, nav.Order);
        Assert.Empty(module.McpToolTypes);
    }

    [Fact]
    public void Backups_nav_moved_out_of_the_pseudo_module()
    {
        Assert.DoesNotContain(new AllInOnePseudoModule().NavItems, n => n.Href == "backups");
    }

    // --- Registration + no-op gate -----------------------------------------------------------------------

    [Fact]
    public void ConfigureServices_registers_the_real_backup_service()
    {
        var services = new ServiceCollection();
        new VolumeBackupsModule().ConfigureServices(services, Config());
        Assert.Contains(services, d => d.ServiceType == typeof(IVolumeBackupService) && d.ImplementationType == typeof(VolumeBackupService));
    }

    [Fact]
    public void Disabled_module_keeps_the_noop_backup_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IVolumeBackupService, NoopVolumeBackupService>();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<NoopVolumeBackupService>(sp.GetRequiredService<IVolumeBackupService>());
    }

    [Fact]
    public void Enabled_module_overrides_the_noop_by_last_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IVolumeBackupService, NoopVolumeBackupService>();
        new VolumeBackupsModule().ConfigureServices(services, Config());
        var last = services.Last(d => d.ServiceType == typeof(IVolumeBackupService));
        Assert.Equal(typeof(VolumeBackupService), last.ImplementationType);
    }

    // --- No-op is data-safe (does not fake a successful backup) ------------------------------------------

    [Fact]
    public async Task Noop_backup_throws_instead_of_faking_success()
    {
        var noop = new NoopVolumeBackupService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => noop.BackupVolumeAsync("vol", "container"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => noop.RestoreVolumeAsync("backup-1"));
    }

    [Fact]
    public async Task Noop_reads_return_empty()
    {
        var noop = new NoopVolumeBackupService();
        Assert.Empty(await noop.ListBackupsAsync());
        Assert.Empty(await noop.ListVolumesAsync());
    }

    // --- Enable/disable gate -----------------------------------------------------------------------------

    [Fact]
    public void Enabled_by_default()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "volumebackups");
    }

    [Fact]
    public void Excluded_when_the_feature_flag_is_off()
    {
        var enabled = ModuleCatalog.DiscoverEnabled(Config(("Features:volumebackups:Enabled", "false")));
        Assert.DoesNotContain(enabled, m => m.Id == "volumebackups");
        Assert.Contains(enabled, m => m.Id == "scheduler");
    }
}
