using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Agent.Triggers;

namespace Whiskers.Services.Notifications;

/// <summary>
/// Delegates notifications to all configured providers (Mattermost, Matrix, etc.).
/// Each provider handles its own enabled/disabled check independently. Also feeds every
/// event to the AI-trigger dispatcher (resolved lazily to avoid a DI cycle).
/// </summary>
public class CompositeNotificationService : INotificationService
{
    private readonly IMattermostNotificationService _mattermost;
    private readonly IMatrixNotificationService _matrix;
    private readonly ITelegramNotificationService _telegram;
    private readonly INtfyNotificationService _ntfy;
    private readonly IDiscordNotificationService _discord;
    private readonly ISlackNotificationService _slack;
    private readonly IEmailNotificationService _email;
    private readonly IWebhookNotificationService _webhook;
    private readonly IInAppNotificationStore _inApp;
    private readonly IServiceProvider _sp;
    private readonly ILogger<CompositeNotificationService> _logger;

    public CompositeNotificationService(
        IMattermostNotificationService mattermost,
        IMatrixNotificationService matrix,
        ITelegramNotificationService telegram,
        INtfyNotificationService ntfy,
        IDiscordNotificationService discord,
        ISlackNotificationService slack,
        IEmailNotificationService email,
        IWebhookNotificationService webhook,
        IInAppNotificationStore inApp,
        IServiceProvider sp,
        ILogger<CompositeNotificationService> logger)
    {
        _mattermost = mattermost;
        _matrix = matrix;
        _telegram = telegram;
        _ntfy = ntfy;
        _discord = discord;
        _slack = slack;
        _email = email;
        _webhook = webhook;
        _inApp = inApp;
        _sp = sp;
        _logger = logger;
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        // Always record in the in-app feed (no external channel needed).
        try { _inApp.Add(evt); } catch (Exception ex) { _logger.LogWarning(ex, "In-app notification failed"); }

        var tasks = new List<Task>();

        tasks.Add(SafeSend("Mattermost", () => _mattermost.SendAsync(evt)));
        tasks.Add(SafeSend("Matrix", () => _matrix.SendAsync(evt)));
        tasks.Add(SafeSend("Telegram", () => _telegram.SendAsync(evt)));
        tasks.Add(SafeSend("ntfy", () => _ntfy.SendAsync(evt)));
        tasks.Add(SafeSend("Discord", () => _discord.SendAsync(evt)));
        tasks.Add(SafeSend("Slack", () => _slack.SendAsync(evt)));
        tasks.Add(SafeSend("Email", () => _email.SendAsync(evt)));
        tasks.Add(SafeSend("Webhook", () => _webhook.SendAsync(evt)));
        tasks.Add(SafeSend("AI-Trigger", () => _sp.GetRequiredService<IAiTriggerDispatcher>().OnEventAsync(evt)));

        await Task.WhenAll(tasks);
    }

    public async Task SendTestAsync()
    {
        // Send test to all enabled providers
        var errors = new List<string>();

        try { await _mattermost.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"Mattermost: {ex.Message}"); }

        try { await _matrix.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"Matrix: {ex.Message}"); }

        try { await _telegram.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"Telegram: {ex.Message}"); }

        try { await _ntfy.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"ntfy: {ex.Message}"); }

        try { await _discord.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"Discord: {ex.Message}"); }

        try { await _slack.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"Slack: {ex.Message}"); }

        try { await _email.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"Email: {ex.Message}"); }

        try { await _webhook.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"Webhook: {ex.Message}"); }

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
