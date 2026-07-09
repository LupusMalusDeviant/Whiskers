namespace Whiskers.Services.Server;

/// <summary>Core no-op defaults for the four host-management services (firewall, nginx, systemd, TLS certs),
/// used when the <b>HostManagement</b> module is off. They exist because <c>ServerTools</c> — an MCP tool
/// class that mixes core server ops (list_servers, get_server_info, execute_command) with the host-management
/// ops — can't be split under the byte-gleich move rule, so it stays in Core and still injects these services
/// per call. These no-ops keep that resolution working: reads return empty, and mutating operations return a
/// <b>failed</b> <see cref="CommandResult"/> (never a fake success) carrying the "module disabled" reason, so
/// an MCP/page call answers cleanly instead of throwing on an unresolved service. The real services are
/// registered by the module afterwards and win (last registration). Soft-dependency-via-no-op-Core-contract
/// pattern (RoadToSAP §2.1). Grouped in one file because they're a single cohesive set for one module.</summary>
internal static class HostManagementDisabled
{
    public const string Message =
        "The host-management module is disabled (set Features:host-management:Enabled=true to enable it).";

    public static CommandResult Result() => new() { ExitCode = 1, Error = Message };
}

public sealed class NoopFirewallService : IFirewallService
{
    public Task<FirewallStatus> GetStatusAsync(string serverId)
        => Task.FromResult(new FirewallStatus { RawOutput = HostManagementDisabled.Message });
    public Task<CommandResult> AddRuleAsync(string serverId, string port, string protocol = "tcp", string action = "allow", string? from = null)
        => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> RemoveRuleAsync(string serverId, int ruleNumber)
        => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> SetStatusAsync(string serverId, bool enable)
        => Task.FromResult(HostManagementDisabled.Result());
}

public sealed class NoopNginxService : INginxService
{
    public Task<List<NginxSite>> ListSitesAsync(string serverId) => Task.FromResult(new List<NginxSite>());
    public Task<string> GetSiteConfigAsync(string serverId, string siteName) => Task.FromResult("");
    public Task<CommandResult> UpdateSiteConfigAsync(string serverId, string siteName, string content)
        => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> TestConfigAsync(string serverId) => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> ReloadAsync(string serverId) => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> EnableSiteAsync(string serverId, string siteName)
        => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> DisableSiteAsync(string serverId, string siteName)
        => Task.FromResult(HostManagementDisabled.Result());
}

public sealed class NoopSystemdService : ISystemdService
{
    public Task<List<SystemdUnit>> ListServicesAsync(string serverId) => Task.FromResult(new List<SystemdUnit>());
    public Task<string> GetStatusAsync(string serverId, string serviceName) => Task.FromResult(HostManagementDisabled.Message);
    public Task<string> GetJournalAsync(string serverId, string serviceName, int lines = 100) => Task.FromResult(HostManagementDisabled.Message);
    public Task<CommandResult> StartAsync(string serverId, string serviceName) => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> StopAsync(string serverId, string serviceName) => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> RestartAsync(string serverId, string serviceName) => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> EnableAsync(string serverId, string serviceName) => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> DisableAsync(string serverId, string serviceName) => Task.FromResult(HostManagementDisabled.Result());
}

public sealed class NoopSslCertService : ISslCertService
{
    public Task<List<SslCertificate>> ListCertificatesAsync(string serverId) => Task.FromResult(new List<SslCertificate>());
    public Task<CommandResult> RenewAsync(string serverId, string certName) => Task.FromResult(HostManagementDisabled.Result());
    public Task<CommandResult> RenewAllAsync(string serverId) => Task.FromResult(HostManagementDisabled.Result());
}
