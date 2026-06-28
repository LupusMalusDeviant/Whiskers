using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

/// <summary>Generic outbound webhook: POSTs a JSON event to a configured URL on every notification.</summary>
public class WebhookNotificationService : IWebhookNotificationService
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<WebhookNotificationSettings> _settings;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<WebhookNotificationService> _logger;

    public WebhookNotificationService(HttpClient http, IOptionsMonitor<WebhookNotificationSettings> settings, ILogger<WebhookNotificationService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
        _throttler = new NotificationThrottler(settings.CurrentValue.ThrottleMinutes);
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        var c = _settings.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.Url)) return;
        if (_throttler.IsThrottled(evt.ContainerId, evt.EventType)) return;
        try
        {
            await PostAsync(c, evt);
            _throttler.Record(evt.ContainerId, evt.EventType);
            _logger.LogInformation("Webhook notification sent: {EventType}", evt.EventType);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send webhook notification"); }
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.Url)) return;
        await PostAsync(c, new NotificationEvent { EventType = "test", ContainerName = "ServerWatch", ImageInfo = "Benachrichtigungen funktionieren." });
    }

    private async Task PostAsync(WebhookNotificationSettings c, NotificationEvent evt)
    {
        var (title, severity) = NotificationFormatter.Describe(evt);
        var payload = new
        {
            @event = evt.EventType,
            title,
            detail = NotificationFormatter.Detail(evt),
            severity,
            container = evt.ContainerName,
            image = evt.Image,
            timestamp = evt.Timestamp,
        };
        var resp = await _http.PostAsJsonAsync(c.Url, payload);
        resp.EnsureSuccessStatusCode();
    }
}
