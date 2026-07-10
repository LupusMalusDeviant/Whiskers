using Whiskers.Models;
using Whiskers.Models.Cloud;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Cloud.Providers;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Cloud;

/// <summary>
/// Provider-agnostic cloud control. Resolves a Whiskers server's configured provider + per-server API key,
/// finds the matching VM/server in that account by public IP, and dispatches power/snapshot/metric operations
/// to the matching <see cref="ICloudProvider"/> (multi-registration, selected by
/// <see cref="Whiskers.Models.ServerConfig.CloudProvider"/> — no hard enum-switch per action; RoadToSAP §3.6 /
/// changeme C10). Callers work in terms of Whiskers servers and never deal with provider-specific IDs. The
/// per-target IP-match warning and every result message are unchanged; only the dispatch is now a seam.
/// </summary>
public class CloudControlService : ICloudControlService
{
    private readonly IServerConfigService _serverConfig;
    private readonly IReadOnlyList<ICloudProvider> _providers;
    private readonly IHetznerExtensions? _hetznerExt;
    private readonly ILogger<CloudControlService> _logger;

    public CloudControlService(
        IServerConfigService serverConfig,
        IEnumerable<ICloudProvider> providers,
        ILogger<CloudControlService> logger)
    {
        _serverConfig = serverConfig;
        _providers = providers.ToList();
        _hetznerExt = _providers.OfType<IHetznerExtensions>().FirstOrDefault();
        _logger = logger;
    }

    private ICloudProvider? ProviderFor(CloudProvider p) => _providers.FirstOrDefault(x => x.Provider == p);

    /// <summary>All Whiskers servers that have a cloud provider + key configured.</summary>
    public List<Whiskers.Models.ServerConfig> CloudServers()
        => _serverConfig.GetServers()
            .Where(c => c.CloudProvider != CloudProvider.None && !string.IsNullOrWhiteSpace(c.CloudApiKey))
            .ToList();

    public Whiskers.Models.ServerConfig? ResolveWhiskers(string idOrName)
        => _serverConfig.GetServers().FirstOrDefault(c => c.Id == idOrName)
           ?? _serverConfig.GetServers().FirstOrDefault(c => c.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves the provider VM/server matching this Whiskers server by public IP.</summary>
    public async Task<CloudServerInfo?> ResolveAsync(Whiskers.Models.ServerConfig sw)
    {
        var token = sw.CloudApiKey;
        if (string.IsNullOrWhiteSpace(token) || sw.CloudProvider == CloudProvider.None)
            return null;
        var provider = ProviderFor(sw.CloudProvider);
        return provider == null ? null : await provider.ResolveAsync(sw, token);
    }

    public async Task<List<CloudServerInfo>> ListAllAsync()
    {
        // Group by (provider, token) so an account with N Whiskers servers is listed ONCE instead of N times
        // (fewer API calls; kinder to the Hetzner rate limit). A no-match is omitted; an account-listing failure
        // marks all of that account's servers as errored — same observable result as the old per-server loop.
        var tasks = CloudServers()
            .GroupBy(sw => (sw.CloudProvider, sw.CloudApiKey))
            .Select(async account =>
            {
                var (provider, token) = account.Key;
                var results = new List<CloudServerInfo>();
                try
                {
                    var p = ProviderFor(provider);
                    if (p != null)
                        results.AddRange(await p.ListAndMapAsync(account.ToList(), token!));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cloud resolve failed for account ({Provider})", provider);
                    foreach (var sw in account)
                        results.Add(new CloudServerInfo
                        {
                            WhiskersId = sw.Id, WhiskersName = sw.Name, Provider = sw.CloudProvider,
                            Name = sw.Name, Status = "error", Note = ex.Message
                        });
                }
                return results;
            });

        return (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();
    }

    // ─────────────────────────── Actions ───────────────────────────

    public Task<string> PowerOnAsync(string idOrName) => DispatchAsync(idOrName, (p, info, token) => p.PowerOnAsync(info, token));
    public Task<string> ShutdownAsync(string idOrName) => DispatchAsync(idOrName, (p, info, token) => p.ShutdownAsync(info, token));
    public Task<string> RebootAsync(string idOrName) => DispatchAsync(idOrName, (p, info, token) => p.RebootAsync(info, token));
    public Task<string> HardResetAsync(string idOrName) => DispatchAsync(idOrName, (p, info, token) => p.HardResetAsync(info, token));

    public async Task<string> CreateSnapshotAsync(string idOrName, string? description)
    {
        var (sw, info, provider) = await ContextAsync(idOrName);
        return await provider.CreateSnapshotAsync(info, sw.CloudApiKey!, description);
    }

    public async Task<string> MetricsAsync(string idOrName, string type)
    {
        var (sw, info, provider) = await ContextAsync(idOrName);
        return await provider.MetricsAsync(info, sw.CloudApiKey!, type);
    }

    /// <summary>For Hetzner-only tools: returns the per-server token + resolved Hetzner server.</summary>
    public async Task<(string token, HetznerServer server)?> HetznerContextAsync(string idOrName)
    {
        var sw = ResolveWhiskers(idOrName);
        if (sw == null || sw.CloudProvider != CloudProvider.Hetzner || string.IsNullOrWhiteSpace(sw.CloudApiKey) || _hetznerExt == null)
            return null;
        var info = await ResolveAsync(sw);
        if (info == null) return null;
        var full = await _hetznerExt.GetServerAsync(sw.CloudApiKey, info.CloudId);
        return full == null ? null : (sw.CloudApiKey, full);
    }

    // ─────────────────────────── Internals ───────────────────────────

    private async Task<(Whiskers.Models.ServerConfig sw, CloudServerInfo info, ICloudProvider provider)> ContextAsync(string idOrName)
    {
        var sw = ResolveWhiskers(idOrName)
                 ?? throw new InvalidOperationException($"Whiskers-Server nicht gefunden: {idOrName}");
        if (sw.CloudProvider == CloudProvider.None || string.IsNullOrWhiteSpace(sw.CloudApiKey))
            throw new InvalidOperationException($"Für '{sw.Name}' ist kein Cloud-Provider/API-Key konfiguriert.");
        var provider = ProviderFor(sw.CloudProvider)
                       ?? throw new InvalidOperationException($"Kein Provider für '{sw.CloudProvider}' registriert.");
        var info = await provider.ResolveAsync(sw, sw.CloudApiKey)
                   ?? throw new InvalidOperationException($"Kein passender {sw.CloudProvider}-Server zu '{sw.Name}' gefunden (IP-Abgleich fehlgeschlagen).");
        return (sw, info, provider);
    }

    // Power ops share the same shape: resolve context, run the provider op, append the weak-resolution note
    // exactly where the former inline dispatch did (power ops + hard reset; snapshot/metrics carry no note).
    private async Task<string> DispatchAsync(string idOrName, Func<ICloudProvider, CloudServerInfo, string, Task<string>> op)
    {
        var (sw, info, provider) = await ContextAsync(idOrName);
        var noteSuffix = info.Note != null ? $"\n⚠️ {info.Note}" : "";
        return await op(provider, info, sw.CloudApiKey!) + noteSuffix;
    }
}
