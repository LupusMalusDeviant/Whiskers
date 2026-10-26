using Whiskers.Services.Mutations;
namespace Whiskers.Services.Server;

/// <summary>Inspects and manages the host firewall (ufw) on a server.</summary>
public interface IFirewallService
{
    Task<FirewallStatus> GetStatusAsync(string serverId);
    [MutatingOperation("firewall.rule_add")]
    Task<CommandResult> AddRuleAsync(string serverId, string port, string protocol = "tcp", string action = "allow", string? from = null);
    [MutatingOperation("firewall.rule_remove")]
    Task<CommandResult> RemoveRuleAsync(string serverId, int ruleNumber);
    [MutatingOperation("firewall.set_status")]
    Task<CommandResult> SetStatusAsync(string serverId, bool enable);
}
