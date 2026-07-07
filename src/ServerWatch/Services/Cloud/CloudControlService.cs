using ServerWatch.Models;
using ServerWatch.Models.Cloud;
using ServerWatch.Models.Hetzner;
using ServerWatch.Services.Hetzner;
using ServerWatch.Services.Hostinger;
using ServerWatch.Services.ServerConfig;
using ServerWatch.Utils;

namespace ServerWatch.Services.Cloud;

/// <summary>
/// Provider-agnostic cloud control. Resolves a ServerWatch server's configured provider + per-server
/// API key, finds the matching VM/server in that account by public IP, and dispatches power/snapshot/
/// metric operations to the right provider client. Callers work in terms of ServerWatch servers and
/// never deal with provider-specific IDs.
/// </summary>
public class CloudControlService : ICloudControlService
{
    private readonly IServerConfigService _serverConfig;
    private readonly IHetznerService _hetzner;
    private readonly IHostingerService _hostinger;
    private readonly ILogger<CloudControlService> _logger;

    public CloudControlService(
        IServerConfigService serverConfig,
        IHetznerService hetzner,
        IHostingerService hostinger,
        ILogger<CloudControlService> logger)
    {
        _serverConfig = serverConfig;
        _hetzner = hetzner;
        _hostinger = hostinger;
        _logger = logger;
    }

    private static string? HostOf(ServerWatch.Models.ServerConfig c)
        => !string.IsNullOrWhiteSpace(c.SshHost) ? c.SshHost : c.TcpHost;

    /// <summary>All ServerWatch servers that have a cloud provider + key configured.</summary>
    public List<ServerWatch.Models.ServerConfig> CloudServers()
        => _serverConfig.GetServers()
            .Where(c => c.CloudProvider != CloudProvider.None && !string.IsNullOrWhiteSpace(c.CloudApiKey))
            .ToList();

    public ServerWatch.Models.ServerConfig? ResolveServerWatch(string idOrName)
        => _serverConfig.GetServers().FirstOrDefault(c => c.Id == idOrName)
           ?? _serverConfig.GetServers().FirstOrDefault(c => c.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    // Surfaced when a destructive/power op resolved its target only by name (the IP match failed) — the
    // SshHost is often a mesh/Tailscale address ≠ the cloud public IP, so name-match is easy to get wrong.
    private const string NameMatchNote = "per Namensabgleich aufgelöst (IP-Abgleich fehlgeschlagen — Ziel ggf. nicht eindeutig)";

    /// <summary>Resolves the provider VM/server matching this ServerWatch server by public IP.</summary>
    public async Task<CloudServerInfo?> ResolveAsync(ServerWatch.Models.ServerConfig sw)
    {
        var token = sw.CloudApiKey;
        if (string.IsNullOrWhiteSpace(token) || sw.CloudProvider == CloudProvider.None)
            return null;
        var ip = HostOf(sw);

        if (sw.CloudProvider == CloudProvider.Hetzner)
        {
            var servers = await _hetzner.ListServersAsync(token);
            var byIp = servers.FirstOrDefault(s => s.Ipv4 != null && s.Ipv4 == ip);
            var match = byIp ?? servers.FirstOrDefault(s => s.Name.Equals(sw.Name, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;
            return new CloudServerInfo
            {
                ServerWatchId = sw.Id,
                ServerWatchName = sw.Name,
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

        if (sw.CloudProvider == CloudProvider.Hostinger)
        {
            var vms = await _hostinger.ListVmsAsync(token);
            var byIp = vms.FirstOrDefault(v => v.PrimaryIpv4 != null && v.PrimaryIpv4 == ip);
            var match = byIp ?? vms.FirstOrDefault(v => (v.Hostname ?? "").Equals(sw.Name, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;
            return new CloudServerInfo
            {
                ServerWatchId = sw.Id,
                ServerWatchName = sw.Name,
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

        return null;
    }

    public async Task<List<CloudServerInfo>> ListAllAsync()
    {
        var tasks = CloudServers().Select(async sw =>
        {
            try { return await ResolveAsync(sw); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cloud resolve failed for {Server}", sw.Name);
                return new CloudServerInfo
                {
                    ServerWatchId = sw.Id, ServerWatchName = sw.Name, Provider = sw.CloudProvider,
                    Name = sw.Name, Status = "error", Note = ex.Message
                };
            }
        });
        return (await Task.WhenAll(tasks)).Where(x => x != null).Cast<CloudServerInfo>().ToList();
    }

    // ─────────────────────────── Actions ───────────────────────────

    public Task<string> PowerOnAsync(string idOrName) => DispatchAsync(idOrName, "einschalten",
        (t, id) => _hetzner.PowerOnAsync(t, id),
        (t, id) => _hostinger.StartAsync(t, id));

    public Task<string> ShutdownAsync(string idOrName) => DispatchAsync(idOrName, "herunterfahren",
        (t, id) => _hetzner.ShutdownAsync(t, id),
        (t, id) => _hostinger.StopAsync(t, id));

    public Task<string> RebootAsync(string idOrName) => DispatchAsync(idOrName, "neu starten",
        (t, id) => _hetzner.RebootAsync(t, id),
        (t, id) => _hostinger.RestartAsync(t, id));

    public async Task<string> HardResetAsync(string idOrName)
    {
        var (sw, info) = await ContextAsync(idOrName);
        var noteSuffix = info.Note != null ? $"\n⚠️ {info.Note}" : "";
        if (info.Provider == CloudProvider.Hetzner)
        {
            var a = await _hetzner.ResetAsync(sw.CloudApiKey!, info.CloudId);
            return $"{info.Name} ({info.Provider}): hart zurücksetzen. (Aktion: {a?.Status ?? "ausgelöst"}){noteSuffix}";
        }
        // Hostinger exposes no hard power-cycle — a graceful restart is the closest, but won't help a hung VM.
        await _hostinger.RestartAsync(sw.CloudApiKey!, info.CloudId);
        return $"{info.Name} ({info.Provider}): kein Hard-Reset verfügbar — stattdessen NEUSTART ausgelöst (bei hängendem System evtl. wirkungslos).{noteSuffix}";
    }

    public async Task<string> CreateSnapshotAsync(string idOrName, string? description)
    {
        var (sw, info) = await ContextAsync(idOrName);
        if (info.Provider == CloudProvider.Hetzner)
        {
            var resp = await _hetzner.CreateSnapshotAsync(sw.CloudApiKey!, info.CloudId, description);
            return $"{info.Name}: Snapshot wird erstellt (Image #{resp?.Image?.Id}).";
        }
        await _hostinger.CreateSnapshotAsync(sw.CloudApiKey!, info.CloudId);
        return $"{info.Name}: Snapshot wird erstellt (Hostinger hält nur EINEN Snapshot pro VM — der vorherige wird ersetzt).";
    }

    public async Task<string> MetricsAsync(string idOrName, string type)
    {
        var (sw, info) = await ContextAsync(idOrName);
        if (info.Provider == CloudProvider.Hetzner)
        {
            var end = DateTime.UtcNow;
            var m = await _hetzner.GetMetricsAsync(sw.CloudApiKey!, info.CloudId, type, end.AddMinutes(-30), end, 60);
            if (m == null || m.TimeSeries.Count == 0) return $"Keine {type}-Metriken für {info.Name}.";
            var lines = m.TimeSeries.Select(kv => $"- {kv.Key}: {(kv.Value.Latest?.ToString("0.###") ?? "-")}");
            return $"{type}-Metriken für {info.Name} (letzte Werte):\n{string.Join('\n', lines)}";
        }
        var raw = await _hostinger.GetMetricsRawAsync(sw.CloudApiKey!, info.CloudId);
        return $"Hostinger-Metriken für {info.Name} (Rohdaten):\n{ShellUtils.Truncate(raw, 4000)}";
    }

    /// <summary>For Hetzner-only tools: returns the per-server token + resolved Hetzner server.</summary>
    public async Task<(string token, HetznerServer server)?> HetznerContextAsync(string idOrName)
    {
        var sw = ResolveServerWatch(idOrName);
        if (sw == null || sw.CloudProvider != CloudProvider.Hetzner || string.IsNullOrWhiteSpace(sw.CloudApiKey))
            return null;
        var info = await ResolveAsync(sw);
        if (info == null) return null;
        var full = await _hetzner.GetServerAsync(sw.CloudApiKey, info.CloudId);
        return full == null ? null : (sw.CloudApiKey, full);
    }

    // ─────────────────────────── Internals ───────────────────────────

    private async Task<(ServerWatch.Models.ServerConfig sw, CloudServerInfo info)> ContextAsync(string idOrName)
    {
        var sw = ResolveServerWatch(idOrName)
                 ?? throw new InvalidOperationException($"ServerWatch-Server nicht gefunden: {idOrName}");
        if (sw.CloudProvider == CloudProvider.None || string.IsNullOrWhiteSpace(sw.CloudApiKey))
            throw new InvalidOperationException($"Für '{sw.Name}' ist kein Cloud-Provider/API-Key konfiguriert.");
        var info = await ResolveAsync(sw)
                   ?? throw new InvalidOperationException($"Kein passender {sw.CloudProvider}-Server zu '{sw.Name}' gefunden (IP-Abgleich fehlgeschlagen).");
        return (sw, info);
    }

    private async Task<string> DispatchAsync(
        string idOrName, string verb,
        Func<string, long, Task<HetznerAction?>> hetznerAction,
        Func<string, long, Task> hostingerAction)
    {
        var (sw, info) = await ContextAsync(idOrName);
        // Surface a weak (name-only) target resolution so a destructive op on the wrong VM is visible.
        var noteSuffix = info.Note != null ? $"\n⚠️ {info.Note}" : "";
        if (info.Provider == CloudProvider.Hetzner)
        {
            var a = await hetznerAction(sw.CloudApiKey!, info.CloudId);
            return $"{info.Name} ({info.Provider}): {verb}. (Aktion: {a?.Status ?? "ausgelöst"}){noteSuffix}";
        }
        await hostingerAction(sw.CloudApiKey!, info.CloudId);
        return $"{info.Name} ({info.Provider}): {verb}. (ausgelöst){noteSuffix}";
    }
}
