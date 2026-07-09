using Whiskers.Models;

namespace Whiskers.Services.LogMonitor;

/// <summary>The Core's default <see cref="ILogMonitorService"/>: manages no rules and runs no background
/// monitor. Registered before the module pipeline so the AI-triggers page (which reads/creates log-alert rules
/// via <see cref="ILogMonitorService"/>) still resolves the service when the <b>LogMonitor</b> module is
/// disabled. When the module is enabled it registers the real <see cref="LogMonitorService"/> afterwards,
/// which wins (last registration). Soft-dependency-via-no-op-Core-contract pattern (RoadToSAP §2.1), the same
/// shape as <c>NoopNotificationService</c>. With the module off, creating a rule is a no-op (it isn't
/// persisted and nothing scans logs), which is the correct behaviour for a disabled log monitor.</summary>
public sealed class NoopLogMonitorService : ILogMonitorService
{
    public Task<List<LogAlertRuleEntity>> GetRulesAsync() => Task.FromResult(new List<LogAlertRuleEntity>());
    public Task<LogAlertRuleEntity> CreateRuleAsync(LogAlertRuleEntity rule) => Task.FromResult(rule);
    public Task DeleteRuleAsync(string ruleId) => Task.CompletedTask;
    public Task ToggleRuleAsync(string ruleId, bool enabled) => Task.CompletedTask;
}
