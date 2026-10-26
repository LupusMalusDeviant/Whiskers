using Whiskers.Services.Mutations;
namespace Whiskers.Services.Server;

/// <summary>Lists and edits nginx site configurations on a server.</summary>
public interface INginxService
{
    Task<List<NginxSite>> ListSitesAsync(string serverId);
    Task<string> GetSiteConfigAsync(string serverId, string siteName);
    [MutatingOperation("nginx.update")]
    Task<CommandResult> UpdateSiteConfigAsync(string serverId, string siteName, string content);
    Task<CommandResult> TestConfigAsync(string serverId);
    [MutatingOperation("nginx.reload")]
    Task<CommandResult> ReloadAsync(string serverId);
    [MutatingOperation("nginx.site_enable")]
    Task<CommandResult> EnableSiteAsync(string serverId, string siteName);
    [MutatingOperation("nginx.site_disable")]
    Task<CommandResult> DisableSiteAsync(string serverId, string siteName);
}
