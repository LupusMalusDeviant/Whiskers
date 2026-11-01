using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.AutoUpdate;

/// <summary>
/// Background service that auto-updates containers WITH OPT-IN policies.
/// Default: OFF. Each container must explicitly enable auto-update.
/// </summary>
public class AutoUpdateService : Whiskers.Services.FleetBackgroundService, IAutoUpdateService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDockerService _docker;
    private readonly IImageUpdateStore _updateStore;
    private readonly INotificationService _notifications;
    private readonly ILogger<AutoUpdateService> _logger;
    private readonly Whiskers.Services.Observability.SelfMetrics.ISelfMetrics _selfMetrics;
    private readonly IAutomationMutationRunner _automation;
    private readonly IAutomationOwnerStore _owners;
    // R1-31 (INV-52): a snapshot holds the container's environment, so it is stored encrypted with a vault-derived key.
    private readonly Whiskers.Services.Vault.IVaultService? _vault;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    public AutoUpdateService(
        IServiceScopeFactory scopeFactory,
        IDockerService docker,
        IImageUpdateStore updateStore,
        INotificationService notifications,
        Whiskers.Services.Observability.SelfMetrics.ISelfMetrics selfMetrics,
        IAutomationMutationRunner automation,
        IAutomationOwnerStore owners,
        ILogger<AutoUpdateService> logger,
        Whiskers.Services.Vault.IVaultService? vault = null)
    {
        _vault = vault;
        _automation = automation;
        _owners = owners;
        _scopeFactory = scopeFactory;
        _docker = docker;
        _updateStore = updateStore;
        _notifications = notifications;
        _selfMetrics = selfMetrics;
        _logger = logger;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auto-update service started (opt-in only). Check interval: {Interval}m", CheckInterval.TotalMinutes);
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            var success = true;
            try
            {
                // WaitAsync bounds the cycle to shutdown: RecreateContainerAsync pulls an image and can
                // run for minutes with no cancellation token, so abandon it on stop rather than block.
                await CheckAndUpdateAsync(stoppingToken).WaitAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                success = false;
                _logger.LogError(ex, "Auto-update check failed");
            }

            // Reports like every other loop, so a stalled auto-updater shows up on /self-status and in the
            // supervisor instead of looking like "no updates due".
            _selfMetrics.RecordCycle("autoupdate", "*", DateTime.UtcNow - startedAt, success, CheckInterval);

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    /// <summary>One check over every opt-in policy. Internal for tests.</summary>
    internal async Task CheckAndUpdateAsync(CancellationToken ct)
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
            var noRollback = false;
            // R1-36: set once the recreate was started — from then on a failed update is rolled back (if enabled).
            var recreateStarted = false;
            AutomationActor? updateActor = null;
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
                // A broken owner file must not read as "no owner" (that would be the grace period): fail closed.
                if (_owners.IsBlocked(IAutomationOwnerStore.AutoUpdate))
                    throw new InvalidOperationException(
                        "AutoUpdate is blocked (its owner could not be read or lost their authority); take it over again in Settings → Automation owners.");

                var progress = new Progress<string>(msg => _logger.LogDebug("Update {Container}: {Msg}", container.Name, msg));
                // Through the gateway on the authority of the admin who took AutoUpdate over (PC-D6), re-checked
                // per update; without one, the grace rule of AutomationOptions.RequireOwner applies.
                // The server the container was listed on, by its id: "local" is a server id like any other, and null
                // would be the default server, which need not be the same machine (R1-06).
                var sid = container.ServerId;
                // Only an admin taking it over in Settings records the owner, so the owner is also its provenance.
                var updateOwner = _owners.Get(IAutomationOwnerStore.AutoUpdate)?.OwnerId;
                var actor = new AutomationActor("autoupdate", "AutoUpdate", updateOwner, updateOwner);
                updateActor = actor;
                var outcome = await _automation.RunAsync(actor,
                    "container.update", new { containerId = container.Id, name = container.Name, serverId = sid },
                    async _ =>
                    {
                        // C12: capture + PERSIST the pre-update snapshot BEFORE the recreate pulls the new image and
                        // drops the old container, so a failed update can be rolled back (and the snapshot survives
                        // even a crash mid-update). Inside the gateway, so a refused update keeps the previous
                        // rollback point.
                        try { await CaptureSnapshotAsync(container); }
                        catch (Exception cex)
                        {
                            // R1-36: with automatic rollback on, the snapshot is the way back the policy promises; an
                            // update whose snapshot failed is not run. Without a usable vault no snapshot can exist at
                            // all: the update goes ahead without a way back and says so (R1-31).
                            if (policy.AutoRollback && cex is not RollbackSnapshotUnavailableException)
                                throw new RollbackSnapshotRequiredException(cex);
                            _logger.LogWarning(cex, "Rollback snapshot capture failed for {Container}", container.Name);
                            noRollback = true;
                        }
                        recreateStarted = true;
                        return await _docker.RecreateContainerAsync(container.Id, sid, progress);
                    }, ct);
                if (outcome.Status is MutationStatus.Denied or MutationStatus.PendingApproval
                    && _automation.LostAuthority(actor, "container.update") is { } lost)
                {
                    // The owner lost their authority (PC-D6): suspend AutoUpdate until an admin takes it over.
                    var result = await _owners.SuspendAsync(IAutomationOwnerStore.AutoUpdate, updateOwner, lost, ct);
                    if (result == SuspendResult.Superseded)
                        throw new InvalidOperationException(
                            $"Refused: {lost}. AutoUpdate has a new owner since this run started; the next run uses it.");
                    await NotifySuspendedAsync(lost, result == SuspendResult.Suspended);
                    throw new InvalidOperationException(
                        $"AutoUpdate was suspended: {lost}. Take it over again in Settings → Automation owners.");
                }
                if (outcome.Status != MutationStatus.Executed)
                    throw outcome.Error ?? new InvalidOperationException(outcome.Reason ?? "The update was refused.");
                var newId = outcome.Value!;

                // Wait for health check (if container has one)
                await Task.Delay(TimeSpan.FromSeconds(15), ct);

                // Check health
                var (state, _, oomKilled) = await _docker.InspectContainerStateAsync(newId, container.ServerId);

                if (state != "running" || oomKilled)
                {
                    throw new Exception($"Container unhealthy after update: state={state}, oom={oomKilled}");
                }

                history.Success = true;
                history.CompletedAt = DateTime.UtcNow;
                policy.LastUpdated = DateTime.UtcNow;
                // R1-31: an update without a way back says so where its result is read.
                policy.LastUpdateResult = noRollback ? "OK (no rollback snapshot)" : "OK";
                if (noRollback) history.Error = "No rollback snapshot was kept for this update; it cannot be rolled back from Whiskers.";

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

                // R1-36: an update that started and failed (the recreate threw, its effect could not be verified, or
                // the container is not healthy afterwards) is rolled back when the policy says so — through the
                // gateway on the same owner's authority, and verified like any rollback.
                if (recreateStarted && policy.AutoRollback && !noRollback && updateActor is { } rollbackActor)
                {
                    var problem = await AutoRollbackAsync(rollbackActor, container, ct);
                    history.RolledBack = problem is null;
                    history.Error = problem is null
                        ? $"{ex.Message} — rolled back to the previous image."
                        : $"{ex.Message} — the automatic rollback failed: {problem}";
                    policy.LastUpdateResult = problem is null
                        ? $"FEHLER (zurückgerollt): {ex.Message}"
                        : $"FEHLER (Rollback fehlgeschlagen): {ex.Message}";
                }

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

    /// <summary>Tells the admins that AutoUpdate was suspended and how to take it over (R1-04). An administrative
    /// event: no container mute or update preference hides it.</summary>
    private async Task NotifySuspendedAsync(string lost, bool saved)
    {
        try
        {
            await _notifications.SendAsync(new NotificationEvent
            {
                EventType = "automation_suspended",
                ContainerName = "AutoUpdate",
                ImageInfo = $"AutoUpdate was suspended: {lost}. It stays off until an admin takes it over in Settings → Automation owners."
                            + (saved ? "" : Whiskers.Services.AutoPatch.AutoPatchService.SuspensionNotSaved),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification for the suspended AutoUpdate failed");
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
        var purpose = Whiskers.Services.Vault.ProtectedFields.RollbackSnapshot(container.ServerId, container.Name);
        if (_vault?.IsEnabled != true)
        {
            // No plaintext snapshot, and no older one offered as the way back from this update either — removed so that
            // an older plaintext one does not linger in the database file.
            var stale = db.UpdateRollbacks.Where(r => r.ContainerName == container.Name && r.ServerId == container.ServerId);
            if (await stale.AnyAsync())
                await Whiskers.Services.Vault.ProtectedFieldMigration.WithoutRemnantsAsync(db, () => stale.ExecuteDeleteAsync(), _logger);
            throw new RollbackSnapshotUnavailableException();
        }
        var (oldImageId, cfg) = await _docker.CaptureRollbackSnapshotAsync(
            container.Id, container.ServerId);
        var stored = _vault.ProtectField(purpose, cfg) ?? throw new RollbackSnapshotUnavailableException();
        await UpsertRollbackAsync(db, container, oldImageId, stored, CancellationToken.None);
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
        await _docker.RollbackContainerAsync(snap.ContainerName, snap.OldImageRef, SnapshotConfig(snap), snap.ServerId,
            RollbackProgress(snap));
        await ConsumeSnapshotAsync(db, snap, markHistory: true);
        _logger.LogInformation("Manual rollback of {Container} to the previous image completed", snap.ContainerName);
        return $"{snap.ContainerName} was rolled back to the previous image.";
    }

    /// <summary>R1-36: rolls a failed automatic update back through the gateway (<c>container.rollback</c>, verified:
    /// the container runs again). Null when it was rolled back; otherwise why not.</summary>
    private async Task<string?> AutoRollbackAsync(AutomationActor actor, ContainerInfo container, CancellationToken ct)
    {
        try
        {
            var outcome = await _automation.RunAsync(actor, "container.rollback",
                new { containerName = container.Name, serverId = container.ServerId },
                async _ =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                    var snap = await db.UpdateRollbacks.FirstOrDefaultAsync(
                                   r => r.ContainerName == container.Name && r.ServerId == container.ServerId)
                               ?? throw new InvalidOperationException("No rollback snapshot found.");
                    var newId = await _docker.RollbackContainerAsync(snap.ContainerName, snap.OldImageRef,
                        SnapshotConfig(snap), snap.ServerId, RollbackProgress(snap));
                    // The run's own history row is written by the caller (not saved yet): not marked here.
                    await ConsumeSnapshotAsync(db, snap, markHistory: false);
                    return newId;
                }, ct);
            if (outcome.Status == MutationStatus.Executed)
            {
                _logger.LogWarning("Auto-update of {Container} failed and was rolled back to the previous image", container.Name);
                return null;
            }
            return outcome.Error?.Message ?? outcome.Reason ?? $"the rollback was {outcome.Status}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Automatic rollback of {Container} failed", container.Name);
            return ex.Message;
        }
    }

    /// <summary>The snapshot's container configuration. Encrypted since R1-31; a snapshot from before is read as it
    /// is until the start-up migration encrypted it.</summary>
    private string SnapshotConfig(UpdateRollbackEntity snap)
        => Whiskers.Services.Vault.ProtectedFields.Read(_vault,
               Whiskers.Services.Vault.ProtectedFields.RollbackSnapshot(snap.ServerId, snap.ContainerName), snap.ConfigJson)
           ?? throw new InvalidOperationException(_vault?.IsEnabled == true
               ? "The rollback snapshot cannot be read: it does not belong to this container and server, or it was changed."
               : "The rollback snapshot cannot be read: the vault is not usable (VAULT_KEY missing or wrong).");

    private Progress<string> RollbackProgress(UpdateRollbackEntity snap)
        => new(msg => _logger.LogDebug("Rollback {Container}: {Msg}", snap.ContainerName, msg));

    /// <summary>Consumes the snapshot after the container was recreated from it.</summary>
    private static async Task ConsumeSnapshotAsync(MetricsDbContext db, UpdateRollbackEntity snap, bool markHistory)
    {
        // Mark the latest update-history row for this container as rolled back.
        if (markHistory)
        {
            var hist = await db.UpdateHistory
                .Where(h => h.ContainerName == snap.ContainerName && h.ServerId == snap.ServerId)
                .OrderByDescending(h => h.StartedAt).FirstOrDefaultAsync();
            if (hist != null) hist.RolledBack = true;
        }

        // The snapshot is consumed — the container now runs the OLD image again, so there is nothing newer to
        // roll back to until the next update captures a fresh snapshot.
        db.UpdateRollbacks.Remove(snap);
        await db.SaveChangesAsync();
    }
}

/// <summary>R1-36: the policy rolls back failed updates automatically, but no snapshot could be kept (no usable
/// vault, or the capture failed): the update is not run, since its promised way back does not exist.</summary>
public sealed class RollbackSnapshotRequiredException(Exception inner)
    : InvalidOperationException("Automatic rollback is on for this container, but no rollback snapshot could be kept ("
        + inner.Message + "). The update was not run; turn automatic rollback off in its update policy to update "
        + "without a way back.", inner);

/// <summary>R1-31: no rollback snapshot was kept for an update — it holds the container's environment, and without a
/// usable vault it is not stored at all. The update itself goes ahead; it just cannot be rolled back from Whiskers.</summary>
public sealed class RollbackSnapshotUnavailableException()
    : InvalidOperationException("No rollback snapshot was kept: the vault is not usable (VAULT_KEY missing or wrong), "
        + "and a snapshot holds the container's environment. The update proceeds, but it cannot be rolled back from Whiskers.");
