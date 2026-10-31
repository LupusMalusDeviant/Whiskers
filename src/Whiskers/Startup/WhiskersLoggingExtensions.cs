using Whiskers.Utils;

namespace Whiskers.Startup;

/// <summary>
/// Puts <see cref="RedactingLoggerFactory"/> in front of the logger factory (INV-51, R1-25): every logger the app
/// resolves — <c>ILogger&lt;T&gt;</c> included — writes only redacted entries to console, debug, event source and event
/// log alike. The real factory stays behind it with its providers, filter rules and scopes unchanged.
/// </summary>
public static class WhiskersLoggingExtensions
{
    public static void AddWhiskersLogRedaction(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // Request URIs of outbound calls can carry a capability (a bot token in the path, a webhook key): the
        // HttpClient request log stays at Warning by default in code. A more specific configuration rule can still
        // switch it on; the redaction behind it catches what that lets through.
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.Services.AddLogRedaction();
    }

    /// <summary>Replaces the <see cref="ILoggerFactory"/> registration with a redacting decorator around it.</summary>
    public static IServiceCollection AddLogRedaction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var factories = services
            .Where(d => d.ServiceType == typeof(ILoggerFactory) && !d.IsKeyedService)
            .ToList();
        if (factories.Count == 0)
            throw new InvalidOperationException("No logger factory is registered: call AddLogging first.");
        foreach (var descriptor in factories)
        {
            services.Remove(descriptor);
            services.Add(new ServiceDescriptor(typeof(ILoggerFactory),
                sp => new RedactingLoggerFactory(Create(sp, descriptor)),
                descriptor.Lifetime));
        }
        return services;
    }

    private static ILoggerFactory Create(IServiceProvider sp, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance as ILoggerFactory
        ?? descriptor.ImplementationFactory?.Invoke(sp) as ILoggerFactory
        ?? (ILoggerFactory)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
}
