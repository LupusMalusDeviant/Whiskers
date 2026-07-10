using Whiskers.Models.Hetzner;

namespace Whiskers.Services.Cloud.Providers;

/// <summary>Optional capability for Hetzner-specific operations that have no cross-provider equivalent — rescue
/// mode, automated backups, server-type change, and snapshot management (RoadToSAP §3.6 / changeme C10). Only
/// the Hetzner provider implements it; the Hetzner MCP tools resolve it through the seam (a capability check)
/// instead of taking <c>IHetznerService</c> directly, so the provider abstraction isn't bypassed. The
/// per-server token is still supplied per call (credentials are per Whiskers server).</summary>
public interface IHetznerExtensions
{
    Task<HetznerServer?> GetServerAsync(string token, long id, CancellationToken ct = default);

    Task<HetznerActionResponse?> EnableRescueAsync(string token, long id, CancellationToken ct = default);
    Task<HetznerAction?> DisableRescueAsync(string token, long id, CancellationToken ct = default);

    Task<HetznerAction?> EnableBackupsAsync(string token, long id, CancellationToken ct = default);
    Task<HetznerAction?> DisableBackupsAsync(string token, long id, CancellationToken ct = default);

    Task<HetznerAction?> ChangeServerTypeAsync(string token, long id, string serverType, bool upgradeDisk, CancellationToken ct = default);

    Task<List<HetznerImage>> ListSnapshotsAsync(string token, CancellationToken ct = default);
    Task<HetznerImage?> GetImageAsync(string token, long imageId, CancellationToken ct = default);
    Task DeleteImageAsync(string token, long imageId, CancellationToken ct = default);
}
