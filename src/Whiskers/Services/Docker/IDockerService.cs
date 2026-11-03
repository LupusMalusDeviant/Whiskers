using Whiskers.Services.Mutations;
using Whiskers.Models;

namespace Whiskers.Services.Docker;

public interface IDockerService
{
    Task<IList<ContainerInfo>> ListContainersAsync(bool all = true, string? serverId = null, CancellationToken ct = default);
    Task<IList<ContainerInfo>> ListAllContainersAsync(bool all = true);

    /// <summary>R1-41: the fleet-wide list for a caller with a deadline or a stop of its own (the AutoPatch planning, the
    /// advisor chat): its cancellation reaches the per-server requests. The default only stops waiting.</summary>
    Task<IList<ContainerInfo>> ListAllContainersAsync(bool all, CancellationToken ct) => ListAllContainersAsync(all).WaitAsync(ct);

    /// <summary>Fleet-wide list PLUS which servers answered. Callers that keep per-server state across
    /// cycles need that distinction — an unreachable host and an empty host look identical in the plain
    /// list. See <see cref="FleetContainerListing"/>.</summary>
    Task<FleetContainerListing> ListAllContainersDetailedAsync(bool all = true);

    /// <summary>R1-41: <see cref="ListAllContainersDetailedAsync(bool)"/> for a loop that is stopped by the host: the
    /// cancellation reaches every per-server request and the waits for a client and a budget slot, a cancelled caller gets
    /// an <see cref="OperationCanceledException"/> (never a listing in which the stop looks like silent servers), and its
    /// cancellation is not held against a server. The default implementation only stops waiting; <c>DockerService</c>
    /// cancels the work itself.</summary>
    Task<FleetContainerListing> ListAllContainersDetailedAsync(bool all, CancellationToken ct)
        => ListAllContainersDetailedAsync(all).WaitAsync(ct);
    Task<ContainerInfo?> GetContainerAsync(string id, string? serverId = null);
    Task<ContainerStats?> GetContainerStatsAsync(string containerId, string? serverId = null);
    [MutatingOperation("container.start")]
    Task StartContainerAsync(string containerId, string? serverId = null);
    [MutatingOperation("container.stop")]
    Task StopContainerAsync(string containerId, string? serverId = null);
    [MutatingOperation("container.restart")]
    Task RestartContainerAsync(string containerId, string? serverId = null);
    [MutatingOperation("container.remove")]
    Task RemoveContainerAsync(string containerId, bool force = false, string? serverId = null);
    Task<string> GetContainerLogsAsync(string containerId, int tailLines = 100, string? serverId = null, DateTime? since = null, CancellationToken ct = default);

    /// <summary>R1-41: the log read with a server <paramref name="deadline"/> (bounds the request and the waits for a client
    /// and a budget slot; an expiry at the host counts against the server) and the caller's own
    /// <paramref name="callerToken"/> (a loop's stop: ends the same waits, never held against the server). The default
    /// links both into the plain read's token; <c>DockerService</c> keeps them apart.</summary>
    async Task<string> GetContainerLogsAsync(string containerId, int tailLines, string? serverId, DateTime? since,
        CancellationToken deadline, CancellationToken callerToken)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(deadline, callerToken);
        return await GetContainerLogsAsync(containerId, tailLines, serverId, since, either.Token);
    }
    [MutatingOperation("container.create")]
    Task<string> CreateContainerAsync(DeploymentRequest request, string? serverId = null);
    [MutatingOperation("image.pull")]
    Task PullImageAsync(string imageName, IProgress<string>? progress = null, string? serverId = null);
    Task<(string State, int ExitCode, bool OomKilled)> InspectContainerStateAsync(string containerId, string? serverId = null);

    /// <summary>R1-41: <see cref="InspectContainerStateAsync(string, string?)"/> whose cancellation reaches the inspect
    /// and the wait for a budget slot and is not held against the server (the default only stops waiting).</summary>
    Task<(string State, int ExitCode, bool OomKilled)> InspectContainerStateAsync(string containerId, string? serverId, CancellationToken ct)
        => InspectContainerStateAsync(containerId, serverId).WaitAsync(ct);
    Task<ServerSystemInfo> GetServerSystemInfoAsync(string? serverId = null, CancellationToken ct = default);
    Task<Dictionary<string, ServerSystemInfo>> GetAllServerSystemInfoAsync();
    Task<string?> GetImageDigestAsync(string imageRef, string? serverId = null);

    /// <summary>R1-41: <see cref="GetImageDigestAsync(string, string?)"/> whose cancellation reaches the inspect and the
    /// wait for a budget slot; a cancelled caller gets the cancellation, not a null that reads as "no digest" (the default
    /// only stops waiting).</summary>
    Task<string?> GetImageDigestAsync(string imageRef, string? serverId, CancellationToken ct)
        => GetImageDigestAsync(imageRef, serverId).WaitAsync(ct);

    /// <summary>The image reference a container was created from (its <c>Config.Image</c>), or null when it
    /// cannot be inspected. Needed because a listing shows a bare image ID once the tag has moved on.</summary>
    Task<string?> GetConfiguredImageAsync(string containerId, string? serverId = null);

    /// <summary>R1-41: <see cref="GetConfiguredImageAsync(string, string?)"/> whose cancellation reaches the inspect; a
    /// cancelled caller gets the cancellation, not a null (the default only stops waiting).</summary>
    Task<string?> GetConfiguredImageAsync(string containerId, string? serverId, CancellationToken ct)
        => GetConfiguredImageAsync(containerId, serverId).WaitAsync(ct);

    /// <summary>What an image declares about how it runs — for measuring update risk before recreating
    /// anything (GAP-6). Null when the image cannot be inspected.</summary>
    Task<ImageUpdate.ImageContract?> GetImageContractAsync(string imageRef, string? serverId = null);

    /// <summary>R1-41: <see cref="GetImageContractAsync(string, string?)"/> for a caller that can be cancelled: the
    /// cancellation reaches the inspect and the wait for a budget slot, and is not held against the server (the default
    /// implementation only stops waiting). The inspect is not shared: a background caller whose identical inspect is already running gets <c>null</c> (the budget's
    /// single-flight drops the duplicate), an interactive caller runs its own.</summary>
    Task<ImageUpdate.ImageContract?> GetImageContractAsync(string imageRef, string? serverId, CancellationToken ct)
        => GetImageContractAsync(imageRef, serverId).WaitAsync(ct);
    [MutatingOperation("container.update")]
    Task<string> RecreateContainerAsync(string containerId, string? serverId = null, IProgress<string>? progress = null);
    /// <summary>The container's log-driver configuration and log file path, or null when Docker reports
    /// none. Used by the log-hygiene inventory (Plan-0007 WP3).</summary>
    Task<ContainerLogConfiguration?> GetLogConfigurationAsync(string containerId, string? serverId = null);

    Task<List<KeyValuePair<string, string>>> GetContainerEnvAsync(string containerId, string? serverId = null);

    // C12 update-rollback: capture the pre-update snapshot (old image ID + full container config as JSON) so a
    // failed update can be undone; RollbackContainerAsync recreates the container from that snapshot's old image.
    Task<(string ImageId, string ConfigJson)> CaptureRollbackSnapshotAsync(string containerId, string? serverId = null);
    [MutatingOperation("container.rollback")]
    Task<string> RollbackContainerAsync(string containerName, string imageId, string configJson, string? serverId = null, IProgress<string>? progress = null);

    // Networks
    Task<IList<NetworkInfo>> ListNetworksAsync(string? serverId = null);
    [MutatingOperation("network.create")]
    Task<string> CreateNetworkAsync(string name, string driver = "bridge", string? serverId = null);
    [MutatingOperation("network.remove")]
    Task RemoveNetworkAsync(string networkId, string? serverId = null);
    [MutatingOperation("network.connect")]
    Task ConnectContainerToNetworkAsync(string networkId, string containerId, string? serverId = null);
    [MutatingOperation("network.disconnect")]
    Task DisconnectContainerFromNetworkAsync(string networkId, string containerId, string? serverId = null);

    // Run a shell command on the HOST via a short-lived privileged nsenter container over the Docker
    // API. This is the SSH-free shell plane for TCP+mTLS servers — same effect as nsenter -t 1 locally,
    // but driven through the mTLS Docker connection instead of SSH.
    Task<(string Output, string Error, int ExitCode)> RunHostShellAsync(string command, string? serverId = null, TimeSpan? timeout = null,
        CancellationToken ct = default);
}
