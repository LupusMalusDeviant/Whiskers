using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

public class NtfyNotificationService : INtfyNotificationService
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<NtfySettings> _settings;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<NtfyNotificationService> _logger;

    public NtfyNotificationService(HttpClient http, IOptionsMonitor<NtfySettings> settings, ILogger<NtfyNotificationService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
        _throttler = new NotificationThrottler(settings.CurrentValue.ThrottleMinutes);
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        var c = _settings.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.ServerUrl) || string.IsNullOrWhiteSpace(c.Topic)) return;
        if (_throttler.IsThrottled(evt.ContainerId, evt.EventType, _settings.CurrentValue.ThrottleMinutes)) return;
        try
        {
            var (_, severity) = NotificationFormatter.Describe(evt);
            await PostAsync(c, NotificationFormatter.PlainText(evt), severity);
            _throttler.Record(evt.ContainerId, evt.EventType);
            _logger.LogInformation("ntfy notification sent: {EventType}", evt.EventType);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send ntfy notification"); }
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.ServerUrl) || string.IsNullOrWhiteSpace(c.Topic)) return;
        await PostAsync(c, "ServerWatch Test — Benachrichtigungen funktionieren.", "Success");
    }

    private async Task PostAsync(NtfySettings c, string body, string severity)
    {
        var url = $"{c.ServerUrl.TrimEnd('/')}/{c.Topic}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body) };
        // ASCII-safe headers only (the message body carries the umlaut-bearing title line).
        req.Headers.TryAddWithoutValidation("Priority", PriorityFor(severity));
        req.Headers.TryAddWithoutValidation("Tags", TagFor(severity));
        if (!string.IsNullOrWhiteSpace(c.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Token);
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    private static string PriorityFor(string s) => s switch { "Error" => "urgent", "Warning" => "high", _ => "default" };
    private static string TagFor(string s) => s switch
    {
        "Error" => "rotating_light", "Warning" => "warning", "Success" => "white_check_mark", _ => "information_source",
    };
}
