using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Whiskers.Configuration;

/// <summary>
/// DI registration helpers that collapse the repeated "dual-registration" idiom
/// (concrete singleton + an interface forwarder to the SAME instance, optionally also run as a
/// hosted service) into one intent-revealing call. Introduced by RoadToSAP Phase 0 as the building
/// block the per-module <c>ConfigureServices</c> methods will use.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TImpl"/> as a singleton and forwards <typeparamref name="TIface"/>
    /// to that same instance. Byte-equivalent to:
    /// <code>services.AddSingleton&lt;TImpl&gt;();
    /// services.AddSingleton&lt;TIface&gt;(sp =&gt; sp.GetRequiredService&lt;TImpl&gt;());</code>
    /// </summary>
    public static IServiceCollection AddSingletonWithInterface<TImpl, TIface>(this IServiceCollection services)
        where TImpl : class, TIface
        where TIface : class
    {
        services.AddSingleton<TImpl>();
        services.AddSingleton<TIface>(sp => sp.GetRequiredService<TImpl>());
        return services;
    }

    /// <summary>
    /// As <see cref="AddSingletonWithInterface{TImpl,TIface}"/>, and additionally runs the singleton as
    /// an <see cref="IHostedService"/> on the SAME instance (so the UI/interface and the background loop
    /// share state). Byte-equivalent to the three-line concrete + forwarder + <c>AddHostedService</c> idiom.
    /// </summary>
    public static IServiceCollection AddSingletonWithInterfaceAndHostedService<TImpl, TIface>(this IServiceCollection services)
        where TImpl : class, TIface, IHostedService
        where TIface : class
    {
        services.AddSingletonWithInterface<TImpl, TIface>();
        services.AddHostedService(sp => sp.GetRequiredService<TImpl>());
        return services;
    }
}
