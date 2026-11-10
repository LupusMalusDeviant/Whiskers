namespace Whiskers.Tests;

/// <summary>
/// Contract 0012 (D10, work split): what is not migrated yet. Both lists may only shrink — a new entry fails
/// <see cref="ResolvedTargetFlowTests"/>, and so does an entry that no longer exists. Step 7 requires both empty.
/// </summary>
internal static class ResolvedTargetFlowPending
{
    /// <summary>Lambdas still handed to the old runner overloads (<c>Func&lt;CancellationToken, …&gt;</c>), as
    /// <c>&lt;owner&gt;.&lt;handler&gt; -&gt; &lt;capability&gt;</c>.</summary>
    public static readonly string[] Lambdas =
    [
    ];

    /// <summary><see cref="Whiskers.Services.Mutations.MutatingOperationAttribute"/> methods of an
    /// <see cref="Whiskers.Services.Mutations.TargetResolutionKind.Object"/> capability without exactly one
    /// <see cref="Whiskers.Services.Mutations.MutationTargetAttribute"/> parameter (D4 b, D10 item 3).</summary>
    public static readonly string[] UnmarkedOperations =
    [
        "Whiskers.Services.Agent.IAgentSettingsStore.SaveAsync (config.agent)",
        "Whiskers.Services.Auth.ILocalAccountService.ChangeOwnPasswordAsync (account.password_change)",
        "Whiskers.Services.Auth.IRoleService.RemoveRoleAsync (config.roles)",
        "Whiskers.Services.Auth.IRoleService.SaveRoleDataAsync (config.roles)",
        "Whiskers.Services.Auth.IRoleService.SetRoleAsync (config.roles)",
        "Whiskers.Services.Auth.IWhitelistService.SaveWhitelistAsync (config.whitelist)",
        "Whiskers.Services.AutoUpdate.IAutoUpdateService.RollbackAsync (container.rollback)",
        "Whiskers.Services.AutoUpdate.IAutoUpdateService.SetPolicyAsync (autoupdate.policy_set)",
        "Whiskers.Services.Cloud.Providers.ICloudProvider.CreateSnapshotAsync (cloud.snapshot_create)",
        "Whiskers.Services.Cloud.Providers.ICloudProvider.HardResetAsync (cloud.hard_reset)",
        "Whiskers.Services.Cloud.Providers.ICloudProvider.PowerOnAsync (cloud.power_on)",
        "Whiskers.Services.Cloud.Providers.ICloudProvider.RebootAsync (cloud.reboot)",
        "Whiskers.Services.Cloud.Providers.ICloudProvider.ShutdownAsync (cloud.shutdown)",
        "Whiskers.Services.Cloud.Providers.IHetznerExtensions.ChangeServerTypeAsync (hetzner.server_type_change)",
        "Whiskers.Services.Cloud.Providers.IHetznerExtensions.DeleteImageAsync (hetzner.snapshot_delete)",
        "Whiskers.Services.Cloud.Providers.IHetznerExtensions.DisableBackupsAsync (hetzner.backups_disable)",
        "Whiskers.Services.Cloud.Providers.IHetznerExtensions.DisableRescueAsync (hetzner.rescue_disable)",
        "Whiskers.Services.Cloud.Providers.IHetznerExtensions.EnableBackupsAsync (hetzner.backups_enable)",
        "Whiskers.Services.Cloud.Providers.IHetznerExtensions.EnableRescueAsync (hetzner.rescue_enable)",
        "Whiskers.Services.GitDeploy.IGitDeployService.DeployAsync (gitdeploy.deploy)",
        "Whiskers.Services.Guardrails.IGuardrailStore.SaveAsync (guardrails.save)",
        "Whiskers.Services.Guardrails.IGuardrailStore.SaveConfigAsync (guardrails.save)",
        "Whiskers.Services.Hetzner.IHetznerService.ChangeServerTypeAsync (hetzner.server_type_change)",
        "Whiskers.Services.Hetzner.IHetznerService.CreateSnapshotAsync (cloud.snapshot_create)",
        "Whiskers.Services.Hetzner.IHetznerService.DeleteImageAsync (hetzner.snapshot_delete)",
        "Whiskers.Services.Hetzner.IHetznerService.DisableBackupsAsync (hetzner.backups_disable)",
        "Whiskers.Services.Hetzner.IHetznerService.DisableRescueAsync (hetzner.rescue_disable)",
        "Whiskers.Services.Hetzner.IHetznerService.EnableBackupsAsync (hetzner.backups_enable)",
        "Whiskers.Services.Hetzner.IHetznerService.EnableRescueAsync (hetzner.rescue_enable)",
        "Whiskers.Services.Hetzner.IHetznerService.PowerOnAsync (cloud.power_on)",
        "Whiskers.Services.Hetzner.IHetznerService.RebootAsync (cloud.reboot)",
        "Whiskers.Services.Hetzner.IHetznerService.ResetAsync (cloud.hard_reset)",
        "Whiskers.Services.Hetzner.IHetznerService.ShutdownAsync (cloud.shutdown)",
        "Whiskers.Services.Hostinger.IHostingerService.CreateSnapshotAsync (cloud.snapshot_create)",
        "Whiskers.Services.Hostinger.IHostingerService.RestartAsync (cloud.reboot)",
        "Whiskers.Services.Hostinger.IHostingerService.StartAsync (cloud.power_on)",
        "Whiskers.Services.Hostinger.IHostingerService.StopAsync (cloud.shutdown)",
        "Whiskers.Services.Mcp.IMcpPermissionService.SavePermissionDataAsync (config.mcp_permissions)",
        "Whiskers.Services.Mcp.IMcpPermissionService.SetToolEnabledAsync (config.mcp_permissions)",
        "Whiskers.Services.Metrics.DiskHealth.IDiskHealthSettingsService.SaveAsync (config.save)",
        "Whiskers.Services.Metrics.IMetricRetentionService.SaveAsync (config.save)",
        "Whiskers.Services.Notifications.IContainerNotificationPrefsService.SavePrefsAsync (config.save)",
        "Whiskers.Services.Notifications.IOutboundExceptionService.SaveAsync (config.save)",
        "Whiskers.Services.Notifications.IQuietHoursService.SaveAsync (config.save)",
        "Whiskers.Services.Persistence.IAppSettingsStore.SaveSectionAsync (config.save)",
        "Whiskers.Services.Persistence.IAppSettingsStore.SaveSectionsAsync (config.save)",
        "Whiskers.Services.Persistence.IAppSettingsStore.UpdateSectionAsync (config.save)",
        "Whiskers.Services.Persistence.IAppSettingsStore.UpdateSectionsAsync (config.save)",
        "Whiskers.Services.Scheduler.ISchedulerService.UpdateTaskAsync (scheduler.update)",
        "Whiskers.Services.Terminal.ITerminalSessionManager.OpenContainerShellAsync (terminal.container_open)",
        "Whiskers.Services.Vault.IVaultService.DeleteSecretAsync (vault.secret_delete)",
    ];
}
