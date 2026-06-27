using ServerWatch.Configuration;

namespace ServerWatch.Services.Coolify;

/// <summary>Stores the Coolify API connection settings.</summary>
public interface ICoolifyConfigService
{
    Task InitializeAsync();
    CoolifySettings GetSettings();
    bool IsEnabled { get; }
    Task SaveAsync(CoolifySettings settings);
}
