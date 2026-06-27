using ServerWatch.Models;

namespace ServerWatch.Services.LogMonitor;

/// <summary>Background log-pattern monitor; manages the alert rules.</summary>
public interface ILogMonitorService
{
    Task<List<LogAlertRuleEntity>> GetRulesAsync();
    Task<LogAlertRuleEntity> CreateRuleAsync(LogAlertRuleEntity rule);
    Task DeleteRuleAsync(string ruleId);
    Task ToggleRuleAsync(string ruleId, bool enabled);
}
