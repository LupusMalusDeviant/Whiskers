using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ServerWatch.Models;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Notifications;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.LogMonitor;

/// <summary>
/// Background service that periodically checks container logs against alert rules.
/// </summary>
public class LogMonitorService : BackgroundService, ILogMonitorService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDockerService _docker;
    private readonly INotificationService _notifications;
    private readonly ILogger<LogMonitorService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly ConcurrentDictionary<string, int> _logOffsets = new(); // container → last checked line count

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    public LogMonitorService(
        IServiceScopeFactory scopeFactory,
        IDockerService docker,
        INotificationService notifications,
        ILogger<LogMonitorService> logger)
    {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Log monitor service started. Check interval: {Interval}s", CheckInterval.TotalSeconds);
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // initial delay

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckLogsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Log monitor check failed");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckLogsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var rules = await db.LogAlertRules.Where(r => r.Enabled).ToListAsync(ct);
        if (!rules.Any()) return;

        var containers = await _docker.ListContainersAsync(all: false);

        foreach (var container in containers)
        {
            if (ct.IsCancellationRequested) break;

            var applicableRules = rules.Where(r =>
                r.ContainerId == null || r.ContainerId == container.Id || r.ContainerName == container.Name).ToList();

            if (!applicableRules.Any()) continue;

            try
            {
                // Get last 50 lines (only new since last check would be ideal, but tail is simpler)
                var logs = await _docker.GetContainerLogsAsync(container.Id, 50);
                var lines = logs.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var rule in applicableRules)
                {
                    // Cooldown check
                    var cooldownKey = $"{rule.RuleId}:{container.Id}";
                    if (_cooldowns.TryGetValue(cooldownKey, out var lastTriggered) &&
                        DateTime.UtcNow - lastTriggered < TimeSpan.FromMinutes(rule.CooldownMinutes))
                        continue;

                    // Pattern match
                    bool matched = false;
                    string? matchedLine = null;

                    foreach (var line in lines)
                    {
                        bool hit = rule.IsRegex
                            ? Regex.IsMatch(line, rule.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
                            : line.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);

                        if (hit)
                        {
                            matched = true;
                            matchedLine = line.Length > 200 ? line[..200] : line;
                            break;
                        }
                    }

                    if (matched)
                    {
                        _cooldowns[cooldownKey] = DateTime.UtcNow;

                        // Update rule stats
                        rule.LastTriggered = DateTime.UtcNow;
                        rule.TriggerCount++;

                        // Send notification
                        var evt = new NotificationEvent
                        {
                            ContainerId = container.Id,
                            ContainerName = container.Name,
                            Image = container.Image,
                            EventType = $"log_alert:{rule.Severity}",
                            // Abuse RestartCount field for trigger count
                            RestartCount = rule.TriggerCount
                        };

                        await _notifications.SendAsync(evt);

                        _logger.LogWarning("Log alert triggered: {RuleName} on {Container} — {Line}",
                            rule.Name, container.Name, matchedLine);
                    }
                }

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to check logs for {Container}", container.Name);
            }
        }
    }

    // === Public API ===

    public async Task<List<LogAlertRuleEntity>> GetRulesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.LogAlertRules.OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<LogAlertRuleEntity> CreateRuleAsync(LogAlertRuleEntity rule)
    {
        // Validate regex
        if (rule.IsRegex)
            _ = new Regex(rule.Pattern); // throws on invalid

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.LogAlertRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    public async Task DeleteRuleAsync(string ruleId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var rule = await db.LogAlertRules.FirstOrDefaultAsync(r => r.RuleId == ruleId);
        if (rule != null)
        {
            db.LogAlertRules.Remove(rule);
            await db.SaveChangesAsync();
        }
    }

    public async Task ToggleRuleAsync(string ruleId, bool enabled)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var rule = await db.LogAlertRules.FirstOrDefaultAsync(r => r.RuleId == ruleId);
        if (rule != null)
        {
            rule.Enabled = enabled;
            await db.SaveChangesAsync();
        }
    }
}
