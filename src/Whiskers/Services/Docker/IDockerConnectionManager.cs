using Docker.DotNet;

namespace Whiskers.Services.Docker;

/// <summary>Provides and caches DockerClient instances per configured server, with self-healing
/// reconnect for dead SSH tunnels.</summary>
public interface IDockerConnectionManager : IDisposable
{
    Task<DockerClient> GetClientAsync(string? serverId = null);
    /// <summary>Runs the operation under the load budget and circuit breaker, and retries ONCE against a
    /// rebuilt tunnel on a transport failure.
    ///
    /// <para><paramref name="singleFlightKey"/> lets a BACKGROUND caller say "if an identical request is
    /// already running, drop mine" (Plan-0001 WP3.2). Interactive callers are unaffected.</para>
    ///
    /// <para><paramref name="callerToken"/> (R1-41): the caller's own cancellation — it ends the wait for the client and
    /// for a budget slot, and a call it cancelled is not held against the server (circuit breaker) and is not reported
    /// as a saturated budget. A deadline about the server belongs in the operation's own token, not here.</para></summary>
    Task<T> ExecuteAsync<T>(string? serverId, Func<DockerClient, Task<T>> operation, string? singleFlightKey = null,
        CancellationToken callerToken = default);

    /// <summary>Budget and circuit breaker, but <b>no retry</b> — for operations that must not run twice, and
    /// for streaming reads where a retry would restart the stream rather than resume it.
    ///
    /// <para>It exists because the alternative was worse: routing every direct <c>GetClient</c> caller through
    /// <see cref="ExecuteAsync{T}"/> would silently give mutating operations (create, start, remove) an
    /// automatic retry they never had. Doubling a container start to gain a load cap is a bad trade.</para></summary>
    Task<T> ExecuteGuardedAsync<T>(string? serverId, Func<DockerClient, Task<T>> operation, string? singleFlightKey = null,
        CancellationToken callerToken = default);
    void InvalidateClient(string serverId, DockerClient? ifCurrent = null);
}
