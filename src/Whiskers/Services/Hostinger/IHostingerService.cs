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

    // R1-48: the provider calls of a decided cloud operation, guarded like IHetznerService's.
    [Whiskers.Services.Mutations.MutatingOperation("cloud.power_on")]
    Task StartAsync(string token, long id, CancellationToken ct = default);
    [Whiskers.Services.Mutations.MutatingOperation("cloud.shutdown")]
    Task StopAsync(string token, long id, CancellationToken ct = default);
    [Whiskers.Services.Mutations.MutatingOperation("cloud.reboot")]
    Task RestartAsync(string token, long id, CancellationToken ct = default);

    [Whiskers.Services.Mutations.MutatingOperation("cloud.snapshot_create")]
    Task CreateSnapshotAsync(string token, long id, CancellationToken ct = default);

    /// <summary>Raw metrics JSON (field-level parsing pending live verification of the response shape).</summary>
    Task<string> GetMetricsRawAsync(string token, long id, CancellationToken ct = default);
}
