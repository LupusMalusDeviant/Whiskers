using Whiskers.Models;
using Whiskers.Models.Cloud;
using Whiskers.Models.Hostinger;
using Whiskers.Services.Hostinger;
using Whiskers.Utils;

namespace Whiskers.Services.Cloud.Providers;

/// <summary>The Hostinger provider (C10): resolves a Whiskers server to its Hostinger VM and runs the agnostic
/// power/snapshot/metric ops. Hostinger has no <see cref="IHetznerExtensions"/> equivalent (no rescue/backups/
/// server-type). Delegates the HTTP work to <see cref="IHostingerService"/> (CancellationTokens threaded
/// through, OPT-12). The mapping + result messages are moved <b>verbatim</b> from the former inline
/// CloudControlService dispatch — including the "no hard reset, restart instead" fallback and the "one
/// snapshot per VM" note.</summary>
public sealed class HostingerCloudProvider : ICloudProvider
{
    private readonly IHostingerService _hostinger;
    public HostingerCloudProvider(IHostingerService hostinger) => _hostinger = hostinger;

    public CloudProvider Provider => CloudProvider.Hostinger;
    public string DisplayName => "Hostinger";

    private const string NameMatchNote = "resolved by name match (IP match failed — target may not be unambiguous)";

    private static string? HostOf(Whiskers.Models.ServerConfig c)
        => !string.IsNullOrWhiteSpace(c.SshHost) ? c.SshHost : c.TcpHost;

    public Task<bool> TestConnectionAsync(string token, CancellationToken ct = default)
        => _hostinger.TestConnectionAsync(token, ct);

    public async Task<List<CloudServerInfo>> ListAndMapAsync(IReadOnlyList<Whiskers.Models.ServerConfig> accountServers, string token, CancellationToken ct = default)
    {
        var vms = await _hostinger.ListVmsAsync(token, ct);
        var results = new List<CloudServerInfo>();
        foreach (var sw in accountServers)
            if (Map(sw, vms) is { } info) results.Add(info);
        return results;
    }

    public async Task<CloudServerInfo?> ResolveAsync(Whiskers.Models.ServerConfig sw, string token, CancellationToken ct = default)
        => Map(sw, await _hostinger.ListVmsAsync(token, ct));

    // Public + static so the destructive-op target resolution is unit-testable without HTTP (C10 safety net).
    public static CloudServerInfo? Map(Whiskers.Models.ServerConfig sw, List<HostingerVm> vms)
    {
        var ip = HostOf(sw);
        var byIp = vms.FirstOrDefault(v => v.PrimaryIpv4 != null && v.PrimaryIpv4 == ip);
        var match = byIp ?? vms.FirstOrDefault(v => (v.Hostname ?? "").Equals(sw.Name, StringComparison.OrdinalIgnoreCase));
        if (match == null) return null;
        return new CloudServerInfo
        {
            WhiskersId = sw.Id,
            WhiskersName = sw.Name,
            Provider = CloudProvider.Hostinger,
            CloudId = match.Id,
            Name = match.Hostname ?? sw.Name,
            Status = match.State ?? "unknown",
            Ipv4 = match.PrimaryIpv4,
            Type = match.Plan ?? (match.Cpus.HasValue ? $"{match.Cpus} vCPU / {match.Memory} MB" : null),
            Location = null,
            Note = byIp == null ? NameMatchNote : null
        };
    }

    public async Task<string> PowerOnAsync(CloudServerInfo target, string token, CancellationToken ct = default)
    {
        await _hostinger.StartAsync(token, target.CloudId, ct);
        return $"{target.Name} ({target.Provider}): power on. (triggered)";
    }

    public async Task<string> ShutdownAsync(CloudServerInfo target, string token, CancellationToken ct = default)
    {
        await _hostinger.StopAsync(token, target.CloudId, ct);
        return $"{target.Name} ({target.Provider}): shut down. (triggered)";
    }

    public async Task<string> RebootAsync(CloudServerInfo target, string token, CancellationToken ct = default)
    {
        await _hostinger.RestartAsync(token, target.CloudId, ct);
        return $"{target.Name} ({target.Provider}): reboot. (triggered)";
    }

    public async Task<string> HardResetAsync(CloudServerInfo target, string token, CancellationToken ct = default)
    {
        // Hostinger exposes no hard power-cycle — a graceful restart is the closest, but won't help a hung VM.
        await _hostinger.RestartAsync(token, target.CloudId, ct);
        return $"{target.Name} ({target.Provider}): no hard reset available — triggered a RESTART instead (may have no effect on a hung system).";
    }

    public async Task<string> CreateSnapshotAsync(CloudServerInfo target, string token, string? description, CancellationToken ct = default)
    {
        await _hostinger.CreateSnapshotAsync(token, target.CloudId, ct);
        return $"{target.Name}: snapshot is being created (Hostinger keeps only ONE snapshot per VM — the previous one is replaced).";
    }

    public async Task<string> MetricsAsync(CloudServerInfo target, string token, string type, CancellationToken ct = default)
    {
        var raw = await _hostinger.GetMetricsRawAsync(token, target.CloudId, ct);
        return $"Hostinger metrics for {target.Name} (raw data):\n{ShellUtils.Truncate(raw, 4000)}";
    }
}
