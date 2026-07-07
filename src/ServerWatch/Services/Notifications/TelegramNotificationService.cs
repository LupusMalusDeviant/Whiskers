using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

public class TelegramNotificationService : ITelegramNotificationService
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<TelegramSettings> _settings;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(HttpClient http, IOptionsMonitor<TelegramSettings> settings, ILogger<TelegramNotificationService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
        _throttler = new NotificationThrottler(settings.CurrentValue.ThrottleMinutes);
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        var c = _settings.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.BotToken) || string.IsNullOrWhiteSpace(c.ChatId)) return;
        if (_throttler.IsThrottled(evt.ContainerId, evt.EventType, _settings.CurrentValue.ThrottleMinutes)) return;
        try
        {
            await PostAsync(c, NotificationFormatter.PlainText(evt));
            _throttler.Record(evt.ContainerId, evt.EventType);
            _logger.LogInformation("Telegram notification sent: {EventType}", evt.EventType);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send Telegram notification"); }
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.BotToken) || string.IsNullOrWhiteSpace(c.ChatId)) return;
        await PostAsync(c, "✅ ServerWatch Test — Benachrichtigungen funktionieren.");
    }

    private async Task PostAsync(TelegramSettings c, string text)
    {
        var url = $"https://api.telegram.org/bot{c.BotToken}/sendMessage";
        var resp = await _http.PostAsJsonAsync(url, new { chat_id = c.ChatId, text, disable_web_page_preview = true });
        resp.EnsureSuccessStatusCode();
    }
}
