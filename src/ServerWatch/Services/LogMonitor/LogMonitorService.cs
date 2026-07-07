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
    // Per-container timestamp of the last log check, so we fetch only NEW lines and an old ERROR line
    // doesn't re-alert every cycle.
    private readonly ConcurrentDictionary<string, DateTime> _lastLogCheck = new();

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    // Our own container must never be scanned for log alerts. ServerWatch logs its own
    // "Log alert triggered: … {matchedLine}" and "Trivy scan failed … FATAL" lines; when an
    // "all containers" rule reads those back they re-match the pattern and create a
    // self-amplifying trigger loop (this is what ran the "Echte Fehler" rule up to 133×).
    // Self-monitoring, if ever wanted, must be a deliberate out-of-band mechanism, not this.
    // Override the excluded name(s) via SERVERWATCH_SELF_CONTAINERS (comma-separated).
    private static readonly HashSet<string> SelfContainerNames = new(
        (Environment.GetEnvironmentVariable("SERVERWATCH_SELF_CONTAINERS") ?? "serverwatch")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

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

        // Compile the regex rules once per cycle (keyed by pattern) instead of re-parsing each pattern
        // for every log line of every container. Invalid patterns are dropped here with a warning.
        var compiledRegexes = new Dictionary<string, Regex>();
        foreach (var r in rules.Where(r => r.IsRegex))
        {
            if (compiledRegexes.ContainsKey(r.Pattern)) continue;
            try { compiledRegexes[r.Pattern] = new Regex(r.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { _logger.LogWarning(ex, "Invalid log-alert regex '{Pattern}' — skipped", r.Pattern); }
        }

        var containers = await _docker.ListContainersAsync(all: false);

        foreach (var container in containers)
        {
            if (ct.IsCancellationRequested) break;

            // Never scan our own logs — breaks the self-amplifying alert feedback loop.
            if (SelfContainerNames.Contains(container.Name)) continue;

            var applicableRules = rules.Where(r =>
                r.ContainerId == null || r.ContainerId == container.Id || r.ContainerName == container.Name).ToList();

            if (!applicableRules.Any()) continue;

            try
            {
                // Fetch only lines since our last check so an old ERROR line doesn't re-alert every cycle;
                // on first sight, baseline to now so historical logs aren't alerted.
                var since = _lastLogCheck.TryGetValue(container.Id, out var last) ? last : DateTime.UtcNow;
                var logs = await _docker.GetContainerLogsAsync(container.Id, 50, since: since);
                _lastLogCheck[container.Id] = DateTime.UtcNow;
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
                            ? compiledRegexes.TryGetValue(rule.Pattern, out var rx) && rx.IsMatch(line)
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

        // Bound the per-container maps: drop entries for containers no longer in the list.
        var liveIds = containers.Select(c => c.Id).ToHashSet();
        foreach (var kv in _cooldowns.ToArray())
        {
            var parts = kv.Key.Split(':', 2); // "ruleId:containerId"
            if (parts.Length == 2 && !liveIds.Contains(parts[1])) _cooldowns.TryRemove(kv.Key, out _);
        }
        foreach (var id in _lastLogCheck.Keys)
            if (!liveIds.Contains(id)) _lastLogCheck.TryRemove(id, out _);
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
        // Validate regex. The timeout is defense-in-depth only — this compiles (it does not match), and the
        // actual match paths (LogSearchService and the monitor loop) already run every pattern under a timeout.
        if (rule.IsRegex)
            _ = new Regex(rule.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); // throws on invalid

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
