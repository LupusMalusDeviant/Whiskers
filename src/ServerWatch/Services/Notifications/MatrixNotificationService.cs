using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

public class MatrixNotificationService : IMatrixNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<MatrixSettings> _settings;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<MatrixNotificationService> _logger;

    public MatrixNotificationService(
        HttpClient httpClient,
        IOptionsMonitor<MatrixSettings> settings,
        ILogger<MatrixNotificationService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _throttler = new NotificationThrottler(settings.CurrentValue.ThrottleMinutes);
        _logger = logger;
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        var config = _settings.CurrentValue;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.HomeserverUrl) ||
            string.IsNullOrWhiteSpace(config.AccessToken) || string.IsNullOrWhiteSpace(config.RoomId))
            return;

        if (_throttler.IsThrottled(evt.ContainerId, evt.EventType))
            return;

        try
        {
            var (plain, html) = FormatMessage(evt);
            await SendMatrixMessage(config, plain, html);

            _throttler.Record(evt.ContainerId, evt.EventType);
            _logger.LogInformation("Matrix notification sent: {EventType} for {Container}",
                evt.EventType, evt.ContainerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Matrix notification for {Container}", evt.ContainerName);
        }
    }

    public async Task SendTestAsync()
    {
        var config = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(config.HomeserverUrl) ||
            string.IsNullOrWhiteSpace(config.AccessToken) || string.IsNullOrWhiteSpace(config.RoomId))
            throw new InvalidOperationException("Matrix is not configured.");

        await SendMatrixMessage(config,
            "ServerWatch Test | Notification system is working correctly.",
            "<strong>ServerWatch Test</strong> | Notification system is working correctly. ✅");
    }

    private async Task SendMatrixMessage(MatrixSettings config, string plainText, string htmlText)
    {
        var txnId = Guid.NewGuid().ToString("N");
        var roomId = Uri.EscapeDataString(config.RoomId);
        var url = $"{config.HomeserverUrl.TrimEnd('/')}/_matrix/client/v3/rooms/{roomId}/send/m.room.message/{txnId}";

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken);
        request.Content = JsonContent.Create(new
        {
            msgtype = "m.text",
            body = plainText,
            format = "org.matrix.custom.html",
            formatted_body = htmlText
        });

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static (string Plain, string Html) FormatMessage(NotificationEvent evt) => evt.EventType switch
    {
        "unhealthy" => (
            $"⚠️ Container Unhealthy | {evt.ContainerName} health check is failing. Image: {evt.Image}",
            $"⚠️ <strong>Container Unhealthy</strong> | <code>{evt.ContainerName}</code> health check is failing.<br/>Image: <code>{evt.Image}</code>"
        ),
        "stopped" => (
            $"🔴 Container Stopped | {evt.ContainerName} exited with code {evt.ExitCode}. Image: {evt.Image}",
            $"🔴 <strong>Container Stopped</strong> | <code>{evt.ContainerName}</code> exited with code {evt.ExitCode}.<br/>Image: <code>{evt.Image}</code>"
        ),
        "oom_killed" => (
            $"💥 OOM Killed | {evt.ContainerName} was killed due to memory limits. Image: {evt.Image}",
            $"💥 <strong>OOM Killed</strong> | <code>{evt.ContainerName}</code> was killed due to memory limits.<br/>Image: <code>{evt.Image}</code>"
        ),
        "restart_loop" => (
            $"🔄 Restart Loop | {evt.ContainerName} has restarted {evt.RestartCount} times in {evt.WindowMinutes} minutes.",
            $"🔄 <strong>Restart Loop</strong> | <code>{evt.ContainerName}</code> has restarted {evt.RestartCount} times in {evt.WindowMinutes} minutes."
        ),
        "image_update" => (
            $"📦 Image Update Available | {evt.ContainerName} has a newer image version for {evt.ImageName}. {evt.ImageInfo}",
            $"📦 <strong>Image Update Available</strong> | <code>{evt.ContainerName}</code> has a newer image version for <code>{evt.ImageName}</code><br/>{evt.ImageInfo}"
        ),
        "cve_finding" => (
            $"🛡️ CVE Findings | {evt.ImageName} for {evt.ContainerName}. {evt.ImageInfo}",
            $"🛡️ <strong>CVE Findings</strong> | {evt.ImageName} for {evt.ContainerName}<br/>{evt.ImageInfo}"
        ),
        "agent_action" => (
            $"🤖 AI-Agent | {evt.ContainerName}. {evt.ImageInfo}",
            $"🤖 <strong>AI-Agent</strong> | <code>{evt.ContainerName}</code><br/>{evt.ImageInfo}"
        ),
        "high_cpu" => (
            $"🔥 Hohe CPU-Last | {evt.ContainerName}. {evt.ImageInfo}",
            $"🔥 <strong>Hohe CPU-Last</strong> | <code>{evt.ContainerName}</code><br/>{evt.ImageInfo}"
        ),
        "high_memory" => (
            $"🔥 Hohe RAM-Last | {evt.ContainerName}. {evt.ImageInfo}",
            $"🔥 <strong>Hohe RAM-Last</strong> | <code>{evt.ContainerName}</code><br/>{evt.ImageInfo}"
        ),
        "high_disk" => (
            $"💾 Hohe Festplatten-Last | {evt.ContainerName}. {evt.ImageInfo}",
            $"💾 <strong>Hohe Festplatten-Last</strong> | <code>{evt.ContainerName}</code><br/>{evt.ImageInfo}"
        ),
        "metric_anomaly" => (
            $"📈 Metrik-Ausreißer | {evt.ContainerName}. {evt.ImageInfo}",
            $"📈 <strong>Metrik-Ausreißer</strong> | <code>{evt.ContainerName}</code><br/>{evt.ImageInfo}"
        ),
        _ when evt.EventType.StartsWith("log_alert", StringComparison.Ordinal) => (
            $"🔍 Log-Alert | {evt.ContainerName}. {(string.IsNullOrWhiteSpace(evt.ImageInfo) ? evt.Image : evt.ImageInfo)}",
            $"🔍 <strong>Log-Alert</strong> | <code>{evt.ContainerName}</code><br/>{(string.IsNullOrWhiteSpace(evt.ImageInfo) ? evt.Image : evt.ImageInfo)}"
        ),
        _ => (
            $"ℹ️ {evt.EventType} | {evt.ContainerName}. {(string.IsNullOrWhiteSpace(evt.ImageInfo) ? $"Image: {evt.Image}" : evt.ImageInfo)}",
            $"ℹ️ <strong>{evt.EventType}</strong> | <code>{evt.ContainerName}</code><br/>{(string.IsNullOrWhiteSpace(evt.ImageInfo) ? $"Image: <code>{evt.Image}</code>" : evt.ImageInfo)}"
        )
    };
}
