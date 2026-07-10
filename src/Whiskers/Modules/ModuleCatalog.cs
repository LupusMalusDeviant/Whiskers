using Microsoft.Extensions.Configuration;

namespace Whiskers.Modules;

/// <summary>The single, explicit list of modules plus the enable/disable gate (RoadToSAP Phase 1).
/// Deliberately static — no assembly scanning — so the module set is greppable, code-reviewable and
/// trim/AOT-friendly. New modules are added to <see cref="All"/>; features are extracted into real modules
/// one PR at a time, shrinking <see cref="AllInOnePseudoModule"/> as they go.</summary>
public static class ModuleCatalog
{
    /// <summary>Every module the app knows about, in a deterministic order.</summary>
    private static IReadOnlyList<IWhiskersModule> All() => new IWhiskersModule[]
    {
        new AllInOnePseudoModule(),
        new Terminal.TerminalModule(),
        new Notifications.NotificationsModule(),
        new Scheduler.SchedulerModule(),
        new LogMonitor.LogMonitorModule(),
        new VolumeBackups.VolumeBackupsModule(),
        new Webhooks.WebhooksModule(),
        new HostManagement.HostManagementModule(),
        new Deployment.DeploymentModule(),
        new Cve.CveModule(),
        new CloudControl.CloudControlModule(),
        new ImageUpdate.ImageUpdateModule(),
        // Real modules are appended here as features are extracted (Terminal was the first pilot).
    };

    /// <summary>Returns the enabled modules. <c>Features:{Id}:Enabled</c> overrides
    /// <see cref="IWhiskersModule.EnabledByDefault"/>. Fails fast if an enabled module declares a
    /// <see cref="IWhiskersModule.DependsOn"/> on a disabled one.</summary>
    public static IReadOnlyList<IWhiskersModule> DiscoverEnabled(IConfiguration config)
    {
        bool IsEnabled(IWhiskersModule m) =>
            config.GetValue<bool?>($"Features:{m.Id}:Enabled") ?? m.EnabledByDefault;

        var enabled = All().Where(IsEnabled).ToList();
        var enabledIds = enabled.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var module in enabled)
            foreach (var dependency in module.DependsOn)
                if (!enabledIds.Contains(dependency))
                    throw new InvalidOperationException(
                        $"Module '{module.Id}' requires module '{dependency}', which is disabled. " +
                        $"Enable it (Features:{dependency}:Enabled=true) or disable '{module.Id}'.");

        return enabled;
    }
}
