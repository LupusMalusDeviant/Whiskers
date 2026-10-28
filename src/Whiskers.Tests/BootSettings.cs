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

    private BootSettings(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        foreach (var (key, value) in settings)
        {
            var name = key.Replace(":", "__", StringComparison.Ordinal);
            _previous.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>Sets <paramref name="settings"/> as environment variables for the boot; dispose after the factory.</summary>
    public static BootSettings Apply(IEnumerable<KeyValuePair<string, string?>> settings) =>
        new(settings ?? throw new ArgumentNullException(nameof(settings)));

    public void Dispose()
    {
        // Restored in reverse, so a key given twice ends at its value from before the boot.
        for (var i = _previous.Count - 1; i >= 0; i--)
            Environment.SetEnvironmentVariable(_previous[i].Name, _previous[i].Previous);
        _previous.Clear();
    }
}
