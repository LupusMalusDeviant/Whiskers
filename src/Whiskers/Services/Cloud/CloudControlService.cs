using Whiskers.Models;
using Whiskers.Models.Cloud;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Cloud.Providers;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

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
    private readonly IVaultService _vault;

    public CloudControlService(
        IServerConfigService serverConfig,
        IEnumerable<ICloudProvider> providers,
        ILogger<CloudControlService> logger,
        IVaultService vault)
    {
        _serverConfig = serverConfig;
        _vault = vault;
        _providers = providers.ToList();
        _hetznerExt = _providers.OfType<IHetznerExtensions>().FirstOrDefault();
        _logger = logger;
    }

    private ICloudProvider? ProviderFor(CloudProvider p) => _providers.FirstOrDefault(x => x.Provider == p);

    /// <summary>All Whiskers servers that have a cloud provider + key configured.</summary>
    public List<Whiskers.Models.ServerConfig> CloudServers()
        => _serverConfig.GetServers()
            .Where(c => c.CloudProvider != CloudProvider.None && c.HasCloudKey())
            .ToList();

    /// <summary>The server's cloud API key, from the vault (R1-29) or a not yet migrated plaintext field.</summary>
    private string? KeyOf(Whiskers.Models.ServerConfig sw) => CloudCredentials.KeyOf(sw, _vault);

    public Whiskers.Models.ServerConfig? ResolveWhiskers(string idOrName)
        => _serverConfig.GetServers().FirstOrDefault(c => c.Id == idOrName)
           ?? _serverConfig.GetServers().FirstOrDefault(c => c.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves the provider VM/server matching this Whiskers server by public IP.</summary>
    public async Task<CloudServerInfo?> ResolveAsync(Whiskers.Models.ServerConfig sw)
    {
        if (sw.CloudProvider == CloudProvider.None || !sw.HasCloudKey())
            return null;
        var token = KeyOf(sw);
        if (string.IsNullOrWhiteSpace(token)) return null;
        var provider = ProviderFor(sw.CloudProvider);
        return provider == null ? null : await provider.ResolveAsync(sw, token);
    }

    public async Task<List<CloudServerInfo>> ListAllAsync()
    {
        // Group by (provider, token) so an account with N Whiskers servers is listed ONCE instead of N times
        // (fewer API calls; kinder to the Hetzner rate limit). A no-match is omitted; an account-listing failure
        // marks all of that account's servers as errored — same observable result as the old per-server loop.
        // A key the vault cannot give (locked, missing) marks only that server as errored.
        var keyed = new List<(Whiskers.Models.ServerConfig Server, string Key)>();
        var unreadable = new List<CloudServerInfo>();
        foreach (var sw in CloudServers())
        {
            try
            {
                keyed.Add((sw, KeyOf(sw)!));
            }
            catch (InvalidOperationException ex)
            {
                unreadable.Add(new CloudServerInfo
                {
                    WhiskersId = sw.Id, WhiskersName = sw.Name, Provider = sw.CloudProvider,
                    Name = sw.Name, Status = "error", Note = ex.Message
                });
            }
        }

        var tasks = keyed
            .GroupBy(k => (k.Server.CloudProvider, k.Key), k => k.Server)
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

        return (await Task.WhenAll(tasks)).SelectMany(r => r).Concat(unreadable).ToList();
    }

    // ─────────────────────────── Actions ───────────────────────────

    public Task<string> PowerOnAsync(string idOrName) => DispatchAsync(idOrName, (p, info, token) => p.PowerOnAsync(info, token));
    public Task<string> ShutdownAsync(string idOrName) => DispatchAsync(idOrName, (p, info, token) => p.ShutdownAsync(info, token));
    public Task<string> RebootAsync(string idOrName) => DispatchAsync(idOrName, (p, info, token) => p.RebootAsync(info, token));
    public Task<string> HardResetAsync(string idOrName) => DispatchAsync(idOrName, (p, info, token) => p.HardResetAsync(info, token));

    public async Task<string> CreateSnapshotAsync(string idOrName, string? description)
    {
        var (_, info, provider, token) = await ContextAsync(idOrName);
        return await provider.CreateSnapshotAsync(info, token, description);
    }

    public async Task<string> MetricsAsync(string idOrName, string type)
    {
        var (_, info, provider, token) = await ContextAsync(idOrName);
        return await provider.MetricsAsync(info, token, type);
    }

    /// <summary>For Hetzner-only tools: returns the per-server token + resolved Hetzner server.</summary>
    public async Task<(string token, HetznerServer server)?> HetznerContextAsync(string idOrName)
    {
        var sw = ResolveWhiskers(idOrName);
        if (sw == null || sw.CloudProvider != CloudProvider.Hetzner || !sw.HasCloudKey() || _hetznerExt == null)
            return null;
        var token = KeyOf(sw);
        if (string.IsNullOrWhiteSpace(token)) return null;
        var info = await ResolveAsync(sw);
        if (info == null) return null;
        var full = await _hetznerExt.GetServerAsync(token, info.CloudId);
        return full == null ? null : (token, full);
    }

    // ─────────────────────────── Internals ───────────────────────────

    private async Task<(Whiskers.Models.ServerConfig sw, CloudServerInfo info, ICloudProvider provider, string token)> ContextAsync(string idOrName)
    {
        var sw = ResolveWhiskers(idOrName)
                 ?? throw new InvalidOperationException($"Whiskers server not found: {idOrName}");
        if (sw.CloudProvider == CloudProvider.None || !sw.HasCloudKey())
            throw new InvalidOperationException($"No cloud provider/API key configured for '{sw.Name}'.");
        var token = KeyOf(sw)
                    ?? throw new InvalidOperationException($"No cloud provider/API key configured for '{sw.Name}'.");
        var provider = ProviderFor(sw.CloudProvider)
                       ?? throw new InvalidOperationException($"No provider registered for '{sw.CloudProvider}'.");
        var info = await provider.ResolveAsync(sw, token)
                   ?? throw new InvalidOperationException($"No matching {sw.CloudProvider} server found for '{sw.Name}' (IP match failed).");
        return (sw, info, provider, token);
    }

    // Power ops share the same shape: resolve context, run the provider op, append the weak-resolution note
    // exactly where the former inline dispatch did (power ops + hard reset; snapshot/metrics carry no note).
    private async Task<string> DispatchAsync(string idOrName, Func<ICloudProvider, CloudServerInfo, string, Task<string>> op)
    {
        var (_, info, provider, token) = await ContextAsync(idOrName);
        var noteSuffix = info.Note != null ? $"\n⚠️ {info.Note}" : "";
        return await op(provider, info, token) + noteSuffix;
    }
}
