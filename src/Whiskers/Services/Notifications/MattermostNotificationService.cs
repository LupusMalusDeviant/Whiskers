using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Services.Notifications;

public class MattermostNotificationService : IMattermostNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<MattermostSettings> _settings;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<MattermostNotificationService> _logger;

    public MattermostNotificationService(
        HttpClient httpClient,
        IOptionsMonitor<MattermostSettings> settings,
        ILogger<MattermostNotificationService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _throttler = new NotificationThrottler(settings.CurrentValue.ThrottleMinutes);
        _logger = logger;
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        var config = _settings.CurrentValue;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.WebhookUrl))
            return;

        if (_throttler.IsThrottled(evt.ContainerId, evt.EventType, _settings.CurrentValue.ThrottleMinutes))
            return;

        try
        {
            var payload = new
            {
                channel = string.IsNullOrWhiteSpace(config.Channel) ? (string?)null : config.Channel,
                username = config.BotUsername,
                icon_url = string.IsNullOrWhiteSpace(config.BotIconUrl) ? (string?)null : config.BotIconUrl,
                text = FormatMessage(evt)
            };

            var response = await _httpClient.PostAsJsonAsync(config.WebhookUrl, payload);
            response.EnsureSuccessStatusCode();

            _throttler.Record(evt.ContainerId, evt.EventType);
            _logger.LogInformation("Mattermost notification sent: {EventType} for {Container}",
                evt.EventType, evt.ContainerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Mattermost notification for {Container}", evt.ContainerName);
        }
    }

    public async Task SendTestAsync()
    {
        var config = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(config.WebhookUrl))
            return;

        var payload = new
        {
            channel = string.IsNullOrWhiteSpace(config.Channel) ? (string?)null : config.Channel,
            username = config.BotUsername,
            icon_url = string.IsNullOrWhiteSpace(config.BotIconUrl) ? (string?)null : config.BotIconUrl,
            text = ":white_check_mark: **Whiskers Test** | Notification system is working correctly."
        };

        var response = await _httpClient.PostAsJsonAsync(config.WebhookUrl, payload);
        response.EnsureSuccessStatusCode();
    }

    private static string FormatMessage(NotificationEvent evt) => evt.EventType switch
    {
        "unhealthy" => $":warning: **Container Unhealthy** | `{evt.ContainerName}` health check is failing.\n> Image: `{evt.Image}`",
        "stopped" => $":red_circle: **Container Stopped** | `{evt.ContainerName}` exited with code {evt.ExitCode}.\n> Image: `{evt.Image}`",
        "oom_killed" => $":boom: **OOM Killed** | `{evt.ContainerName}` was killed due to memory limits.\n> Image: `{evt.Image}`",
        "restart_loop" => $":arrows_counterclockwise: **Restart Loop** | `{evt.ContainerName}` has restarted {evt.RestartCount} times in {evt.WindowMinutes} minutes.\n> Image: `{evt.Image}`",
        "image_update" => $":arrows_counterclockwise: **Image Update Available** | `{evt.ContainerName}` has a newer image version for `{evt.ImageName}`\n> {evt.ImageInfo}",
        "cve_finding" => $":shield: **CVE Findings** | {evt.ImageName} for {evt.ContainerName}\n> {evt.ImageInfo}",
        "agent_action" => $":robot_face: **AI-Agent** | `{evt.ContainerName}`\n> {evt.ImageInfo}",
        "high_cpu" => $":fire: **High CPU Load** | `{evt.ContainerName}`\n> {evt.ImageInfo}",
        "high_memory" => $":fire: **High Memory Load** | `{evt.ContainerName}`\n> {evt.ImageInfo}",
        "high_disk" => $":floppy_disk: **High Disk Usage** | `{evt.ContainerName}`\n> {evt.ImageInfo}",
        "metric_anomaly" => $":chart_with_upwards_trend: **Metric Anomaly** | `{evt.ContainerName}`\n> {evt.ImageInfo}",
        _ when evt.EventType.StartsWith("log_alert", StringComparison.Ordinal) =>
            $":mag: **Log-Alert** | `{evt.ContainerName}`\n> {(string.IsNullOrWhiteSpace(evt.ImageInfo) ? evt.Image : evt.ImageInfo)}",
        // Fallback: still surface any detail text instead of dropping it.
        _ => $":information_source: **{evt.EventType}** | `{evt.ContainerName}`\n> {(string.IsNullOrWhiteSpace(evt.ImageInfo) ? $"Image: `{evt.Image}`" : evt.ImageInfo)}"
    };
}
