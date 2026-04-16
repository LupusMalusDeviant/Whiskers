using System.Text.Json;
using ServerWatch.Services.Auth;
using ServerWatch.Services.Mcp;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.ConfigExport;

public class ConfigExportData
{
    public string ExportedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string Version { get; set; } = "1.0";
    public object? Servers { get; set; }
    public object? Roles { get; set; }
    public object? McpPermissions { get; set; }
    public object? Whitelist { get; set; }
}

public class ConfigExportService
{
    private readonly ServerConfigService _serverConfig;
    private readonly RoleService _roleService;
    private readonly McpPermissionService _mcpService;
    private readonly WhitelistService _whitelistService;

    public ConfigExportService(ServerConfigService serverConfig, RoleService roleService,
        McpPermissionService mcpService, WhitelistService whitelistService)
    {
        _serverConfig = serverConfig;
        _roleService = roleService;
        _mcpService = mcpService;
        _whitelistService = whitelistService;
    }

    public string ExportJson()
    {
        var data = new ConfigExportData
        {
            Servers = _serverConfig.GetServers().Select(s => new
            {
                s.Name, s.ConnectionType, s.TcpHost, s.TcpPort, s.SshHost, s.SshPort, s.SshUser,
                s.VpnType, s.VpnNote, s.TailscaleIP, s.IsDefault, s.Enabled
            }),
            Roles = _roleService.GetRoleData(),
            McpPermissions = new
            {
                Keys = _mcpService.GetPermissionData().ApiKeys.Select(k => new { k.Name, k.PermissionLevel, k.Enabled }),
                Tools = _mcpService.GetPermissionData().Tools
            },
            Whitelist = _whitelistService.GetWhitelist()
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }
}
