namespace Whiskers.Services.Server;

/// <summary>Lists and edits nginx site configurations on a server.</summary>
public interface INginxService
{
    Task<List<NginxSite>> ListSitesAsync(string serverId);
    Task<string> GetSiteConfigAsync(string serverId, string siteName);
    Task<CommandResult> UpdateSiteConfigAsync(string serverId, string siteName, string content);
    Task<CommandResult> TestConfigAsync(string serverId);
    Task<CommandResult> ReloadAsync(string serverId);
    Task<CommandResult> EnableSiteAsync(string serverId, string siteName);
    Task<CommandResult> DisableSiteAsync(string serverId, string siteName);
}
