using Whiskers.Models;
using Whiskers.Models.Cloud;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Hetzner;

namespace Whiskers.Services.Cloud.Providers;

/// <summary>The Hetzner provider (C10): resolves a Whiskers server to its Hetzner VM, runs the agnostic
/// power/snapshot/metric ops, and implements <see cref="IHetznerExtensions"/> for the Hetzner-only
/// capabilities. Delegates the HTTP work to <see cref="IHetznerService"/> (CancellationTokens threaded
/// through, OPT-12). The mapping + result messages are moved <b>verbatim</b> from the former inline
/// CloudControlService dispatch.</summary>
public sealed class HetznerCloudProvider : ICloudProvider, IHetznerExtensions
{
    private readonly IHetznerService _hetzner;
    public HetznerCloudProvider(IHetznerService hetzner) => _hetzner = hetzner;

    public CloudProvider Provider => CloudProvider.Hetzner;
    public string DisplayName => "Hetzner";

    // Surfaced when a destructive/power op resolved its target only by name (the IP match failed) — the
    // SshHost is often a mesh/Tailscale address ≠ the cloud public IP, so name-match is easy to get wrong.
    private const string NameMatchNote = "per Namensabgleich aufgelöst (IP-Abgleich fehlgeschlagen — Ziel ggf. nicht eindeutig)";

    private static string? HostOf(Whiskers.Models.ServerConfig c)
        => !string.IsNullOrWhiteSpace(c.SshHost) ? c.SshHost : c.TcpHost;

    public Task<bool> TestConnectionAsync(string token, CancellationToken ct = default)
        => _hetzner.TestConnectionAsync(token, ct);

    public async Task<List<CloudServerInfo>> ListAndMapAsync(IReadOnlyList<Whiskers.Models.ServerConfig> accountServers, string token, CancellationToken ct = default)
    {
        var servers = await _hetzner.ListServersAsync(token, ct);
        var results = new List<CloudServerInfo>();
        foreach (var sw in accountServers)
            if (Map(sw, servers) is { } info) results.Add(info);
        return results;
    }

    public async Task<CloudServerInfo?> ResolveAsync(Whiskers.Models.ServerConfig sw, string token, CancellationToken ct = default)
        => Map(sw, await _hetzner.ListServersAsync(token, ct));

    // Match a Whiskers server to a VM in a pre-fetched account listing (public IP, then name) and project it.
    // Public + static so the destructive-op target resolution is unit-testable without HTTP (C10 safety net).
    public static CloudServerInfo? Map(Whiskers.Models.ServerConfig sw, List<HetznerServer> servers)
    {
        var ip = HostOf(sw);
        var byIp = servers.FirstOrDefault(s => s.Ipv4 != null && s.Ipv4 == ip);
        var match = byIp ?? servers.FirstOrDefault(s => s.Name.Equals(sw.Name, StringComparison.OrdinalIgnoreCase));
        if (match == null) return null;
        return new CloudServerInfo
        {
            WhiskersId = sw.Id,
            WhiskersName = sw.Name,
            Provider = CloudProvider.Hetzner,
            CloudId = match.Id,
            Name = match.Name,
            Status = match.Status,
            Ipv4 = match.Ipv4,
            Type = match.ServerType?.Name,
            Location = match.Datacenter?.Location?.City ?? match.Datacenter?.Location?.Name,
            TrafficPercent = match.TrafficUsedPercent,
            BackupsEnabled = match.BackupsEnabled,
            Note = byIp == null ? NameMatchNote : null
        };
    }

    // Actions — messages byte-identical to the former CloudControlService dispatch.
    public async Task<string> PowerOnAsync(CloudServerInfo target, string token, CancellationToken ct = default)
    {
        var a = await _hetzner.PowerOnAsync(token, target.CloudId, ct);
        return $"{target.Name} ({target.Provider}): einschalten. (Aktion: {a?.Status ?? "ausgelöst"})";
    }

    public async Task<string> ShutdownAsync(CloudServerInfo target, string token, CancellationToken ct = default)
    {
        var a = await _hetzner.ShutdownAsync(token, target.CloudId, ct);
        return $"{target.Name} ({target.Provider}): herunterfahren. (Aktion: {a?.Status ?? "ausgelöst"})";
    }

    public async Task<string> RebootAsync(CloudServerInfo target, string token, CancellationToken ct = default)
    {
        var a = await _hetzner.RebootAsync(token, target.CloudId, ct);
        return $"{target.Name} ({target.Provider}): neu starten. (Aktion: {a?.Status ?? "ausgelöst"})";
    }

    public async Task<string> HardResetAsync(CloudServerInfo target, string token, CancellationToken ct = default)
    {
        var a = await _hetzner.ResetAsync(token, target.CloudId, ct);
        return $"{target.Name} ({target.Provider}): hart zurücksetzen. (Aktion: {a?.Status ?? "ausgelöst"})";
    }

    public async Task<string> CreateSnapshotAsync(CloudServerInfo target, string token, string? description, CancellationToken ct = default)
    {
        var resp = await _hetzner.CreateSnapshotAsync(token, target.CloudId, description, ct);
        return $"{target.Name}: Snapshot wird erstellt (Image #{resp?.Image?.Id}).";
    }

    public async Task<string> MetricsAsync(CloudServerInfo target, string token, string type, CancellationToken ct = default)
    {
        var end = DateTime.UtcNow;
        var m = await _hetzner.GetMetricsAsync(token, target.CloudId, type, end.AddMinutes(-30), end, 60, ct);
        if (m == null || m.TimeSeries.Count == 0) return $"Keine {type}-Metriken für {target.Name}.";
        var lines = m.TimeSeries.Select(kv => $"- {kv.Key}: {(kv.Value.Latest?.ToString("0.###") ?? "-")}");
        return $"{type}-Metriken für {target.Name} (letzte Werte):\n{string.Join('\n', lines)}";
    }

    // ── IHetznerExtensions ── delegate to the client, threading the CancellationToken through (OPT-12).
    public Task<HetznerServer?> GetServerAsync(string token, long id, CancellationToken ct = default) => _hetzner.GetServerAsync(token, id, ct);
    public Task<HetznerActionResponse?> EnableRescueAsync(string token, long id, CancellationToken ct = default) => _hetzner.EnableRescueAsync(token, id, ct);
    public Task<HetznerAction?> DisableRescueAsync(string token, long id, CancellationToken ct = default) => _hetzner.DisableRescueAsync(token, id, ct);
    public Task<HetznerAction?> EnableBackupsAsync(string token, long id, CancellationToken ct = default) => _hetzner.EnableBackupsAsync(token, id, ct);
    public Task<HetznerAction?> DisableBackupsAsync(string token, long id, CancellationToken ct = default) => _hetzner.DisableBackupsAsync(token, id, ct);
    public Task<HetznerAction?> ChangeServerTypeAsync(string token, long id, string serverType, bool upgradeDisk, CancellationToken ct = default) => _hetzner.ChangeServerTypeAsync(token, id, serverType, upgradeDisk, ct);
    public Task<List<HetznerImage>> ListSnapshotsAsync(string token, CancellationToken ct = default) => _hetzner.ListSnapshotsAsync(token, ct);
    public Task<HetznerImage?> GetImageAsync(string token, long imageId, CancellationToken ct = default) => _hetzner.GetImageAsync(token, imageId, ct);
    public Task DeleteImageAsync(string token, long imageId, CancellationToken ct = default) => _hetzner.DeleteImageAsync(token, imageId, ct);
}
