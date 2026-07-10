using Whiskers.Models.Hetzner;

namespace Whiskers.Services.Hetzner;

/// <summary>
/// Hetzner Cloud API client. Every call takes an explicit project token, because credentials are
/// configured per Whiskers server (each may live in a different Hetzner project). Every call also takes a
/// <see cref="CancellationToken"/> (OPT-12) so a long provider call can be aborted with its request.
/// </summary>
public interface IHetznerService
{
    Task<bool> TestConnectionAsync(string token, CancellationToken ct = default);

    Task<List<HetznerServer>> ListServersAsync(string token, CancellationToken ct = default);
    Task<HetznerServer?> GetServerAsync(string token, long id, CancellationToken ct = default);

    Task<HetznerAction?> PowerOnAsync(string token, long id, CancellationToken ct = default);
    Task<HetznerAction?> ShutdownAsync(string token, long id, CancellationToken ct = default);
    Task<HetznerAction?> RebootAsync(string token, long id, CancellationToken ct = default);
    Task<HetznerAction?> ResetAsync(string token, long id, CancellationToken ct = default);

    Task<HetznerActionResponse?> EnableRescueAsync(string token, long id, CancellationToken ct = default);
    Task<HetznerAction?> DisableRescueAsync(string token, long id, CancellationToken ct = default);

    Task<HetznerActionResponse?> CreateSnapshotAsync(string token, long id, string? description, CancellationToken ct = default);
    Task<List<HetznerImage>> ListSnapshotsAsync(string token, CancellationToken ct = default);
    Task<HetznerImage?> GetImageAsync(string token, long imageId, CancellationToken ct = default);
    Task DeleteImageAsync(string token, long imageId, CancellationToken ct = default);

    Task<HetznerAction?> EnableBackupsAsync(string token, long id, CancellationToken ct = default);
    Task<HetznerAction?> DisableBackupsAsync(string token, long id, CancellationToken ct = default);

    Task<List<HetznerServerType>> ListServerTypesAsync(string token, CancellationToken ct = default);
    Task<HetznerAction?> ChangeServerTypeAsync(string token, long id, string serverType, bool upgradeDisk, CancellationToken ct = default);

    Task<HetznerMetrics?> GetMetricsAsync(string token, long id, string type, DateTime start, DateTime end, int? step = null, CancellationToken ct = default);
}
