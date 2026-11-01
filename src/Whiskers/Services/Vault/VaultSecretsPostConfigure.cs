using System.Reflection;
using Microsoft.Extensions.Options;

namespace Whiskers.Services.Vault;

/// <summary>
/// Materializes the vault-backed properties of a settings class (R1-30): where its configuration section names a vault
/// key (<c>&lt;Property&gt;Ref</c>), the property gets the value from the vault — read when the options are bound, so a
/// save (a new key in the file) applies live. Without a usable vault the property stays empty: the channel or client
/// counts as not configured and the log says why — fail closed, never a stale or plaintext value.
/// </summary>
public sealed class VaultSecretsPostConfigure<T> : IPostConfigureOptions<T> where T : class
{
    private readonly IConfiguration? _configuration;
    private readonly IVaultService? _vault;
    private readonly ILogger _logger;
    private readonly string _section;
    private readonly PropertyInfo[] _properties;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Warned = new();

    /// <param name="configuration">Null in a container without configuration (a module test): nothing names a vault key.</param>
    /// <param name="vault">Null without a vault service: a value kept in the vault is treated as not set.</param>
    public VaultSecretsPostConfigure(IConfiguration? configuration, IVaultService? vault, ILogger<VaultSecretsPostConfigure<T>> logger, string section)
    {
        _configuration = configuration;
        _vault = vault;
        _logger = logger;
        _section = section;
        _properties = VaultBackedSettings.Fields[section]
            .Select(p => typeof(T).GetProperty(p, BindingFlags.Public | BindingFlags.Instance)
                         ?? throw new InvalidOperationException($"{typeof(T).Name} has no property {p}."))
            .ToArray();
    }

    public void PostConfigure(string? name, T options)
    {
        if (_configuration is null) return;
        var section = _configuration.GetSection(_section);
        foreach (var property in _properties)
        {
            var vaultKey = section[VaultBackedSettings.RefName(property.Name)];
            if (string.IsNullOrWhiteSpace(vaultKey)) continue;   // plaintext from before, or set by the environment
            string? value = null;
            if (_vault is not { IsEnabled: true })
            {
                var state = _vault?.KeyState == VaultKeyState.Wrong ? "locked" : "disabled";
                // Options can be bound before the vault has started (they are bound again afterwards): say it once per state.
                if (Warned.TryAdd($"{_section}:{property.Name}:{state}", 0))
                    _logger.LogWarning("{Section}:{Property} is kept in the vault, and the vault is {State}; it is treated as not set",
                        _section, property.Name, state);
            }
            else if ((value = _vault.GetSecret(vaultKey)) is null && Warned.TryAdd($"{_section}:{property.Name}:missing:{vaultKey}", 0))
                _logger.LogWarning("{Section}:{Property} names a vault entry that is missing; it is treated as not set",
                    _section, property.Name);
            property.SetValue(options, value ?? "");
        }
    }
}

/// <summary>Drops the cached options of the vault-backed settings, so the next read binds them again — after the vault
/// has started (options bound before it read nothing from it) and after the start-up migration.</summary>
public interface IVaultBackedOptionsCache
{
    void Invalidate();
}

internal sealed class VaultBackedOptionsCache<T>(IOptionsMonitorCache<T> cache) : IVaultBackedOptionsCache where T : class
{
    public void Invalidate() => cache.TryRemove(Options.DefaultName);
}

public static class VaultSecretsServiceCollectionExtensions
{
    /// <summary>Reads the vault-backed properties of <typeparamref name="T"/> from the vault (R1-30). Register it before
    /// any other post-configuration of <typeparamref name="T"/> that needs the values (the log redaction's).</summary>
    public static IServiceCollection AddVaultBackedSecrets<T>(this IServiceCollection services, string section) where T : class
    {
        services.AddSingleton<IPostConfigureOptions<T>>(sp => new VaultSecretsPostConfigure<T>(
            sp.GetService<IConfiguration>(), sp.GetService<IVaultService>(),
            sp.GetService<ILogger<VaultSecretsPostConfigure<T>>>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VaultSecretsPostConfigure<T>>.Instance, section));
        services.AddSingleton<IVaultBackedOptionsCache>(sp => new VaultBackedOptionsCache<T>(sp.GetRequiredService<IOptionsMonitorCache<T>>()));
        return services;
    }
}
