using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Hubs;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Notifications;

namespace Whiskers.Services.ImageUpdate;

public class ImageUpdateChecker : Whiskers.Services.FleetBackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IImageUpdateStore _store;
    private readonly IRegistryClient _registry;
    private readonly ILogger<ImageUpdateChecker> _logger;
    private readonly Whiskers.Services.Observability.SelfMetrics.ISelfMetrics _selfMetrics;
    private readonly ImageUpdateSettings _settings;

    public ImageUpdateChecker(
        IServiceProvider services,
        IImageUpdateStore store,
        IRegistryClient registry,
        IOptions<ImageUpdateSettings> settings,
        ILogger<ImageUpdateChecker> logger,
        Whiskers.Services.Observability.SelfMetrics.ISelfMetrics selfMetrics)
    {
        _services = services;
        _store = store;
        _registry = registry;
        _settings = settings.Value;
        _logger = logger;
        _selfMetrics = selfMetrics;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Image update checker is disabled");
            return;
        }

        _logger.LogInformation("Image update checker started (interval: {Hours}h)", _settings.CheckIntervalHours);

        // Initial delay: wait 30s after startup, then check
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            // WaitAsync bounds the cycle to shutdown: registry/Docker digest calls carry no token, so
            // abandon an in-flight check on stop instead of blocking the host's shutdown window.
            await CheckAllImagesAsync(ct).WaitAsync(ct);
            await Task.Delay(TimeSpan.FromHours(_settings.CheckIntervalHours), ct);
        }
    }

    public async Task CheckAllImagesAsync(CancellationToken ct = default)
    {
        _store.IsChecking = true;
        _logger.LogInformation("Starting image update check...");

        try
        {
            var cycleStartedAt = DateTime.UtcNow;
            using var scope = _services.CreateScope();
            var docker = scope.ServiceProvider.GetRequiredService<IDockerService>();
            var notification = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ContainerHub>>();

            // The Docker container listing excludes Kubernetes servers, so this loop never looks at them.
            // Recording that is the difference between "explicitly not covered" and invisible (Plan-0003 WP2).
            var allServers = scope.ServiceProvider
                .GetRequiredService<Whiskers.Services.ServerConfig.IServerConfigService>().GetEnabledServers();
            Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.RecordKubernetesSkips(
                _selfMetrics,
                Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.Loops.ImageUpdate,
                allServers);

            var containers = await docker.ListAllContainersAsync(all: false);

            foreach (var server in allServers.Where(s => s.ConnectionType != Whiskers.Models.ConnectionType.Kubernetes))
                _selfMetrics.RecordCycle(
                    Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.Loops.ImageUpdate,
                    server.Id, DateTime.UtcNow - cycleStartedAt, success: true);
            // ConcurrentBag: up to 5 parallel checks call .Add — a List<T> would lose entries or crash on growth.
            var newUpdates = new System.Collections.Concurrent.ConcurrentBag<ImageUpdateInfo>();

            // Check each container in parallel (max 5 concurrent)
            var semaphore = new SemaphoreSlim(5);
            var tasks = containers.Select(async container =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var info = await CheckSingleImageAsync(docker, container, ct);
                    if (info != null)
                    {
                        // Read the prior state BEFORE overwriting, so we only notify when an update FIRST
                        // appears — not on every cycle while it stays available.
                        var prev = _store.Get(container.Id, container.ServerId);
                        _store.Set(info);
                        if (info.UpdateAvailable && prev?.UpdateAvailable != true)
                            newUpdates.Add(info);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            _store.LastCheckAt = DateTime.UtcNow;

            // Send notifications for new updates
            if (_settings.NotifyOnUpdate && newUpdates.Count > 0)
            {
                foreach (var update in newUpdates)
                {
                    try
                    {
                        await notification.SendAsync(new NotificationEvent
                        {
                            ContainerId = update.ContainerId,
                            ContainerName = update.ContainerName,
                            EventType = "image_update",
                            ImageName = update.Image,
                            ImageInfo = $"Local: {Truncate(update.LocalDigest)} → Remote: {Truncate(update.RemoteDigest)}"
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send update notification for {Container}", update.ContainerName);
                    }
                }
            }

            // Broadcast to UI
            await hubContext.Clients.All.SendAsync("ImageUpdatesChanged", ct);

            await _store.SaveAsync();

            // Coverage, not just a count. "21 containers" used to be printed whether the fleet had 21 or 46,
            // and half of them being unreachable at that moment looked exactly like a complete check. And the
            // second number said "updates available" while counting only the ones that appeared THIS cycle —
            // so a fleet with twenty long-known updates still reported "0 updates available".
            var expected = allServers
                .Where(s => s.ConnectionType != Whiskers.Models.ConnectionType.Kubernetes)
                .Select(s => s.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seen = containers.Select(c => c.ServerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = expected.Except(seen, StringComparer.OrdinalIgnoreCase).ToList();
            var pending = _store.GetAllPendingUpdates().Count;

            if (missing.Count > 0)
                _logger.LogWarning(
                    "Image update check covered {Seen} of {Total} servers — no containers came back from " +
                    "{Missing}. {Pending} update(s) known ({New} new this cycle), but this answer is " +
                    "INCOMPLETE: an unchecked server looks exactly like one with nothing to update.",
                    seen.Count, expected.Count, string.Join(", ", missing), pending, newUpdates.Count);
            else
                _logger.LogInformation(
                    "Image update check complete: {Containers} containers across all {Servers} servers, " +
                    "{Pending} update(s) known ({New} new this cycle)",
                    containers.Count, expected.Count, pending, newUpdates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image update check failed");
        }
        finally
        {
            _store.IsChecking = false;
        }
    }

    /// <summary>True for a digest-pinned image (<c>repo@sha256:…</c>) — a pin has no "newer" version.
    /// The old guard (<c>&amp;&amp; !image.Contains(':')</c>) could never fire because <c>@sha256:</c> itself
    /// contains a colon.</summary>
    public static bool IsDigestPinned(string image)
        => image.Contains("@sha256:", StringComparison.Ordinal);

    /// <summary>True when a listed "image" is really a bare image ID (<c>e9e1a1e18e8e</c>,
    /// <c>sha256:…</c>). Docker lists a container that way once its tag has moved on to a newer image.</summary>
    public static bool LooksLikeImageId(string image)
        => System.Text.RegularExpressions.Regex.IsMatch(image, "^(sha256:)?[0-9a-f]{12,64}$");

    /// <summary>
    /// Which image name to ask the registry about, and which local image stands for "what runs now".
    ///
    /// <para>Reading the local digest through the TAG (the old behaviour) compares the registry with whatever
    /// was pulled last — not with what the container runs. Any pull without a recreate (a manual one, the
    /// candidate pull of <c>assess_update_risk</c>) therefore made a stale container look current. Worse, the
    /// listing then shows the bare old image ID instead of the name, so the registry was asked about
    /// "e9e1a1e18e8e" and the check ended as "could not reach registry". Found 2026-10-25 with 305 fixable
    /// CVEs on containers whose newer image had been on disk for weeks.</para>
    /// </summary>
    /// <param name="listedImage">The image as the container listing shows it.</param>
    /// <param name="configuredImage">The container's <c>Config.Image</c>, when it had to be looked up.</param>
    /// <param name="runningImageId">The ID of the image the container runs, when known.</param>
    public static (string Image, string LocalRef) ResolveCheckRefs(
        string listedImage, string? configuredImage, string? runningImageId)
    {
        var image = LooksLikeImageId(listedImage) && !string.IsNullOrWhiteSpace(configuredImage)
            ? configuredImage
            : listedImage;
        var localRef = string.IsNullOrWhiteSpace(runningImageId) ? image : runningImageId;
        return (image, localRef);
    }

    private async Task<ImageUpdateInfo?> CheckSingleImageAsync(
        IDockerService docker, ContainerInfo container, CancellationToken ct)
    {
        try
        {
            var configured = LooksLikeImageId(container.Image)
                ? await docker.GetConfiguredImageAsync(container.Id, container.ServerId)
                : null;
            var (image, localRef) = ResolveCheckRefs(container.Image, configured, container.ImageId);

            // Skip digest-pinned images — a pin is a pin, there's no newer version to check. A bare ID that
            // could not be resolved to a name has nothing to ask a registry about either.
            if (IsDigestPinned(image) || LooksLikeImageId(image))
                return null;

            // Local digest of the image the container RUNS, not of whatever the tag points at now.
            var localDigest = await docker.GetImageDigestAsync(localRef, container.ServerId);

            // Get remote digest
            var remoteDigest = await _registry.GetRemoteDigestAsync(image);

            if (localDigest == null || remoteDigest == null)
            {
                return new ImageUpdateInfo
                {
                    ContainerId = container.Id,
                    ContainerName = container.Name,
                    Image = image,
                    LocalDigest = localDigest ?? "unknown",
                    RemoteDigest = remoteDigest ?? "unknown",
                    UpdateAvailable = false,
                    CheckedAt = DateTime.UtcNow,
                    ServerId = container.ServerId,
                    Error = localDigest == null ? "Could not get local digest" : "Could not reach registry"
                };
            }

            var updateAvailable = !string.Equals(localDigest, remoteDigest, StringComparison.OrdinalIgnoreCase);

            return new ImageUpdateInfo
            {
                ContainerId = container.Id,
                ContainerName = container.Name,
                Image = image,
                LocalDigest = localDigest,
                RemoteDigest = remoteDigest,
                UpdateAvailable = updateAvailable,
                CheckedAt = DateTime.UtcNow,
                ServerId = container.ServerId
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check image for container {Name}", container.Name);
            return null;
        }
    }

    private static string Truncate(string digest)
        => digest.Length > 19 ? digest[..19] + "…" : digest;
}
