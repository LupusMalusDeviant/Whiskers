using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Whiskers.Tests;

/// <summary>
/// Settings for a full-app boot (<c>WebApplicationFactory&lt;Program&gt;</c>) that Program reads <b>eagerly</b>,
/// while the builder is still being set up: the module flags (<c>Features:&lt;id&gt;:Enabled</c>, read by
/// <c>ModuleCatalog.DiscoverEnabled</c>), <c>Auth:Disabled</c> (the authentication setup) and the data directory.
/// The factory's <c>ConfigureAppConfiguration</c> is layered in only after that, so a value given there alone
/// reaches the services but not those early reads: a "lean" boot silently ran with every module on, and an
/// auth-disabled boot registered the real authentication. <see cref="Apply"/> also sets each value as a process
/// environment variable (<c>Section:Key</c> → <c>Section__Key</c>) until it is disposed, which Program does see.
/// Boot tests share the serialized <c>WebAppBoot</c> collection, so the variables cannot leak into another boot.
/// </summary>
internal sealed class BootSettings : IDisposable
{
    /// <summary>The setting most boot tests need: no login in front of the endpoints they call.</summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> AuthDisabled { get; } = [new("Auth:Disabled", "true")];

    private readonly List<(string Name, string? Previous)> _previous = [];

    private BootSettings() { }

    /// <summary>Sets <paramref name="settings"/> as environment variables for the boot; dispose after the factory.
    /// All or nothing: if one cannot be set, those set before it are restored and the error is rethrown.</summary>
    public static BootSettings Apply(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var all = settings.ToList();                    // enumerated before anything is changed
        var applied = new BootSettings();
        try
        {
            foreach (var (key, value) in all)
            {
                var name = key.Replace(":", "__", StringComparison.Ordinal);
                var previous = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
                applied._previous.Add((name, previous));
            }
        }
        catch
        {
            applied.Dispose();
            throw;
        }
        return applied;
    }

    public void Dispose()
    {
        // Restored in reverse, so a key given twice ends at its value from before the boot.
        for (var i = _previous.Count - 1; i >= 0; i--)
            Environment.SetEnvironmentVariable(_previous[i].Name, _previous[i].Previous);
        _previous.Clear();
    }

    /// <summary>Asserts which authentication setup the booted app registered. The two branches of
    /// <c>AddWhiskersAuthentication</c> differ in the cookie options: only the real setup sets the logout path and the
    /// 24-hour session, so this tells a boot that saw <c>Auth:Disabled</c> while the builder was set up from one
    /// whose services saw it only later.</summary>
    public static void AssertAuthenticationSetup(IServiceProvider services, bool disabled)
    {
        var cookie = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var realSetup = cookie.LogoutPath == "/logout" && cookie.ExpireTimeSpan == TimeSpan.FromHours(24);
        Assert.True(realSetup != disabled,
            $"The boot registered the {(realSetup ? "real" : "auth-disabled")} authentication setup, " +
            $"but it was started with Auth:Disabled={disabled.ToString().ToLowerInvariant()}.");
    }
}
