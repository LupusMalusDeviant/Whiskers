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

    public ImageOperations(
        IDockerConnectionManager connectionManager,
        ILogger<DockerService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    private async Task<DockerClient> GetClient(string? serverId)
        => await _connectionManager.GetClientAsync(serverId);

    public async Task PullImageAsync(string imageName, IProgress<string>? progress = null, string? serverId = null)
    {
        var client = await GetClient(serverId);
        var (repo, tag) = ParseImageReference(imageName);

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = repo, Tag = tag },
            null,
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

    public async Task<string?> GetImageDigestAsync(string imageRef, string? serverId = null)
    {
        try
        {
            var client = await GetClient(serverId);
            var inspect = await client.Images.InspectImageAsync(imageRef);

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
