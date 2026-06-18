using ServerWatch.Models.Hostinger;

namespace ServerWatch.Services.Hostinger;

/// <summary>
/// Hostinger VPS API client. Token is supplied per call (credentials are per-server).
/// </summary>
public interface IHostingerService
{
    Task<bool> TestConnectionAsync(string token);

    Task<List<HostingerVm>> ListVmsAsync(string token);
    Task<HostingerVm?> GetVmAsync(string token, long id);

    Task StartAsync(string token, long id);
    Task StopAsync(string token, long id);
    Task RestartAsync(string token, long id);

    Task<HostingerSnapshot?> GetSnapshotAsync(string token, long id);
    Task CreateSnapshotAsync(string token, long id);
    Task DeleteSnapshotAsync(string token, long id);

    /// <summary>Raw metrics JSON (field-level parsing pending live verification of the response shape).</summary>
    Task<string> GetMetricsRawAsync(string token, long id);
}
