using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Hubs;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Notifications;

namespace Whiskers.Services.ImageUpdate;

public class ImageUpdateChecker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IImageUpdateStore _store;
    private readonly IRegistryClient _registry;
    private readonly ILogger<ImageUpdateChecker> _logger;
    private readonly ImageUpdateSettings _settings;

    public ImageUpdateChecker(
        IServiceProvider services,
        IImageUpdateStore store,
        IRegistryClient registry,
        IOptions<ImageUpdateSettings> settings,
        ILogger<ImageUpdateChecker> logger)
    {
        _services = services;
        _store = store;
        _registry = registry;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
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
            await CheckAllImagesAsync(ct);
            await Task.Delay(TimeSpan.FromHours(_settings.CheckIntervalHours), ct);
        }
    }

    public async Task CheckAllImagesAsync(CancellationToken ct = default)
    {
        _store.IsChecking = true;
        _logger.LogInformation("Starting image update check...");

        try
        {
            using var scope = _services.CreateScope();
            var docker = scope.ServiceProvider.GetRequiredService<IDockerService>();
            var notification = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ContainerHub>>();

            var containers = await docker.ListAllContainersAsync(all: false);
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

            _logger.LogInformation("Image update check complete: {Total} containers, {Updates} updates available",
                containers.Count, newUpdates.Count);
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

    private async Task<ImageUpdateInfo?> CheckSingleImageAsync(
        IDockerService docker, ContainerInfo container, CancellationToken ct)
    {
        try
        {
            var image = container.Image;

            // Skip digest-pinned images — a pin is a pin, there's no newer version to check.
            if (IsDigestPinned(image))
                return null;

            // Get local digest
            var localDigest = await docker.GetImageDigestAsync(image, container.ServerId);

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
