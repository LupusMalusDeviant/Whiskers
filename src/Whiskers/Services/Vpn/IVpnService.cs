using Microsoft.Extensions.Options;
using Whiskers.Services.Vpn.Providers;

namespace Whiskers.Services.Vpn;

/// <summary>Resolves and exposes the configured (active) <see cref="IVpnProvider"/>.</summary>
public interface IVpnService
{
    /// <summary>The provider selected by <see cref="VpnSettings.Provider"/> (Noop if unknown/none).</summary>
    IVpnProvider Active { get; }

    /// <summary>All registered providers (for a UI selector).</summary>
    IReadOnlyList<IVpnProvider> Providers { get; }

    Task<VpnStatus> GetStatusAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public class VpnService : IVpnService
{
    private readonly ILogger<VpnService> _logger;

    public VpnService(IEnumerable<IVpnProvider> providers, IOptions<VpnSettings> settings, ILogger<VpnService> logger)
    {
        _logger = logger;
        Providers = providers.ToList();

        var wanted = settings.Value.Provider?.Trim();
        Active = Providers.FirstOrDefault(p => string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase))
                 ?? Providers.FirstOrDefault(p => p is NoopVpnProvider)
                 ?? Providers.First();

        if (!string.IsNullOrWhiteSpace(wanted) && !string.Equals(Active.Id, wanted, StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("[vpn] unknown provider '{Wanted}', falling back to '{Active}'", wanted, Active.Id);
    }

    public IVpnProvider Active { get; }
    public IReadOnlyList<IVpnProvider> Providers { get; }

    public Task<VpnStatus> GetStatusAsync(CancellationToken ct = default) => Active.GetStatusAsync(ct);
}
