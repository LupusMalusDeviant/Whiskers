namespace ServerWatch.Services.Server;

/// <summary>Inspects and manages the host firewall (ufw) on a server.</summary>
public interface IFirewallService
{
    Task<FirewallStatus> GetStatusAsync(string serverId);
    Task<CommandResult> AddRuleAsync(string serverId, string port, string protocol = "tcp", string action = "allow", string? from = null);
    Task<CommandResult> RemoveRuleAsync(string serverId, int ruleNumber);
    Task<CommandResult> SetStatusAsync(string serverId, bool enable);
}
