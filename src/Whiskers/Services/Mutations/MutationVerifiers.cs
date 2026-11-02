using Whiskers.Services.Backup;
using Whiskers.Services.Cloud;
using Whiskers.Services.Dns;
using Whiskers.Services.Docker;
using Whiskers.Services.Server;

namespace Whiskers.Services.Mutations;

/// <summary>Every registered read-back (R1-36, R1-37), one per capability whose <see cref="VerificationContracts"/> row
/// is <see cref="VerificationKind.ReadBack"/>. The app registers these; the tests build the same list.</summary>
public static class MutationVerifiers
{
    public static IReadOnlyList<Func<IServiceProvider, IMutationVerifier>> Factories { get; } =
    [
        // Containers (R1-36)
        sp => new ContainerStartVerifier(sp),
        sp => new ContainerStopVerifier(sp),
        sp => new ContainerRestartVerifier(sp),
        sp => new ContainerRemoveVerifier(sp),
        sp => new ContainerUpdateVerifier(sp),
        sp => new ContainerCreateVerifier(sp),
        sp => new ContainerCreateVerifier(sp, "app.deploy"),
        sp => new ContainerRollbackVerifier(sp),
        // Deployments and networks
        sp => new ComposeDeployVerifier(sp),
        sp => new NetworkCreateVerifier(sp),
        sp => new NetworkRemoveVerifier(sp),
        sp => new NetworkAttachVerifier(sp, connect: true),
        sp => new NetworkAttachVerifier(sp, connect: false),
        // Hosts
        sp => new FirewallRuleAddVerifier(sp),
        sp => new FirewallStatusVerifier(sp),
        sp => new NginxUpdateVerifier(sp),
        sp => new NginxReloadVerifier(sp),
        sp => new NginxSiteVerifier(sp, enable: true),
        sp => new NginxSiteVerifier(sp, enable: false),
        sp => new SystemdVerifier(sp),
        sp => new SslRenewVerifier(sp),
        // Backups
        sp => new DbBackupVerifier(sp),
        sp => new DbBackupDeleteVerifier(sp),
        sp => new SelfBackupCreateVerifier(sp),
        sp => new SelfBackupDeleteVerifier(sp),
        sp => new SelfBackupPruneVerifier(sp),
        sp => new VolumeBackupVerifier(sp),
        sp => new VolumeBackupDeleteVerifier(sp),
        // DNS
        sp => new DnsRecordVerifier(sp, set: true),
        sp => new DnsRecordVerifier(sp, set: false),
        // Cloud
        sp => new CloudPowerVerifier(sp, on: true),
        sp => new CloudPowerVerifier(sp, on: false),
        sp => new HetznerFlagVerifier(sp, "hetzner.rescue_enable"),
        sp => new HetznerFlagVerifier(sp, "hetzner.rescue_disable"),
        sp => new HetznerFlagVerifier(sp, "hetzner.backups_enable"),
        sp => new HetznerFlagVerifier(sp, "hetzner.backups_disable"),
        sp => new HetznerSnapshotDeleteVerifier(sp),
    ];
}
