using Whiskers.Services.Mutations;
using Whiskers.Models;

namespace Whiskers.Services.LogMonitor;

/// <summary>Background log-pattern monitor; manages the alert rules.</summary>
/// <summary>A container whose logs the scan has given up reading for now (Plan-0002 WP3/WP5).</summary>
/// <param name="Until">When the scan will try again. The pause lengthens with each repeat: 5, 15, then 60
/// minutes.</param>
/// <param name="ConsecutiveTimeouts">How many fetches in a row timed out. The number matters more than the
/// pause: on 2026-08-26 these timeouts were being written to the log every cycle for six days and nobody
/// counted them.</param>
public sealed record SuspendedContainer(
    string ServerId, string ContainerId, string ContainerName, DateTime Until, int ConsecutiveTimeouts);

public interface ILogMonitorService
{
    /// <summary>Containers currently excluded from the log scan because their logs could not be read.
    ///
    /// <para>This state was already being announced when it started and then lived only inside the scanner.
    /// A container nobody is reading looks exactly like a container with nothing to report, and the one-time
    /// alert scrolls out of the channel — so it has to be visible somewhere that is still true tomorrow.</para></summary>
    IReadOnlyList<SuspendedContainer> SuspendedContainers();

    Task<List<LogAlertRuleEntity>> GetRulesAsync();
    [MutatingOperation("logalert.create")]
    Task<LogAlertRuleEntity> CreateRuleAsync(LogAlertRuleEntity rule);
    [MutatingOperation("logalert.delete")]
    Task DeleteRuleAsync(string ruleId);
    [MutatingOperation("logalert.toggle")]
    Task ToggleRuleAsync(string ruleId, bool enabled);
}
