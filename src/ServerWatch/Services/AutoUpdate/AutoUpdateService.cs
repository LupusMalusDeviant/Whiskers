using Microsoft.EntityFrameworkCore;
using ServerWatch.Models;
using ServerWatch.Services.Docker;
using ServerWatch.Services.ImageUpdate;
using ServerWatch.Services.Notifications;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.AutoUpdate;

/// <summary>
/// Background service that auto-updates containers WITH OPT-IN policies.
/// Default: OFF. Each container must explicitly enable auto-update.
/// </summary>
public class AutoUpdateService : BackgroundService, IAutoUpdateService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDockerService _docker;
    private readonly IImageUpdateStore _updateStore;
    private readonly INotificationService _notifications;
    private readonly ILogger<AutoUpdateService> _logger;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    public AutoUpdateService(
        IServiceScopeFactory scopeFactory,
        IDockerService docker,
        IImageUpdateStore updateStore,
        INotificationService notifications,
        ILogger<AutoUpdateService> logger)
    {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _updateStore = updateStore;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auto-update service started (opt-in only). Check interval: {Interval}m", CheckInterval.TotalMinutes);
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndUpdateAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Auto-update check failed");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckAndUpdateAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var policies = await db.UpdatePolicies.Where(p => p.AutoUpdate).ToListAsync(ct);
        if (!policies.Any()) return;

        var containers = await _docker.ListAllContainersAsync(all: false);

        foreach (var policy in policies)
        {
            if (ct.IsCancellationRequested) break;

            var now = DateTime.UtcNow;
            if (policy.LastChecked != null &&
                now - policy.LastChecked.Value < TimeSpan.FromMinutes(policy.CheckIntervalMinutes))
                continue;

            policy.LastChecked = now;

            var container = containers.FirstOrDefault(c => c.Name == policy.ContainerName || c.Id == policy.ContainerId);
            if (container == null) continue;

            // Check if update is available
            var updateInfo = _updateStore.Get(container.Id, container.ServerId);
            if (updateInfo?.UpdateAvailable != true)
            {
                await db.SaveChangesAsync(ct);
                continue;
            }

            _logger.LogInformation("Auto-update triggered for {Container} (opt-in policy)", container.Name);

            // Notify before update
            if (policy.NotifyBeforeUpdate)
            {
                await _notifications.SendAsync(new NotificationEvent
                {
                    ContainerId = container.Id,
                    ContainerName = container.Name,
                    Image = container.Image,
                    EventType = "auto_update_start"
                });
            }

            // Perform update
            var history = new UpdateHistoryEntity
            {
                ContainerId = container.Id,
                ContainerName = container.Name,
                ServerId = container.ServerId,
                OldImageDigest = updateInfo.LocalDigest ?? "",
                NewImageDigest = updateInfo.RemoteDigest ?? "",
                Image = container.Image,
                StartedAt = DateTime.UtcNow
            };

            try
            {
                var progress = new Progress<string>(msg => _logger.LogDebug("Update {Container}: {Msg}", container.Name, msg));
                var newId = await _docker.RecreateContainerAsync(container.Id, container.ServerId == "local" ? null : container.ServerId, progress);

                // Wait for health check (if container has one)
                await Task.Delay(TimeSpan.FromSeconds(15), ct);

                // Check health
                var (state, _, oomKilled) = await _docker.InspectContainerStateAsync(newId, container.ServerId == "local" ? null : container.ServerId);

                if (state != "running" || oomKilled)
                {
                    throw new Exception($"Container unhealthy after update: state={state}, oom={oomKilled}");
                }

                history.Success = true;
                history.CompletedAt = DateTime.UtcNow;
                policy.LastUpdated = DateTime.UtcNow;
                policy.LastUpdateResult = "OK";

                _updateStore.Remove(container.Id, container.ServerId);
                _logger.LogInformation("Auto-update succeeded for {Container}", container.Name);
            }
            catch (Exception ex)
            {
                history.Success = false;
                history.Error = ex.Message;
                history.CompletedAt = DateTime.UtcNow;
                policy.LastUpdateResult = $"FEHLER: {ex.Message}";

                _logger.LogError(ex, "Auto-update failed for {Container}", container.Name);

                // TODO: Rollback would go here — requires storing old image reference
                // For now, notify about failure
                await _notifications.SendAsync(new NotificationEvent
                {
                    ContainerId = container.Id,
                    ContainerName = container.Name,
                    Image = container.Image,
                    EventType = "auto_update_failed"
                });
            }

            db.UpdateHistory.Add(history);
            await db.SaveChangesAsync(ct);
        }
    }

    // === Public API ===

    public async Task<List<UpdatePolicyEntity>> GetPoliciesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.UpdatePolicies.OrderBy(p => p.ContainerName).ToListAsync();
    }

    public async Task SetPolicyAsync(UpdatePolicyEntity policy)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var existing = await db.UpdatePolicies.FirstOrDefaultAsync(p => p.ContainerId == policy.ContainerId);
        if (existing != null)
        {
            existing.AutoUpdate = policy.AutoUpdate;
            existing.AutoRollback = policy.AutoRollback;
            existing.NotifyBeforeUpdate = policy.NotifyBeforeUpdate;
            existing.CheckIntervalMinutes = policy.CheckIntervalMinutes;
        }
        else
        {
            db.UpdatePolicies.Add(policy);
        }
        await db.SaveChangesAsync();
    }

    public async Task<List<UpdateHistoryEntity>> GetHistoryAsync(string? containerId = null, int limit = 20)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        IQueryable<UpdateHistoryEntity> query = db.UpdateHistory;
        if (!string.IsNullOrEmpty(containerId))
            query = query.Where(h => h.ContainerId == containerId);
        return await query.OrderByDescending(h => h.StartedAt).Take(limit).ToListAsync();
    }
}
