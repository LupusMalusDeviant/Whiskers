using Whiskers.Services.Mutations;
namespace Whiskers.Services.Server;

/// <summary>Inspects and manages the host firewall (ufw) on a server.</summary>
public interface IFirewallService
{
    [InventoryLookup]
    Task<FirewallStatus> GetStatusAsync(string serverId);
    [MutatingOperation("firewall.rule_add")]
    Task<CommandResult> AddRuleAsync(string serverId, string port, string protocol = "tcp", string action = "allow", string? from = null);
    [MutatingOperation("firewall.rule_remove")]
    /// <param name="ruleNumber">The rule's number in <c>ufw status numbered</c>, as text: the target the decision
    /// resolved (contract 0012, D11), bound by the scope guard. Anything but a positive integer is refused.</param>
    Task<CommandResult> RemoveRuleAsync(string serverId, [MutationTarget] string ruleNumber);
    [MutatingOperation("firewall.set_status")]
    Task<CommandResult> SetStatusAsync(string serverId, bool enable);
}
