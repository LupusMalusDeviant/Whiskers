using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.AutoUpdate;

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
                // WaitAsync bounds the cycle to shutdown: RecreateContainerAsync pulls an image and can
                // run for minutes with no cancellation token, so abandon it on stop rather than block.
                await CheckAndUpdateAsync(stoppingToken).WaitAsync(stoppingToken);
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

            var container = containers.FirstOrDefault(c => MatchesPolicy(c, policy));
            if (container == null) continue;

            // Check if update is available
            var updateInfo = _updateStore.Get(container.Id, container.ServerId);
            if (updateInfo?.UpdateAvailable != true)
            {
                await db.SaveChangesAsync(ct);
                continue;
            }

            _logger.LogInformation("Auto-update triggered for {Container} (opt-in policy)", container.Name);

            // Notify before update — a notification failure must not abort the update.
            if (policy.NotifyBeforeUpdate)
            {
                try
                {
                    await _notifications.SendAsync(new NotificationEvent
                    {
                        ContainerId = container.Id,
                        ContainerName = container.Name,
                        Image = container.Image,
                        EventType = "auto_update_start"
                    });
                }
                catch (Exception nex) { _logger.LogWarning(nex, "Auto-update start notification failed for {Container}", container.Name); }
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
                // C12: capture + PERSIST the pre-update snapshot BEFORE the recreate pulls the new image and
                // drops the old container, so a failed update can be rolled back from the UI (and the snapshot
                // survives even a crash mid-update). Best-effort — a snapshot failure must never abort the update.
                try { await CaptureSnapshotAsync(container); }
                catch (Exception cex) { _logger.LogWarning(cex, "Rollback snapshot capture failed for {Container}", container.Name); }

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
                // Notify about failure — wrapped so a notify error can't skip the history save below.
                try
                {
                    await _notifications.SendAsync(new NotificationEvent
                    {
                        ContainerId = container.Id,
                        ContainerName = container.Name,
                        Image = container.Image,
                        EventType = "auto_update_failed"
                    });
                }
                catch (Exception nex) { _logger.LogWarning(nex, "Auto-update failure notification failed for {Container}", container.Name); }
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

    // Match a container to an auto-update policy. Scoped by ServerId (empty ServerId = any server, for
    // back-compat) so a same-named container on another host is never recreated; id-match before name-match.
    public static bool MatchesPolicy(ContainerInfo container, UpdatePolicyEntity policy)
        => (string.IsNullOrEmpty(policy.ServerId) || container.ServerId == policy.ServerId)
           && (container.Id == policy.ContainerId || container.Name == policy.ContainerName);

    public async Task SetPolicyAsync(UpdatePolicyEntity policy)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var existing = await db.UpdatePolicies.FirstOrDefaultAsync(
            p => p.ContainerId == policy.ContainerId && p.ServerId == policy.ServerId);
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

    // === C12 manual rollback ===

    // Capture + persist a container's pre-update snapshot (old image id + full config). Called right before an
    // update from BOTH the auto-updater above and the manual Dashboard update path, so either can be rolled
    // back. Own scope + immediate save so the snapshot is durable before the recreate touches the container.
    public async Task CaptureSnapshotAsync(ContainerInfo container)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var (oldImageId, cfg) = await _docker.CaptureRollbackSnapshotAsync(
            container.Id, container.ServerId == "local" ? null : container.ServerId);
        await UpsertRollbackAsync(db, container, oldImageId, cfg, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    // Upsert the pre-update snapshot keyed by (container NAME, server): the name is stable across updates (the
    // container ID changes on each recreate), so exactly one snapshot is kept per container — the latest good
    // pre-update state. Saved by the caller together with the update-history row.
    private static async Task UpsertRollbackAsync(MetricsDbContext db, ContainerInfo container, string oldImageId, string configJson, CancellationToken ct)
    {
        var existing = await db.UpdateRollbacks.FirstOrDefaultAsync(
            r => r.ContainerName == container.Name && r.ServerId == container.ServerId, ct);
        if (existing != null)
        {
            existing.ContainerId = container.Id;
            existing.OldImageRef = oldImageId;
            existing.ConfigJson = configJson;
            existing.CapturedAt = DateTime.UtcNow;
        }
        else
        {
            db.UpdateRollbacks.Add(new UpdateRollbackEntity
            {
                ContainerId = container.Id,
                ContainerName = container.Name,
                ServerId = container.ServerId,
                OldImageRef = oldImageId,
                ConfigJson = configJson,
                CapturedAt = DateTime.UtcNow
            });
        }
    }

    public async Task<List<UpdateRollbackEntity>> GetRollbacksAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.UpdateRollbacks.OrderByDescending(r => r.CapturedAt).ToListAsync();
    }

    public async Task<string> RollbackAsync(long rollbackId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var snap = await db.UpdateRollbacks.FirstOrDefaultAsync(r => r.Id == rollbackId)
                   ?? throw new InvalidOperationException("No rollback snapshot found.");

        var progress = new Progress<string>(msg => _logger.LogDebug("Rollback {Container}: {Msg}", snap.ContainerName, msg));
        await _docker.RollbackContainerAsync(
            snap.ContainerName, snap.OldImageRef, snap.ConfigJson,
            snap.ServerId == "local" ? null : snap.ServerId, progress);

        // Mark the latest update-history row for this container as rolled back.
        var hist = await db.UpdateHistory
            .Where(h => h.ContainerName == snap.ContainerName && h.ServerId == snap.ServerId)
            .OrderByDescending(h => h.StartedAt).FirstOrDefaultAsync();
        if (hist != null) hist.RolledBack = true;

        // The snapshot is consumed — the container now runs the OLD image again, so there is nothing newer to
        // roll back to until the next update captures a fresh snapshot.
        db.UpdateRollbacks.Remove(snap);
        await db.SaveChangesAsync();

        _logger.LogInformation("Manual rollback of {Container} to the previous image completed", snap.ContainerName);
        return $"{snap.ContainerName} was rolled back to the previous image.";
    }
}
