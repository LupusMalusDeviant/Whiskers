using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Agent.Triggers;

namespace Whiskers.Services.Notifications;

/// <summary>
/// Fans a notification out to every configured channel (Mattermost, Matrix, Telegram, …) plus the in-app
/// feed, and feeds every event to the AI-trigger dispatcher (resolved lazily to avoid a DI cycle). Channels
/// arrive as <c>IEnumerable&lt;INotificationChannel&gt;</c> (changeme C9) instead of being hard-wired, so
/// adding/removing a channel is a registration change only. Each channel does its own enabled/disabled check.
/// </summary>
public class CompositeNotificationService : INotificationService
{
    private readonly IReadOnlyList<INotificationChannel> _channels;
    private readonly IInAppNotificationStore _inApp;
    private readonly IServiceProvider _sp;
    private readonly ILogger<CompositeNotificationService> _logger;

    public CompositeNotificationService(
        IEnumerable<INotificationChannel> channels,
        IInAppNotificationStore inApp,
        IServiceProvider sp,
        ILogger<CompositeNotificationService> logger)
    {
        _channels = channels.ToList();
        _inApp = inApp;
        _sp = sp;
        _logger = logger;
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        // Always record in the in-app feed (no external channel needed).
        try { _inApp.Add(evt); } catch (Exception ex) { _logger.LogWarning(ex, "In-app notification failed"); }

        var tasks = _channels.Select(c => SafeSend(c.Name, () => c.SendAsync(evt))).ToList();
        // AI-trigger dispatch runs alongside the channels; lazily resolved to avoid a DI cycle.
        tasks.Add(SafeSend("AI-Trigger", () => _sp.GetRequiredService<IAiTriggerDispatcher>().OnEventAsync(evt)));

        await Task.WhenAll(tasks);
    }

    public async Task SendTestAsync()
    {
        var errors = new List<string>();
        foreach (var channel in _channels)
        {
            try { await channel.SendTestAsync(); }
            catch (Exception ex) { errors.Add($"{channel.Name}: {ex.Message}"); }
        }

        if (errors.Count > 0)
            throw new AggregateException($"Some providers failed: {string.Join("; ", errors)}");
    }

    private async Task SafeSend(string provider, Func<Task> action)
    {
        // Retry once on failure; the per-client 15s HttpClient timeout (Program.cs) bounds each attempt so a
        // slow endpoint can't stall the loop. Log only the provider name (never the payload/URL).
        var (ok, last) = await NotificationRetry.TrySendAsync(action, maxAttempts: 2);
        if (!ok)
            _logger.LogError(last, "Notification provider {Provider} failed after retry", provider);
    }
}
