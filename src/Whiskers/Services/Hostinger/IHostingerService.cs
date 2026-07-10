using Whiskers.Models.Hostinger;

namespace Whiskers.Services.Hostinger;

/// <summary>
/// Hostinger VPS API client. Token is supplied per call (credentials are per-server). Every call takes a
/// <see cref="CancellationToken"/> (OPT-12), threaded through to the underlying HttpClient.
/// </summary>
public interface IHostingerService
{
    Task<bool> TestConnectionAsync(string token, CancellationToken ct = default);

    Task<List<HostingerVm>> ListVmsAsync(string token, CancellationToken ct = default);
    Task<HostingerVm?> GetVmAsync(string token, long id, CancellationToken ct = default);

    Task StartAsync(string token, long id, CancellationToken ct = default);
    Task StopAsync(string token, long id, CancellationToken ct = default);
    Task RestartAsync(string token, long id, CancellationToken ct = default);

    Task<HostingerSnapshot?> GetSnapshotAsync(string token, long id, CancellationToken ct = default);
    Task CreateSnapshotAsync(string token, long id, CancellationToken ct = default);
    Task DeleteSnapshotAsync(string token, long id, CancellationToken ct = default);

    /// <summary>Raw metrics JSON (field-level parsing pending live verification of the response shape).</summary>
    Task<string> GetMetricsRawAsync(string token, long id, CancellationToken ct = default);
}
