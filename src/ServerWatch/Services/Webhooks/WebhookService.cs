using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ServerWatch.Models;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Persistence;
using ServerWatch.Services.Server;
using ServerWatch.Utils;

namespace ServerWatch.Services.Webhooks;

public class WebhookService : IWebhookService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDockerService _docker;
    private readonly IHostCommandExecutor _executor;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(IServiceScopeFactory scopeFactory, IDockerService docker,
        IHostCommandExecutor executor, ILogger<WebhookService> logger)
    {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _executor = executor;
        _logger = logger;
    }

    public async Task<List<WebhookEntity>> GetWebhooksAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.Webhooks.OrderBy(w => w.Name).ToListAsync();
    }

    public async Task<WebhookEntity> CreateWebhookAsync(WebhookEntity webhook)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.Webhooks.Add(webhook);
        await db.SaveChangesAsync();
        return webhook;
    }

    public async Task DeleteWebhookAsync(string webhookId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId);
        if (webhook != null)
        {
            db.Webhooks.Remove(webhook);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Execute a webhook trigger. Validates HMAC signature if provided.</summary>
    public async Task<(bool Success, string Output)> TriggerAsync(string webhookId, string? signature = null, string? body = null, string? sourceIp = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId);
        if (webhook == null) return (false, "Webhook not found");
        if (!webhook.Enabled) return (false, "Webhook is disabled");

        // Validate HMAC. If the webhook has a secret configured, a valid signature is REQUIRED —
        // otherwise anyone who learns the (unauthenticated) webhook URL could trigger it without
        // knowing the shared secret. Compare in constant time to avoid signature-timing leaks.
        if (!string.IsNullOrEmpty(webhook.Secret))
        {
            if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(body))
                return (false, "Signature required");

            var expected = Encoding.UTF8.GetBytes($"sha256={ComputeHmac(webhook.Secret, body)}");
            var provided = Encoding.UTF8.GetBytes(signature.Trim());
            if (expected.Length != provided.Length ||
                !CryptographicOperations.FixedTimeEquals(expected, provided))
                return (false, "Invalid signature");
        }

        _logger.LogInformation("Webhook triggered: {Name} ({Action} on {Target})", webhook.Name, webhook.Action, webhook.TargetId);

        string output;
        bool success;

        try
        {
            (success, output) = webhook.Action switch
            {
                "restart" => await RestartContainer(webhook),
                "rebuild" => await RebuildContainer(webhook),
                "deploy" => await DeployCompose(webhook),
                _ => (false, $"Unknown action: {webhook.Action}")
            };
        }
        catch (Exception ex)
        {
            success = false;
            output = ex.Message;
        }

        // Log
        webhook.LastTriggered = DateTime.UtcNow;
        webhook.TriggerCount++;
        db.WebhookLogs.Add(new WebhookLogEntity
        {
            WebhookId = webhook.WebhookId,
            WebhookName = webhook.Name,
            Success = success,
            Output = success ? output : null,
            Error = success ? null : output,
            SourceIp = sourceIp
        });
        await db.SaveChangesAsync();

        return (success, output);
    }

    private async Task<(bool, string)> RestartContainer(WebhookEntity webhook)
    {
        var containers = await _docker.ListContainersAsync(serverId: webhook.ServerId);
        var container = containers.FirstOrDefault(c => c.Name == webhook.TargetId || c.Id.StartsWith(webhook.TargetId));
        if (container == null) return (false, $"Container '{webhook.TargetId}' not found");

        await _docker.RestartContainerAsync(container.Id, webhook.ServerId);
        return (true, $"Container {container.Name} restarted.");
    }

    private async Task<(bool, string)> RebuildContainer(WebhookEntity webhook)
    {
        var containers = await _docker.ListContainersAsync(serverId: webhook.ServerId);
        var container = containers.FirstOrDefault(c => c.Name == webhook.TargetId || c.Id.StartsWith(webhook.TargetId));
        if (container == null) return (false, $"Container '{webhook.TargetId}' not found");

        var newId = await _docker.RecreateContainerAsync(container.Id, webhook.ServerId);
        return (true, $"Container {container.Name} rebuilt (new ID: {newId[..12]}).");
    }

    private async Task<(bool, string)> DeployCompose(WebhookEntity webhook)
    {
        var sid = webhook.ServerId ?? "local";
        // TargetId is the compose project directory. Require an absolute path and single-quote it for the
        // host shell — otherwise a crafted value would be executed as an arbitrary root command (and paths
        // with spaces would break). Every other host-command site in the codebase quotes; this one must too.
        var dir = webhook.TargetId ?? "";
        if (!dir.StartsWith('/'))
            return (false, "Deploy target must be an absolute path.");
        var result = await _executor.ExecuteAsync(sid,
            $"cd {ShellUtils.Quote(dir)} && docker compose pull 2>&1 && docker compose up -d 2>&1",
            TimeSpan.FromMinutes(5));
        return (result.Success, result.Success ? result.Output : result.Error);
    }

    private static string ComputeHmac(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }
}
