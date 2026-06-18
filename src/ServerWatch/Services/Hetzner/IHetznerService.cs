using ServerWatch.Models.Hetzner;

namespace ServerWatch.Services.Hetzner;

public interface IHetznerService
{
    Task<bool> TestConnectionAsync();

    // Servers
    Task<List<HetznerServer>> ListServersAsync();
    Task<HetznerServer?> GetServerAsync(long id);

    // Power / lifecycle actions
    Task<HetznerAction?> PowerOnAsync(long id);
    Task<HetznerAction?> ShutdownAsync(long id);   // graceful ACPI shutdown
    Task<HetznerAction?> RebootAsync(long id);     // graceful reboot
    Task<HetznerAction?> ResetAsync(long id);      // hard reset (power cycle)

    // Rescue mode (returns root password on enable)
    Task<HetznerActionResponse?> EnableRescueAsync(long id);
    Task<HetznerAction?> DisableRescueAsync(long id);

    // Snapshots / backups
    Task<HetznerActionResponse?> CreateSnapshotAsync(long id, string? description);
    Task<List<HetznerImage>> ListSnapshotsAsync();
    Task DeleteImageAsync(long imageId);
    Task<HetznerAction?> EnableBackupsAsync(long id);
    Task<HetznerAction?> DisableBackupsAsync(long id);

    // Resize
    Task<List<HetznerServerType>> ListServerTypesAsync();
    Task<HetznerAction?> ChangeServerTypeAsync(long id, string serverType, bool upgradeDisk);

    // Metrics
    Task<HetznerMetrics?> GetMetricsAsync(long id, string type, DateTime start, DateTime end, int? step = null);

    // Firewalls & pricing
    Task<List<HetznerFirewall>> ListFirewallsAsync();
    Task<HetznerPricing?> GetPricingAsync();
}
