using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Agent.Guardrails;
using Whiskers.Services.Notifications;

namespace Whiskers.Services.Agent.Triggers;

/// <summary>Watches every notification event and, for each enabled matching trigger, runs the agent
/// autonomously under the trigger's guardrail preset. The principal is capped at the trigger's
/// configured MaxLevel (default write) so the PrincipalCeilingRule bounds the run; confirmations are
/// denied, never auto-approved (no human is watching). Result → notification + audit. Resolves its
/// dependencies lazily from the root provider to avoid a DI cycle with the notification service.</summary>
public interface IAiTriggerDispatcher
{
    Task OnEventAsync(NotificationEvent evt);
}

public sealed class AiTriggerDispatcher : IAiTriggerDispatcher
{
    private const int MaxConcurrentRuns = 3;
    private const int MaxSummaryChars = 1500;

    private readonly IAiTriggerStore _store;
    private readonly IServiceProvider _sp;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<AiTriggerDispatcher>? _logger;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentRuns, MaxConcurrentRuns);
    private readonly ConcurrentDictionary<string, DateTime> _lastRun = new();

    public AiTriggerDispatcher(
        IAiTriggerStore store, IServiceProvider sp, IOptionsMonitor<AgentSettings> settings,
        ILogger<AiTriggerDispatcher>? logger = null)
    {
        _store = store;
        _sp = sp;
        _settings = settings;
        _logger = logger;
    }

    public Task OnEventAsync(NotificationEvent evt)
    {
        // Recursion guard: never react to our own result events or approval pushes.
        if (evt.EventType.StartsWith("agent_action", StringComparison.Ordinal)) return Task.CompletedTask;
        if (evt.EventType == "agent_approval") return Task.CompletedTask;
        if (!_settings.CurrentValue.Enabled) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        foreach (var t in _store.Triggers)
        {
            if (!t.Enabled || !Matches(t, evt)) continue;

            var key = $"{t.Id}|{evt.ContainerId}";
            if (_lastRun.TryGetValue(key, out var last) && (now - last).TotalSeconds < t.CooldownSeconds) continue;
            _lastRun[key] = now;

            _ = RunTriggerAsync(t, evt);   // fire-and-forget; bounded by the concurrency gate
        }

        // Bound _lastRun (keyed trigger|container, so recreated containers grow it): once it's large, drop
        // entries older than the longest cooldown — they can no longer suppress anything.
        if (_lastRun.Count > 1000)
        {
            var maxCooldown = _store.Triggers.Select(x => x.CooldownSeconds).DefaultIfEmpty(0).Max();
            var cutoff = now.AddSeconds(-Math.Max(3600, maxCooldown));
            foreach (var kv in _lastRun)
                if (kv.Value < cutoff) _lastRun.TryRemove(kv.Key, out _);
        }
        return Task.CompletedTask;
    }

    private static bool Matches(AiTrigger t, NotificationEvent evt)
    {
        var typeMatch = t.EventTypes.Any(x =>
            evt.EventType == x || (x == "log_alert" && evt.EventType.StartsWith("log_alert", StringComparison.Ordinal)));
        if (!typeMatch) return false;
        if (string.IsNullOrWhiteSpace(t.NameFilter)) return true;
        return GlobMatch(t.NameFilter, evt.ContainerName)
               || GlobMatch(t.NameFilter, evt.ImageName)
               || GlobMatch(t.NameFilter, evt.Image);
    }

    private async Task RunTriggerAsync(AiTrigger t, NotificationEvent evt)
    {
        if (!await _concurrency.WaitAsync(0))
        {
            _logger?.LogWarning("AI-Trigger '{Name}' übersprungen — zu viele parallele Läufe.", t.Name);
            return;
        }
        try
        {
            var agentService = _sp.GetRequiredService<IAgentService>();
            var guardrails = _sp.GetRequiredService<IGuardrailStore>();

            var preset = guardrails.Config.Presets.FirstOrDefault(p => p.Name == t.GuardrailPreset)
                         ?? guardrails.Config.Presets.FirstOrDefault();
            var policy = preset?.Policy ?? GuardrailPolicy.SafeDefault();

            // The principal is capped at the trigger's configured ceiling (default write) so the
            // PrincipalCeilingRule constrains an unattended run — never a synthetic admin.
            var level = McpPermissionLevels.Normalize(t.MaxLevel);
            var principal = new AgentPrincipal(
                AgentPrincipalKind.McpKey, $"ai-trigger:{t.Name}", level, null,
                McpKeyId: "ai-trigger");
            var ctx = new AgentContext(Guid.NewGuid().ToString("N"), principal, AgentOrigin.Trigger, policy);

            var session = await agentService.StartSessionAsync(ctx);
            var sb = new StringBuilder();
            await foreach (var ev in session.SendAsync(BuildTaskMessage(t, evt)))
            {
                switch (ev)
                {
                    case AgentEvent.AssistantDelta d:
                        sb.Append(d.Text);
                        break;
                    case AgentEvent.ConfirmationRequired c:
                        // An action that surfaces as Confirm is above the preset's autonomous level. No human
                        // is watching a trigger run, so it must be denied, never auto-approved.
                        await session.ResolveConfirmationAsync(c.Call.Id, false);
                        sb.AppendLine($"[nicht autonom erlaubt: {c.Call.Name}]");
                        break;
                    case AgentEvent.Failed f:
                        sb.Append($"\n[Fehler] {f.Message}");
                        break;
                }
            }

            var summary = sb.ToString().Trim();
            if (summary.Length > MaxSummaryChars) summary = summary[..MaxSummaryChars] + "…";
            _logger?.LogInformation("AI-Trigger '{Name}' lief ({Event} @ {Container}).", t.Name, evt.EventType, evt.ContainerName);

            await TryAuditAsync(t, evt);
            await TryNotifyAsync(t, evt, summary);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AI-Trigger '{Name}' fehlgeschlagen.", t.Name);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static string BuildTaskMessage(AiTrigger t, NotificationEvent evt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AUTONOMOUS TRIGGER RUN (no human is waiting to answer follow-up questions).");
        sb.AppendLine($"Event: {AiTriggerEvents.Label(evt.EventType)} ({evt.EventType}).");
        if (!string.IsNullOrWhiteSpace(evt.ContainerName))
            sb.AppendLine($"Container: {evt.ContainerName} ({evt.Image}).");
        if (evt.ExitCode is { } ec) sb.AppendLine($"Exit code: {ec}.");
        if (evt.RestartCount is { } rc) sb.AppendLine($"Restart/trigger count: {rc}.");
        if (!string.IsNullOrWhiteSpace(evt.ImageInfo)) sb.AppendLine($"Info: {evt.ImageInfo}.");
        sb.AppendLine();
        sb.AppendLine("TASK:");
        sb.Append(string.IsNullOrWhiteSpace(t.Prompt)
            ? "Analyze the event and take the necessary actions within the bounds of the guardrails."
            : t.Prompt);
        return sb.ToString();
    }

    private async Task TryAuditAsync(AiTrigger t, NotificationEvent evt)
    {
        try
        {
            var audit = _sp.GetRequiredService<IAuditLogService>();
            await audit.LogAsync($"ai-trigger:{t.Name}", "trigger", "agent.run", "ai-trigger", t.Id, t.Name,
                details: $"{evt.EventType} @ {evt.ContainerName}");
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "AI-Trigger Audit fehlgeschlagen."); }
    }

    private async Task TryNotifyAsync(AiTrigger t, NotificationEvent evt, string summary)
    {
        try
        {
            var notify = _sp.GetRequiredService<INotificationService>();
            await notify.SendAsync(new NotificationEvent
            {
                EventType = "agent_action",
                ContainerId = evt.ContainerId,
                ContainerName = evt.ContainerName,
                Image = evt.Image,
                ImageInfo = $"AI-Trigger '{t.Name}' auf {AiTriggerEvents.Label(evt.EventType)}:\n{summary}",
            });
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "AI-Trigger Benachrichtigung fehlgeschlagen."); }
    }

    private static bool GlobMatch(string glob, string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase);
    }
}
