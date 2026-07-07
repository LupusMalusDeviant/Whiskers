using ServerWatch.Models.Hetzner;

namespace ServerWatch.Services.Hetzner;

/// <summary>
/// Hetzner Cloud API client. Every call takes an explicit project token, because credentials are
/// configured per ServerWatch server (each may live in a different Hetzner project).
/// </summary>
public interface IHetznerService
{
    Task<bool> TestConnectionAsync(string token);

    Task<List<HetznerServer>> ListServersAsync(string token);
    Task<HetznerServer?> GetServerAsync(string token, long id);

    Task<HetznerAction?> PowerOnAsync(string token, long id);
    Task<HetznerAction?> ShutdownAsync(string token, long id);
    Task<HetznerAction?> RebootAsync(string token, long id);
    Task<HetznerAction?> ResetAsync(string token, long id);

    Task<HetznerActionResponse?> EnableRescueAsync(string token, long id);
    Task<HetznerAction?> DisableRescueAsync(string token, long id);

    Task<HetznerActionResponse?> CreateSnapshotAsync(string token, long id, string? description);
    Task<List<HetznerImage>> ListSnapshotsAsync(string token);
    Task<HetznerImage?> GetImageAsync(string token, long imageId);
    Task DeleteImageAsync(string token, long imageId);

    Task<HetznerAction?> EnableBackupsAsync(string token, long id);
    Task<HetznerAction?> DisableBackupsAsync(string token, long id);

    Task<List<HetznerServerType>> ListServerTypesAsync(string token);
    Task<HetznerAction?> ChangeServerTypeAsync(string token, long id, string serverType, bool upgradeDisk);

    Task<HetznerMetrics?> GetMetricsAsync(string token, long id, string type, DateTime start, DateTime end, int? step = null);
}
