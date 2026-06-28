using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

public class DiscordNotificationService : IDiscordNotificationService
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<DiscordSettings> _settings;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(HttpClient http, IOptionsMonitor<DiscordSettings> settings, ILogger<DiscordNotificationService> logger)
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
        if (_throttler.IsThrottled(evt.ContainerId, evt.EventType)) return;
        try
        {
            var (title, severity) = NotificationFormatter.Describe(evt);
            await PostAsync(c, title, NotificationFormatter.Detail(evt), severity);
            _throttler.Record(evt.ContainerId, evt.EventType);
            _logger.LogInformation("Discord notification sent: {EventType}", evt.EventType);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send Discord notification"); }
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.WebhookUrl)) return;
        await PostAsync(c, "ServerWatch Test", "Benachrichtigungen funktionieren.", "Success");
    }

    private async Task PostAsync(DiscordSettings c, string title, string description, string severity)
    {
        var payload = new
        {
            username = "ServerWatch",
            embeds = new[]
            {
                new { title, description = string.IsNullOrWhiteSpace(description) ? "—" : description, color = ColorFor(severity) }
            }
        };
        var resp = await _http.PostAsJsonAsync(c.WebhookUrl, payload);
        resp.EnsureSuccessStatusCode();
    }

    private static int ColorFor(string s) => s switch
    {
        "Error" => 0xE53935, "Warning" => 0xFB8C00, "Success" => 0x43A047, _ => 0x1E88E5,
    };
}
