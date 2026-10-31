using Whiskers.Utils;

namespace Whiskers.Startup;

/// <summary>
/// Puts <see cref="RedactingLoggerProvider"/> in front of every log provider registered so far (INV-51, R1-25):
/// console, debug, event source and event log alike write only redacted entries. Runs last, after every other
/// registration, so no provider is left unwrapped.
/// </summary>
public static class WhiskersLoggingExtensions
{
    public static void AddWhiskersLogRedaction(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // Request URIs of outbound calls can carry a capability (a bot token in the path, a webhook key): the
        // HttpClient request log stays at Warning whatever the configuration says, and the redaction behind it
        // catches what a configuration override lets through.
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.Services.AddLogRedaction();
    }

    /// <summary>Replaces each <see cref="ILoggerProvider"/> registration with a redacting wrapper around it.</summary>
    public static IServiceCollection AddLogRedaction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var providers = services
            .Where(d => d.ServiceType == typeof(ILoggerProvider) && !d.IsKeyedService)
            .ToList();
        foreach (var descriptor in providers)
        {
            services.Remove(descriptor);
            services.Add(new ServiceDescriptor(typeof(ILoggerProvider),
                sp => new RedactingLoggerProvider(Create(sp, descriptor)), descriptor.Lifetime));
        }
        return services;
    }

    private static ILoggerProvider Create(IServiceProvider sp, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance as ILoggerProvider
        ?? descriptor.ImplementationFactory?.Invoke(sp) as ILoggerProvider
        ?? (ILoggerProvider)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
}
