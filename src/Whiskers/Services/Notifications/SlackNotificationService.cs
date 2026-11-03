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

    public async Task<bool> DeliverAsync(NotificationEvent evt, CancellationToken ct)
    {
        var c = _settings.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.WebhookUrl)) return false;
        if (!evt.IsRedelivery && _throttler.IsThrottled(evt, _settings.CurrentValue.ThrottleMinutes)) return false;
        var (title, severity) = NotificationFormatter.Describe(evt);
        await PostAsync(c, title, NotificationFormatter.Detail(evt), severity, ct);
        _throttler.Record(evt);
        _logger.LogInformation("Slack notification sent: {EventType}", evt.EventType);
        return true;
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.WebhookUrl)) return;
        await PostAsync(c, "Whiskers Test", "Benachrichtigungen funktionieren.", "Success");
    }

    private async Task PostAsync(SlackSettings c, string title, string description, string severity,
        CancellationToken ct = default)
    {
        var payload = new
        {
            attachments = new[]
            {
                new { color = ColorFor(severity), title, text = string.IsNullOrWhiteSpace(description) ? "—" : description }
            }
        };
        using var resp = await _http.PostAsJsonAsync(c.WebhookUrl, payload, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static string ColorFor(string s) => s switch
    {
        "Error" => "danger", "Warning" => "warning", "Success" => "good", _ => "#1E88E5",
    };
}
