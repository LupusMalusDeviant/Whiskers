using ServerWatch.Models;

namespace ServerWatch.Services.ServerConfig;

/// <summary>Stores and serves the configured Docker hosts, plus their SSH key material.</summary>
public interface IServerConfigService
{
    Task InitializeAsync();
    List<Models.ServerConfig> GetServers();
    List<Models.ServerConfig> GetEnabledServers();
    Models.ServerConfig? GetServer(string serverId);
    Models.ServerConfig? GetDefaultServer();
    bool SupportsTerminal(string? serverId);
    Task AddServerAsync(Models.ServerConfig server);
    Task UpdateServerAsync(Models.ServerConfig server);
    Task RemoveServerAsync(string serverId);
    Task SaveSshKeyAsync(string serverId, string fileName, byte[] keyData);
    string? GetSshKeyPath(Models.ServerConfig server);
}
