using ModelContextProtocol.Server;
using System.ComponentModel;
using Whiskers.Services.Docker;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Mcp;
using Whiskers.Services.AuditLog;
using Whiskers.Utils;
using Microsoft.AspNetCore.Http;

namespace Whiskers.Mcp.Tools;

[McpServerToolType]
public class ServerTools
{
    [McpServerTool, Description("List all configured servers with their connection type and status. Optionally filter by a group or tag (case-insensitive) to narrow a large fleet.")]
    public static async Task<string> ListServers(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IServerConfigService serverConfig,
        [Description("Optional group name or tag to filter by (case-insensitive). Omit to list all servers.")] string? tag = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_servers");
        if (denied != null) return denied;
        var servers = serverConfig.GetEnabledServers();
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var t = tag.Trim();
            servers = servers
                .Where(s => string.Equals(s.Group, t, StringComparison.OrdinalIgnoreCase)
                            || s.Tags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        var infos = await docker.GetAllServerSystemInfoAsync();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(tag)
            ? $"Configured servers ({servers.Count}):"
            : $"Configured servers matching '{tag}' ({servers.Count}):");
        foreach (var server in servers)
        {
            var info = infos.GetValueOrDefault(server.Id);
            var org = new List<string>();
            if (!string.IsNullOrEmpty(server.Group)) org.Add($"Group: {server.Group}");
            if (server.Tags.Count > 0) org.Add($"Tags: {string.Join(", ", server.Tags)}");
            var orgSuffix = org.Count > 0 ? $" [{string.Join("; ", org)}]" : "";
            sb.AppendLine($"- {server.Name} (ID: {server.Id}, Type: {server.ConnectionType}){orgSuffix}");
            if (server.ConnectionType == Whiskers.Models.ConnectionType.Kubernetes)
                sb.AppendLine("  Kubernetes cluster — pods are shown on the dashboard (workload provider); MCP tool coverage follows in Track B.3.");
            else if (info?.IsReachable == true)
                sb.AppendLine($"  OS: {info.OperatingSystem}, CPU: {info.CpuCount} cores ({info.CpuUsagePercent:F1}%), RAM: {info.MemoryUsedBytes / 1073741824.0:F1}/{info.MemoryTotalBytes / 1073741824.0:F1} GB, Docker: {info.DockerVersion}, Containers: {info.ContainersRunning}/{info.ContainersTotal}");
            else
                sb.AppendLine($"  Status: Unreachable - {info?.Error ?? "unknown"}");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("Get detailed system information for a server (OS, CPU, RAM, disk, Docker version, containers).")]
    public static async Task<string> GetServerInfo(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        [Description("Server ID")] string serverId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_server_info");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";

        var info = await docker.GetServerSystemInfoAsync(serverId);
        if (!info.IsReachable) return $"Server unreachable: {info.Error}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Server: {info.ServerName} ({serverId})");
        sb.AppendLine($"OS: {info.OperatingSystem}");
        sb.AppendLine($"Kernel: {info.KernelVersion}");
        sb.AppendLine($"Architecture: {info.Architecture}");
        sb.AppendLine($"CPU: {info.CpuCount} cores, {info.CpuUsagePercent:F1}% used");
        sb.AppendLine($"Memory: {info.MemoryUsedBytes / 1073741824.0:F1} GB / {info.MemoryTotalBytes / 1073741824.0:F1} GB ({info.MemoryUsedPercent:F1}%)");
        sb.AppendLine($"Docker: {info.DockerVersion}");
        sb.AppendLine($"Containers: {info.ContainersRunning} running / {info.ContainersTotal} total");
        sb.AppendLine($"Images: {info.ImagesCount}");
        sb.AppendLine($"IP: {info.IpAddress}");
        return sb.ToString();
    }

    [McpServerTool, Description("Execute a shell command on a server. Use with caution.")]
    public static async Task<string> ExecuteCommand(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IHostCommandExecutor executor,
        IAuditLogService auditLog,
        [Description("Server ID")] string serverId,
        [Description("Shell command to execute")] string command,
        [Description("Timeout in seconds (default 30, max 600)")] int timeoutSeconds = 30)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "execute_command");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";
        if (string.IsNullOrWhiteSpace(command))
            return "Command is required.";

        if (timeoutSeconds <= 0 || timeoutSeconds > 600)
            timeoutSeconds = 30;

        // Cap per-stream output so a chatty command can't blow up the model context.
        const int maxStreamChars = 50_000;

        var result = await executor.ExecuteAsync(serverId, command, TimeSpan.FromSeconds(timeoutSeconds));
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        // Redact secrets before persisting the command to the audit log (the executed command is unchanged).
        var redactedCommand = SecretRedactor.Redact(command);
        await auditLog.LogAsync(actor, actorType, "command.execute", "command", redactedCommand, redactedCommand, details: $"Exit code: {result.ExitCode}", serverId: serverId, success: result.ExitCode == 0);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Exit code: {result.ExitCode}");
        if (!string.IsNullOrEmpty(result.Output))
            sb.AppendLine($"Output:\n{ShellUtils.Truncate(result.Output, maxStreamChars)}");
        if (!string.IsNullOrEmpty(result.Error))
            sb.AppendLine($"Errors:\n{ShellUtils.Truncate(result.Error, maxStreamChars)}");
        return sb.ToString();
    }

    [McpServerTool, Description("List firewall (UFW) rules on a server.")]
    public static async Task<string> ListFirewallRules(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IFirewallService firewall,
        [Description("Server ID")] string serverId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_firewall_rules");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";

        var status = await firewall.GetStatusAsync(serverId);
        if (!status.Active) return "Firewall is inactive.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Firewall: Active ({status.Rules.Count} rules)");
        foreach (var rule in status.Rules)
            sb.AppendLine($"  [{rule.Number}] {rule.Port} {rule.Action} {rule.Direction} from {rule.From}");
        return sb.ToString();
    }

    [McpServerTool, Description("Add a firewall rule (UFW) to allow or deny traffic on a port.")]
    public static async Task<string> AddFirewallRule(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IFirewallService firewall,
        IAuditLogService auditLog,
        [Description("Server ID")] string serverId,
        [Description("Port number or range (e.g. '80', '8000:9000')")] string port,
        [Description("Protocol: tcp, udp, or both")] string protocol = "tcp",
        [Description("Action: allow or deny")] string action = "allow",
        [Description("Source IP/CIDR (optional, e.g. '192.168.1.0/24')")] string? from = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "add_firewall_rule");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";
        if (string.IsNullOrWhiteSpace(port))
            return "Port is required.";

        var validProtocols = new[] { "tcp", "udp", "both" };
        if (!validProtocols.Contains(protocol.ToLower()))
            return $"Invalid protocol '{protocol}'. Must be one of: tcp, udp, both.";

        var validActions = new[] { "allow", "deny" };
        if (!validActions.Contains(action.ToLower()))
            return $"Invalid action '{action}'. Must be one of: allow, deny.";

        var result = await firewall.AddRuleAsync(serverId, port, protocol, action, from);
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        var ruleDesc = $"{action} {port}/{protocol}" + (from != null ? $" from {from}" : "");
        await auditLog.LogAsync(actor, actorType, "firewall.add", "firewall", ruleDesc, ruleDesc, serverId: serverId, success: result.Success);
        return result.Success
            ? $"Rule added: {action} {port}/{protocol}" + (from != null ? $" from {from}" : "")
            : $"Failed: {result.Error}";
    }

    [McpServerTool, Description("Remove a firewall rule by its number.")]
    public static async Task<string> RemoveFirewallRule(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IFirewallService firewall,
        IAuditLogService auditLog,
        [Description("Server ID")] string serverId,
        [Description("Rule number to remove")] int ruleNumber)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "remove_firewall_rule");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";
        if (ruleNumber <= 0)
            return "Rule number must be a positive integer.";

        var result = await firewall.RemoveRuleAsync(serverId, ruleNumber);
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        await auditLog.LogAsync(actor, actorType, "firewall.remove", "firewall", ruleNumber.ToString(), $"Rule #{ruleNumber}", serverId: serverId, success: result.Success);
        return result.Success ? $"Rule {ruleNumber} removed." : $"Failed: {result.Error}";
    }

    [McpServerTool, Description("List Nginx sites (enabled and available) on a server.")]
    public static async Task<string> ListNginxSites(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        INginxService nginx,
        [Description("Server ID")] string serverId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_nginx_sites");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";

        var sites = await nginx.ListSitesAsync(serverId);
        var lines = sites.Select(s => $"- {s.Name} [{(s.Enabled ? "enabled" : "available")}]");
        return $"Nginx sites:\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Get the Nginx configuration for a specific site.")]
    public static async Task<string> GetNginxConfig(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        INginxService nginx,
        [Description("Server ID")] string serverId,
        [Description("Site name (filename in sites-available)")] string siteName)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_nginx_config");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";
        if (string.IsNullOrWhiteSpace(siteName))
            return "Site name is required.";

        var config = await nginx.GetSiteConfigAsync(serverId, siteName);
        return $"Nginx config for {siteName}:\n```nginx\n{config}\n```";
    }

    [McpServerTool, Description("Update an Nginx site configuration. Validates with nginx -t before applying.")]
    public static async Task<string> UpdateNginxConfig(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        INginxService nginx,
        IAuditLogService auditLog,
        [Description("Server ID")] string serverId,
        [Description("Site name")] string siteName,
        [Description("New nginx config content")] string content)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "update_nginx_config");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";
        if (string.IsNullOrWhiteSpace(siteName))
            return "Site name is required.";
        if (string.IsNullOrWhiteSpace(content))
            return "Config content is required.";

        var result = await nginx.UpdateSiteConfigAsync(serverId, siteName, content);
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        await auditLog.LogAsync(actor, actorType, "nginx.update", "nginx", siteName, siteName, serverId: serverId, success: result.Success);
        return result.Success ? $"Nginx config for {siteName} updated and reloaded." : $"Failed: {result.Error}";
    }

    [McpServerTool, Description("List systemd services on a server.")]
    public static async Task<string> ListSystemdServices(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISystemdService systemd,
        [Description("Server ID")] string serverId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_systemd_services");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";

        var services = await systemd.ListServicesAsync(serverId);
        var lines = services.Take(50).Select(s => $"- {s.Name} [{s.ActiveState}/{s.SubState}] {(s.Enabled ? "enabled" : "disabled")} — {s.Description}");
        return $"systemd services ({services.Count} total, showing first 50):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Manage a systemd service (start, stop, restart, enable, disable).")]
    public static async Task<string> ManageSystemdService(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISystemdService systemd,
        IAuditLogService auditLog,
        [Description("Server ID")] string serverId,
        [Description("Service name (e.g. 'nginx', 'docker')")] string serviceName,
        [Description("Action: start, stop, restart, enable, disable")] string action)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "manage_systemd_service");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";
        if (string.IsNullOrWhiteSpace(serviceName))
            return "Service name is required.";
        if (string.IsNullOrWhiteSpace(action))
            return "Action is required.";

        var validActions = new[] { "start", "stop", "restart", "enable", "disable" };
        if (!validActions.Contains(action.ToLower()))
            return $"Invalid action '{action}'. Must be one of: start, stop, restart, enable, disable.";

        var result = action.ToLower() switch
        {
            "start" => await systemd.StartAsync(serverId, serviceName),
            "stop" => await systemd.StopAsync(serverId, serviceName),
            "restart" => await systemd.RestartAsync(serverId, serviceName),
            "enable" => await systemd.EnableAsync(serverId, serviceName),
            "disable" => await systemd.DisableAsync(serverId, serviceName),
            _ => new Whiskers.Services.Server.CommandResult { ExitCode = 1, Error = $"Unknown action: {action}" }
        };
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        await auditLog.LogAsync(actor, actorType, "systemd.manage", "systemd", serviceName, serviceName, details: action, serverId: serverId, success: result.Success);
        return result.Success ? $"{serviceName}: {action} succeeded." : $"{serviceName}: {action} failed - {result.Error}";
    }

    [McpServerTool, Description("List SSL/TLS certificates managed by certbot on a server.")]
    public static async Task<string> ListSslCertificates(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISslCertService ssl,
        [Description("Server ID")] string serverId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_ssl_certificates");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";

        var certs = await ssl.ListCertificatesAsync(serverId);
        if (!certs.Any()) return "No SSL certificates found.";

        var lines = certs.Select(c => c.ExpiresAt is { } exp
            ? $"- {c.CertName}: {string.Join(", ", c.Domains)} — Expires: {exp:yyyy-MM-dd} ({c.DaysUntilExpiry} days){(c.IsExpiringSoon ? " ⚠️" : "")}"
            : $"- {c.CertName}: {string.Join(", ", c.Domains)} — Expires: unknown");
        return $"SSL certificates:\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Renew an SSL certificate using certbot.")]
    public static async Task<string> RenewSslCertificate(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ISslCertService ssl,
        IAuditLogService auditLog,
        [Description("Server ID")] string serverId,
        [Description("Certificate name (from list). Use 'all' to renew all.")] string certName)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "renew_ssl_certificate");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(serverId))
            return "Server ID is required.";
        if (string.IsNullOrWhiteSpace(certName))
            return "Certificate name is required. Use 'all' to renew all certificates.";

        var result = certName.ToLower() == "all"
            ? await ssl.RenewAllAsync(serverId)
            : await ssl.RenewAsync(serverId, certName);
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        await auditLog.LogAsync(actor, actorType, "ssl.renew", "ssl", certName, certName, serverId: serverId, success: result.Success);
        return result.Success ? $"SSL renewal succeeded." : $"SSL renewal failed: {result.Error}";
    }
}
