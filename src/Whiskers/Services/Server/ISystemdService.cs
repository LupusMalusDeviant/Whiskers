using Whiskers.Services.Mutations;
namespace Whiskers.Services.Server;

/// <summary>Lists and controls systemd units on a server.</summary>
public interface ISystemdService
{
    Task<List<SystemdUnit>> ListServicesAsync(string serverId, CancellationToken ct = default);

    /// <summary>Contract 0012 (D11): every service unit the host knows (its unit files and loaded units, active or not),
    /// for the gateway to resolve a <c>systemd.manage</c> target. Throws when the host cannot be asked.</summary>
    [InventoryLookup]
    Task<IReadOnlyList<string>> ListUnitNamesAsync(string serverId);
    Task<string> GetStatusAsync(string serverId, string serviceName);
    Task<string> GetJournalAsync(string serverId, string serviceName, int lines = 100);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> StartAsync(string serverId, [MutationTarget] string serviceName);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> StopAsync(string serverId, [MutationTarget] string serviceName);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> RestartAsync(string serverId, [MutationTarget] string serviceName);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> EnableAsync(string serverId, [MutationTarget] string serviceName);
    [MutatingOperation("systemd.manage")]
    Task<CommandResult> DisableAsync(string serverId, [MutationTarget] string serviceName);
}
