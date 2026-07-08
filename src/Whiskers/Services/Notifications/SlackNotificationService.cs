using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Services.Notifications;

public class SlackNotificationService : ISlackNotificationService
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<SlackSettings> _settings;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<SlackNotificationService> _logger;

    public SlackNotificationService(HttpClient http, IOptionsMonitor<SlackSettings> settings, ILogger<SlackNotificationService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
        _throttler = new NotificationThrottler(settings.CurrentValue.ThrottleMinutes);
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        var c = _settings.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.WebhookUrl)) return;
        if (_throttler.IsThrottled(evt.ContainerId, evt.EventType, _settings.CurrentValue.ThrottleMinutes)) return;
        try
        {
            var (title, severity) = NotificationFormatter.Describe(evt);
            await PostAsync(c, title, NotificationFormatter.Detail(evt), severity);
            _throttler.Record(evt.ContainerId, evt.EventType);
            _logger.LogInformation("Slack notification sent: {EventType}", evt.EventType);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send Slack notification"); }
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.WebhookUrl)) return;
        await PostAsync(c, "Whiskers Test", "Benachrichtigungen funktionieren.", "Success");
    }

    private async Task PostAsync(SlackSettings c, string title, string description, string severity)
    {
        var payload = new
        {
            attachments = new[]
            {
                new { color = ColorFor(severity), title, text = string.IsNullOrWhiteSpace(description) ? "—" : description }
            }
        };
        var resp = await _http.PostAsJsonAsync(c.WebhookUrl, payload);
        resp.EnsureSuccessStatusCode();
    }

    private static string ColorFor(string s) => s switch
    {
        "Error" => "danger", "Warning" => "warning", "Success" => "good", _ => "#1E88E5",
    };
}
