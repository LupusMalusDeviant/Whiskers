using Docker.DotNet;
using Docker.DotNet.Models;

namespace Whiskers.Services.Docker.Operations;

/// <summary>
/// Image pull and digest-inspect operations for the <see cref="DockerService"/> facade.
/// </summary>
internal sealed class ImageOperations
{
    private readonly IDockerConnectionManager _connectionManager;
    private readonly ILogger<DockerService> _logger;
    private readonly Whiskers.Services.Registries.IRegistryConfigService? _registries;

    public ImageOperations(
        IDockerConnectionManager connectionManager,
        ILogger<DockerService> logger,
        Whiskers.Services.Registries.IRegistryConfigService? registries = null)
    {
        _connectionManager = connectionManager;
        _logger = logger;
        _registries = registries;
    }

    private async Task<DockerClient> GetClient(string? serverId)
        => await _connectionManager.GetClientAsync(serverId);

    /// <summary>Pulls an image. Deliberately NOT under the load budget (Plan-0001 WP6.3).
    ///
    /// <para>A pull can run for minutes. The budget's slots are sized for short calls, so holding one for the
    /// duration of a pull would starve the health checks and the log scan of the same server — trading a
    /// bounded amount of load for an unbounded amount of blindness. The cap is there to stop many small calls
    /// piling up, which is not what this is.</para></summary>
    public async Task PullImageAsync(string imageName, IProgress<string>? progress = null, string? serverId = null)
    {
        var client = await GetClient(serverId);
        var (repo, tag) = ParseImageReference(imageName);

        // F8: authenticated pulls for UI-managed private registries — the image's registry host is
        // matched against the configured registries; no match = anonymous pull (unchanged behavior).
        AuthConfig? auth = null;
        if (_registries?.GetCredentialForImage(imageName) is { } cred)
        {
            auth = new AuthConfig { Username = cred.Username, Password = cred.Password, ServerAddress = cred.ServerAddress };
            _logger.LogDebug("Pulling {Image} with credentials for {Registry}", imageName, cred.ServerAddress);
        }

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = repo, Tag = tag },
            auth,
            new Progress<JSONMessage>(msg =>
            {
                progress?.Report(msg.Status ?? "");
            }));
    }

    /// <summary>Splits a Docker image reference into (repo, tag) for the Images.CreateImage call. A naive
    /// Split(':') breaks references with a registry port (host:5000/app) or a digest (repo@sha256:...),
    /// so only treat a ':' that comes after the last '/' as the tag separator, and pass a digest through
    /// as the tag (the Docker API accepts a digest in the Tag field).</summary>
    internal static (string Repo, string Tag) ParseImageReference(string imageName)
    {
        var atIdx = imageName.IndexOf('@');
        if (atIdx >= 0)
            return (imageName[..atIdx], imageName[(atIdx + 1)..]);   // repo + "sha256:..."

        var slashIdx = imageName.LastIndexOf('/');
        var colonIdx = imageName.LastIndexOf(':');
        if (colonIdx > slashIdx)   // colon belongs to the tag, not a registry port
            return (imageName[..colonIdx], imageName[(colonIdx + 1)..]);

        return (imageName, "latest");
    }

    /// <summary>
    /// The part of an image a running container depends on — entrypoint, user, ports, volumes, healthcheck,
    /// OS. Read before an update so the risk can be measured instead of guessed (GAP-6).
    /// </summary>
    public async Task<ImageUpdate.ImageContract?> GetImageContractAsync(string imageRef, string? serverId = null)
    {
        try
        {
            var inspect = await _connectionManager.ExecuteGuardedAsync(
                serverId, c => c.Images.InspectImageAsync(imageRef),
                singleFlightKey: $"imagecontract:{imageRef}");

            var cfg = inspect.Config;
            return new ImageUpdate.ImageContract(
                cfg?.Entrypoint?.ToList() ?? [],
                cfg?.Cmd?.ToList() ?? [],
                string.IsNullOrWhiteSpace(cfg?.User) ? null : cfg.User,
                new HashSet<string>(cfg?.ExposedPorts?.Keys ?? []),
                new HashSet<string>(cfg?.Volumes?.Keys ?? []),
                string.IsNullOrWhiteSpace(cfg?.WorkingDir) ? null : cfg.WorkingDir,
                cfg?.Healthcheck?.Test is { Count: > 0 },
                inspect.Os,
                cfg?.Labels is { } labels ? new Dictionary<string, string>(labels) : null);
        }
        catch (Exception ex)
        {
            // Null, not an empty contract: an empty one would compare as "everything was removed" and turn a
            // missing image into a fleet of high-risk findings.
            _logger.LogWarning(ex, "Could not inspect image {Image} on {Server}", imageRef, serverId ?? "default");
            return null;
        }
    }

    public async Task<string?> GetImageDigestAsync(string imageRef, string? serverId = null)
    {
        try
        {
            // Plan-0001 WP6.3: under the budget. The image-update checker calls this once per image on every
            // pass, so it is steady background traffic — and it was taking a bare client, invisible to both
            // the cap and the circuit breaker.
            var inspect = await _connectionManager.ExecuteGuardedAsync(
                serverId, c => c.Images.InspectImageAsync(imageRef),
                singleFlightKey: $"imagedigest:{imageRef}");

            // RepoDigests contains the pull-able digest, e.g. "nginx@sha256:abc..."
            if (inspect.RepoDigests?.Count > 0)
            {
                var digest = inspect.RepoDigests[0];
                var atIndex = digest.IndexOf('@');
                return atIndex >= 0 ? digest[(atIndex + 1)..] : digest;
            }

            // Fallback to image ID
            return inspect.ID;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get image digest for {Image}", imageRef);
            return null;
        }
    }
}
