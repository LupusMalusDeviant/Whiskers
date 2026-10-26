using Whiskers.Services.Mutations;
namespace Whiskers.Services.Server;

/// <summary>Lists and controls systemd units on a server.</summary>
public interface ISystemdService
{
    Task<List<SystemdUnit>> ListServicesAsync(string serverId);
    Task<string> GetStatusAsync(string serverId, string serviceName);
    Task<string> GetJournalAsync(string serverId, string serviceName, int lines = 100);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> StartAsync(string serverId, string serviceName);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> StopAsync(string serverId, string serviceName);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> RestartAsync(string serverId, string serviceName);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> EnableAsync(string serverId, string serviceName);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> DisableAsync(string serverId, string serviceName);
}
