using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Notifications;

/// <summary>A quiet-hours window that holds an event: the rule and the window (its start's local date).</summary>
public sealed record QuietHoursMatch(string RuleId, string RuleName, string WindowKey);

/// <summary>
/// R1-56: the quiet-hours rules and their one write path. <see cref="Match"/> decides whether a notification is held
/// back from the channels right now; the composite records what it held (<see cref="IAlertSuppressionStore"/>) and the
/// summarizer delivers it after the window. <see cref="SaveAsync"/> is the Settings page's gated, audited
/// <c>config.save</c> (R1-11, R1-22).
/// </summary>
public interface IQuietHoursService
{
    /// <summary>The rules in effect (copies).</summary>
    IReadOnlyList<QuietHoursRule> Rules { get; }

    /// <summary>The window holding <paramref name="evt"/> now, or null: outside every window, for an event no rule covers,
    /// and always for one <see cref="NotificationEventTypes.IsSuppressible"/> refuses (an unknown type is delivered).</summary>
    QuietHoursMatch? Match(NotificationEvent evt);

    /// <summary>Whether the window <paramref name="windowKey"/> of rule <paramref name="ruleId"/> is still in effect at
    /// <paramref name="utcNow"/>. False once it ended — also early, because the rule was changed, switched off or removed.</summary>
    bool IsActive(string ruleId, string windowKey, DateTime utcNow);

    /// <summary>Checks every rule (an unknown time zone, a malformed time, an event type quiet hours cannot hold throw
    /// <see cref="ArgumentException"/> and nothing is written), writes them to <c>app-settings.json</c> and publishes
    /// them once the file is written. A failed write throws and leaves the rules in effect as they were.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SaveAsync(IReadOnlyList<QuietHoursRule> rules, CancellationToken ct = default);
}

/// <inheritdoc cref="IQuietHoursService"/>
public sealed class QuietHoursService : IQuietHoursService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IAppSettingsStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IDisposable? _reload;
    private volatile IReadOnlyList<QuietHoursRule> _rules;

    public QuietHoursService(IConfiguration configuration, IAppSettingsStore store, ILogger<QuietHoursService> logger,
        TimeProvider? time = null)
    {
        _configuration = configuration;
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _rules = Read();
        // A hand edit of app-settings.json applies without a restart, like every other section.
        _reload = ChangeToken.OnChange(configuration.GetReloadToken, () => _rules = Read());
    }

    public IReadOnlyList<QuietHoursRule> Rules => _rules.Select(r => r.Clone()).ToList();

    public void Dispose() => _reload?.Dispose();

    public QuietHoursMatch? Match(NotificationEvent evt)
    {
        if (!NotificationEventTypes.IsSuppressible(evt)) return null;
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var rule in _rules)
        {
            if (!Covers(rule, evt.EventType)) continue;
            if (QuietHoursWindow.Active(rule, now) is { } window) return new QuietHoursMatch(rule.Id, rule.Name, window);
        }
        return null;
    }

    public bool IsActive(string ruleId, string windowKey, DateTime utcNow) =>
        _rules.FirstOrDefault(r => r.Id == ruleId) is { } rule && QuietHoursWindow.Active(rule, utcNow) == windowKey;

    /// <summary>A global rule covers every suppressible event; a rule naming types covers those, their
    /// <c>_recovered</c>, and with <c>log_alert</c> every log alert.</summary>
    internal static bool Covers(QuietHoursRule rule, string eventType)
    {
        if (rule.EventTypes.Count == 0) return true;
        if (rule.EventTypes.Contains(eventType)) return true;
        if (NotificationEventTypes.IsRecovery(eventType)
            && rule.EventTypes.Contains(eventType[..^NotificationEventTypes.RecoveredSuffix.Length])) return true;
        return NotificationEventTypes.IsLogAlert(eventType) && rule.EventTypes.Contains(NotificationEventTypes.LogAlertPrefix);
    }

    public async Task SaveAsync(IReadOnlyList<QuietHoursRule> rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var copies = rules.Select(r => Normalized(r.Clone())).ToList();
        // Everything is checked before anything is written.
        if (copies.Count > QuietHoursSettings.MaxRules)
            throw new ArgumentException($"At most {QuietHoursSettings.MaxRules} quiet-hours rules are possible.");
        if (copies.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != copies.Count)
            throw new ArgumentException("Two quiet-hours rules have the same id.");
        foreach (var rule in copies)
            if (QuietHoursWindow.Problem(rule) is { } problem) throw new ArgumentException(problem);

        await _lock.WaitAsync(ct);
        try
        {
            var section = JsonSerializer.SerializeToNode(new QuietHoursSettings { Rules = copies })!.AsObject();
            await _store.UpdateSectionAsync(QuietHoursSettings.SectionName, _ => section);
            // Published only once the file is written; a failed write threw above and left the rules as they were.
            _rules = copies;
        }
        finally { _lock.Release(); }
    }

    private static QuietHoursRule Normalized(QuietHoursRule rule)
    {
        rule.Name = rule.Name?.Trim() ?? "";
        rule.TimeZone = rule.TimeZone?.Trim() ?? "";
        rule.Start = rule.Start?.Trim() ?? "";
        rule.End = rule.End?.Trim() ?? "";
        rule.EventTypes = (rule.EventTypes ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal).ToList();
        return rule;
    }

    /// <summary>The rules as configured. One that cannot be read (a hand edit with an unknown time zone) holds nothing —
    /// its events are delivered — and is reported once per reload; a section that does not bind keeps the last rules.</summary>
    private IReadOnlyList<QuietHoursRule> Read()
    {
        try
        {
            var settings = _configuration.GetSection(QuietHoursSettings.SectionName).Get<QuietHoursSettings>() ?? new();
            var valid = new List<QuietHoursRule>();
            foreach (var rule in settings.Rules.Take(QuietHoursSettings.MaxRules))
            {
                var normalized = Normalized(rule.Clone());
                if (QuietHoursWindow.Problem(normalized) is { } problem)
                {
                    _logger.LogWarning("Quiet-hours rule {Rule} is ignored — its notifications are delivered: {Problem}",
                        normalized.Name, problem);
                    continue;
                }
                valid.Add(normalized);
            }
            return valid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Quiet hours cannot be read ({Error}); the last rules stay in effect", ex.Message);
            return _rules ?? [];
        }
    }
}
