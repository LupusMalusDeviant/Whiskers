using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Docker.Operations;

/// <summary>
/// Container read/inspect/lifecycle-command operations (list, stats, logs, start/stop/restart/remove)
/// for the <see cref="DockerService"/> facade.
/// </summary>
internal sealed class ContainerOperations
{
    private readonly IDockerConnectionManager _connectionManager;
    private readonly IServerConfigService _serverConfigService;
    private readonly ILogger<DockerService> _logger;
    private readonly MemoryCache _statsCache;
    private readonly Whiskers.Services.Observability.SelfMetrics.ISelfMetrics _selfMetrics;
    private static readonly TimeSpan StatsCacheDuration = TimeSpan.FromSeconds(3);
    // Reused across every stats deserialize instead of allocating a fresh options object per call.
    private static readonly System.Text.Json.JsonSerializerOptions StatsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
    };

    public ContainerOperations(
        IDockerConnectionManager connectionManager,
        IServerConfigService serverConfigService,
        ILogger<DockerService> logger,
        MemoryCache statsCache,
        Whiskers.Services.Observability.SelfMetrics.ISelfMetrics selfMetrics)
    {
        _selfMetrics = selfMetrics;
        _connectionManager = connectionManager;
        _serverConfigService = serverConfigService;
        _logger = logger;
        _statsCache = statsCache;
    }

    private async Task<DockerClient> GetClient(string? serverId)
        => await _connectionManager.GetClientAsync(serverId);

    public Task<IList<ContainerInfo>> ListContainersAsync(bool all = true, string? serverId = null, CancellationToken ct = default)
        // R1-41: ct is the caller's own (a loop's stop, a request aborted): it reaches the request and the waits for the
        // client and a slot, and a call it cancelled is not held against the server.
        => ListContainersAsync(all, serverId, ct, callerToken: ct, deadline: default);

    /// <summary><paramref name="ct"/> bounds the request; <paramref name="callerToken"/> is the caller's own cancellation,
    /// which is not held against the server; <paramref name="deadline"/> is the server's deadline and also bounds the waits
    /// for the client and for a budget slot (R1-41).</summary>
    private async Task<IList<ContainerInfo>> ListContainersAsync(bool all, string? serverId, CancellationToken ct,
        CancellationToken callerToken, CancellationToken deadline)
    {
        var server = serverId != null
            ? _serverConfigService.GetServer(serverId)
            : _serverConfigService.GetDefaultServer();

        var containers = await _connectionManager.ExecuteAsync(serverId, c =>
            c.Containers.ListContainersAsync(new ContainersListParameters { All = all }, ct), callerToken: callerToken,
            deadline: deadline);

        return containers.Select(c => ToContainerInfo(c, server?.Id ?? "local", server?.Name ?? "Local")).ToList();
    }

    private ContainerInfo ToContainerInfo(ContainerListResponse c, string serverId, string serverName) => new()
    {
        Id = c.ID,
        Name = c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12],
        Image = c.Image,
        ImageId = c.ImageID ?? string.Empty,
        Status = c.Status,
        State = c.State,
        Created = c.Created,
        HealthStatus = ExtractHealthStatus(c.Status),
        Labels = c.Labels != null ? new Dictionary<string, string>(c.Labels) : new(),
        Ports = c.Ports?.Select(p => new PortMapping
        {
            IP = p.IP ?? "",
            PrivatePort = p.PrivatePort,
            PublicPort = p.PublicPort,
            Type = p.Type
        }).ToList() ?? new(),
        ServerId = serverId,
        ServerName = serverName
    };

    /// <summary>Single-container inspect (via an id filter) for the detail-page poll — avoids listing every
    /// container on the server each tick.</summary>
    public async Task<ContainerInfo?> GetContainerAsync(string id, string? serverId = null)
    {
        var server = serverId != null
            ? _serverConfigService.GetServer(serverId)
            : _serverConfigService.GetDefaultServer();

        var containers = await _connectionManager.ExecuteAsync(serverId, c =>
            c.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["id"] = new Dictionary<string, bool> { [id] = true }
                }
            }));

        var match = containers.FirstOrDefault();
        return match == null ? null : ToContainerInfo(match, server?.Id ?? "local", server?.Name ?? "Local");
    }

    public async Task<IList<ContainerInfo>> ListAllContainersAsync(bool all = true)
        => (await ListAllContainersDetailedAsync(all)).Containers.ToList();

    /// <summary>R1-41: as above, with the caller's cancellation reaching every per-server request; a cancelled caller
    /// gets an <see cref="OperationCanceledException"/>, not a listing that looks like silent servers.</summary>
    public async Task<IList<ContainerInfo>> ListAllContainersAsync(bool all, CancellationToken ct)
        => (await ListAllContainersDetailedAsync(all, ct)).Containers.ToList();

    /// <summary>One server's slice of a fleet-wide listing: its containers, or why it stayed silent.</summary>
    private sealed record ServerListing(Models.ServerConfig Server, IList<ContainerInfo> Containers, FleetServerFailure? Failure);

    public Task<FleetContainerListing> ListAllContainersDetailedAsync(bool all = true)
        => ListAllContainersDetailedAsync(all, CancellationToken.None);

    /// <summary>How long one server of a fleet listing may take, from asking for its client to its answer. Tests only
    /// shorten it.</summary>
    internal TimeSpan PerServerTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>The clock that runs <see cref="PerServerTimeout"/> down. Tests only replace it.</summary>
    internal TimeProvider PerServerClock { get; init; } = TimeProvider.System;

    /// <summary>The fleet listing. <paramref name="ct"/> is the caller's (a loop's stop, a chat closed): it ends the whole
    /// listing with an <see cref="OperationCanceledException"/> and is held against no server. Each server additionally
    /// has <see cref="PerServerTimeout"/> — for the wait for its client and for a budget slot as well as for the request —
    /// and running out of it is that server's timeout: it is reported as a failed server, not as a cancelled caller.</summary>
    public async Task<FleetContainerListing> ListAllContainersDetailedAsync(bool all, CancellationToken ct)
    {
        // Kubernetes servers are not Docker hosts — their workloads come from the workload seam
        // (Services/Workloads), not from this Docker-only aggregation.
        var servers = _serverConfigService.GetEnabledServers()
            .Where(s => s.ConnectionType != ConnectionType.Kubernetes).ToList();
        // Bound each server with a short timeout so ONE unreachable host can't blank the whole
        // dashboard: a slow/dead server is skipped after 8s (returns empty) while the reachable ones
        // render immediately. WHICH servers failed is reported alongside the list, so callers that keep
        // per-server state can tell "empty host" from "silent host". R1-41: the timeout covers the wait for the
        // client and for a budget slot too — before, a server whose slots were all taken queued without end.
        var perServerTimeout = PerServerTimeout;
        var tasks = servers.Select(async server =>
        {
            try
            {
                // Cancel the request, do not merely stop waiting for it: a Task.WhenAny race leaves the
                // call running against the daemon, and this runs for every server on every cycle. Same
                // defect as the log fetch in the 2026-08-26 incident.
                using var expiry = new CancellationTokenSource(perServerTimeout, PerServerClock);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
                try
                {
                    // The API-latency probe (Plan-0004 WP4). This call happens once per server per health
                    // cycle and is the most regular Docker request Whiskers makes, which makes it the natural
                    // place to measure how fast the daemon is answering. Timed here rather than at every call
                    // site: 20 of the 24 sites do not pass through a shared path at all, so a probe placed
                    // there would measure whatever happened to be instrumented rather than the host.
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var listing = new ServerListing(server,
                        await ListContainersAsync(all, server.Id, timeout.Token, callerToken: ct, deadline: timeout.Token), null);
                    _selfMetrics.RecordApiCall(server.Id, System.Diagnostics.Stopwatch.GetElapsedTime(started));
                    return listing;
                }
                // The deadline ran out in Whiskers' own budget queue: the server was never asked. Its containers are unknown
                // this cycle, but it must not read as an unreachable host (review round 4) — the failure is marked NotAsked
                // so the reachability tracker neither counts nor clears an outage for it. The circuit does not count it
                // either, the budget's SaturationFailures does. Only while still queued (Ran is zero): a request that was
                // sent and then cut short by the deadline did ask the server, so it falls through to the timeout below
                // and counts toward an outage like any other (review round 5).
                catch (Budget.BudgetSaturatedException ex) when (!ct.IsCancellationRequested && ex.Ran == TimeSpan.Zero)
                {
                    _logger.LogWarning("Listing containers for server {ServerName} waited {Waited:F1}s in Whiskers' own queue and was not sent — skipping (degraded view).",
                        server.Name, ex.Waited.TotalSeconds);
                    return new ServerListing(server, new List<ContainerInfo>(),
                        new FleetServerFailure(server.Id, server.Name,
                            $"Not asked: waited in Whiskers' own queue for {ex.Waited.TotalSeconds:0.0}s", NotAsked: true));
                }
                // The server's deadline ran out — in the request, or still waiting for its client. Either way it is this
                // server's timeout, never the caller's cancellation. Follow-up (2026-11-03, review R1-41 round 8 NIT 1): only
                // a failure the deadline caused reads as a timeout; another one that merely ended after it (the daemon's
                // own error) keeps its reason below.
                catch (Exception ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested && CausedByDeadline(ex))
                {
                    _logger.LogWarning("Listing containers for server {ServerName} aborted after {Timeout}s — skipping (degraded view).",
                        server.Name, perServerTimeout.TotalSeconds);
                    return new ServerListing(server, new List<ContainerInfo>(),
                        new FleetServerFailure(server.Id, server.Name, $"Timed out after {perServerTimeout.TotalSeconds:0}s"));
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Failed to list containers for server {ServerName}", server.Name);
                return new ServerListing(server, new List<ContainerInfo>(),
                    new FleetServerFailure(server.Id, server.Name, ex.Message));
            }
        });

        var results = await Task.WhenAll(tasks);
        // A caller that cancelled gets a cancellation, never a listing in which its cancellation looks like silent servers.
        ct.ThrowIfCancellationRequested();
        return new FleetContainerListing
        {
            Containers = results.SelectMany(r => r.Containers).ToList(),
            RespondedServerIds = results.Where(r => r.Failure == null)
                .Select(r => r.Server.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            FailedServers = results.Where(r => r.Failure != null).Select(r => r.Failure!).ToList()
        };
    }

    /// <summary>Whether <paramref name="ex"/> (or what it wraps) is a cancellation or a timeout — what a deadline that ran
    /// out produces, in the request as in the wait for a client or a budget slot.</summary>
    private static bool CausedByDeadline(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is OperationCanceledException or TimeoutException) return true;
        return false;
    }

    public async Task<ContainerStats?> GetContainerStatsAsync(string containerId, string? serverId = null)
    {
        var cacheKey = $"stats:{serverId ?? "local"}:{containerId}";
        if (_statsCache.TryGetValue(cacheKey, out ContainerStats? cached))
            return cached;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Plan-0001 WP6.3: under the budget. The metrics collector asks this for every container every
            // 30 seconds — across a fleet that is the single largest source of steady Docker traffic Whiskers
            // produces, and it was entirely invisible to the cap and the circuit breaker.
            //
            // Single-shot (Stream=false) returns the stats JSON as a Stream — all we need here. The
            // non-obsolete overload requires an IProgress<JSONMessage> streaming callback; keep the
            // simpler obsolete overload deliberately.
#pragma warning disable CS0618
            var response = await _connectionManager.ExecuteGuardedAsync(
                serverId,
                c => c.Containers.GetContainerStatsAsync(
                    containerId, new ContainerStatsParameters { Stream = false }, cts.Token),
                singleFlightKey: $"stats:{containerId}");
#pragma warning restore CS0618

            using var reader = new StreamReader(response);
            var json = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(json))
                return null;

            var stats = System.Text.Json.JsonSerializer.Deserialize<DockerStatsResponse>(json, StatsJsonOptions);

            if (stats == null)
                return null;

            var cpuDelta = stats.CpuStats?.CpuUsage?.TotalUsage - stats.PrecpuStats?.CpuUsage?.TotalUsage ?? 0;
            var systemDelta = stats.CpuStats?.SystemCpuUsage - stats.PrecpuStats?.SystemCpuUsage ?? 0;
            var cpuCount = stats.CpuStats?.OnlineCpus ?? 1;
            var cpuPercent = systemDelta > 0 ? (double)cpuDelta / systemDelta * cpuCount * 100.0 : 0;

            long netRx = 0, netTx = 0;
            if (stats.Networks != null)
            {
                foreach (var net in stats.Networks.Values)
                {
                    netRx += net.RxBytes;
                    netTx += net.TxBytes;
                }
            }

            long blockRead = 0, blockWrite = 0;
            if (stats.BlkioStats?.IoServiceBytesRecursive != null)
            {
                foreach (var entry in stats.BlkioStats.IoServiceBytesRecursive)
                {
                    if (string.Equals(entry.Op, "read", StringComparison.OrdinalIgnoreCase))
                        blockRead += entry.Value;
                    else if (string.Equals(entry.Op, "write", StringComparison.OrdinalIgnoreCase))
                        blockWrite += entry.Value;
                }
            }

            var result = new ContainerStats
            {
                ContainerId = containerId,
                Timestamp = DateTime.UtcNow,
                CpuPercent = Math.Round(cpuPercent, 2),
                MemoryUsageBytes = stats.MemoryStats?.Usage ?? 0,
                MemoryLimitBytes = stats.MemoryStats?.Limit ?? 0,
                NetworkRxBytes = netRx,
                NetworkTxBytes = netTx,
                BlockReadBytes = blockRead,
                BlockWriteBytes = blockWrite
            };
            _statsCache.Set(cacheKey, result, StatsCacheDuration);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get stats for container {ContainerId}", containerId);
            return null;
        }
    }

    // The four operations below (and DisableRestartAsync after them) stay outside the budget on purpose, and the reason is not the one that was
    // written here before ("must never be auto-retried" — ExecuteGuardedAsync does not retry, so that was
    // never the risk). The real reason is the circuit breaker: it refuses calls to a server it has given up
    // on, and these four are exactly what a person reaches for when a server is in trouble — which is the
    // moment the circuit is most likely open. Refusing an operator's "stop this container" because Whiskers
    // has decided the host is unwell would take the fix away at the worst possible time.
    public async Task StartContainerAsync(string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
    }

    public async Task StopContainerAsync(string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Containers.StopContainerAsync(containerId,
            new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
    }

    /// <summary>Contract 0006: <c>docker update --restart=no</c>, so a container the re-check stops stays stopped.</summary>
    public async Task DisableRestartAsync(string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Containers.UpdateContainerAsync(containerId, new ContainerUpdateParameters
        {
            RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No },
        });
    }

    public async Task RestartContainerAsync(string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Containers.RestartContainerAsync(containerId,
            new ContainerRestartParameters { WaitBeforeKillSeconds = 10 });
    }

    public async Task RemoveContainerAsync(string containerId, bool force = false, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Containers.RemoveContainerAsync(containerId,
            new ContainerRemoveParameters { Force = force });
    }

    public Task<string> GetContainerLogsAsync(string containerId, int tailLines = 100, string? serverId = null, DateTime? since = null, CancellationToken ct = default)
        => GetContainerLogsAsync(containerId, tailLines, serverId, since, ct, callerToken: default, deadline: default);

    /// <summary>R1-41: the log read with the server's <paramref name="deadline"/> (the log monitor's fetch timeout — it
    /// bounds the request and the waits for the client and a slot, and an expiry at the host counts against it) kept
    /// apart from the <paramref name="callerToken"/> (the loop's stop — it ends the same waits and is never held against
    /// the server).</summary>
    public async Task<string> GetContainerLogsAsync(string containerId, int tailLines, string? serverId, DateTime? since,
        CancellationToken deadline, CancellationToken callerToken)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(deadline, callerToken);
        return await GetContainerLogsAsync(containerId, tailLines, serverId, since, either.Token, callerToken, deadline);
    }

    private async Task<string> GetContainerLogsAsync(string containerId, int tailLines, string? serverId, DateTime? since,
        CancellationToken ct, CancellationToken callerToken, CancellationToken deadline)
    {
        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            // The tail limit applies in BOTH cases. Docker filters by `since` first and then keeps the last
            // N of those, so the monitor still sees only new lines — but a container that dumps thousands of
            // lines between two cycles can no longer pull its whole burst over a remote connection every
            // minute (Tail="all" did exactly that, once per container per cycle, fleet-wide).
            Tail = tailLines.ToString(),
            Since = since.HasValue
                ? ((DateTimeOffset)DateTime.SpecifyKind(since.Value, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString()
                : null,
            Timestamps = true
        };

        // Through the guarded path, not a bare client: this is the exact call that ran thirteen times over
        // on 2026-08-26, so it belongs under the load budget and the circuit breaker. Guarded rather than
        // ExecuteAsync because a retry would restart the log stream from the top instead of resuming it.
        // The single-flight key makes a second BACKGROUND request for the same container's logs drop rather
        // than queue; the UI keeps its own lane.
        using var muxStream = await _connectionManager.ExecuteGuardedAsync(serverId,
            c => c.Containers.GetContainerLogsAsync(containerId, false, logParams, ct),
            singleFlightKey: $"logs:{containerId}", callerToken: callerToken, deadline: deadline);

        var sb = new StringBuilder();
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();

        // Drain the multiplexed stream frame by frame until EOF instead of using CopyOutputToAsync.
        // Measured against a live daemon: for a short response (a `since` window holding a single line)
        // CopyOutputToAsync returned with ZERO bytes while the very same request over curl returned the
        // line — so a container that logs rarely was silently never matched, while a chatty one worked.
        // "No new lines" and "read produced nothing" are indistinguishable downstream, which is why this
        // stayed invisible: the alert simply never fired.
        var buffer = new byte[16 * 1024];
        while (true)
        {
            // The token has to reach THIS line. Passing CancellationToken.None here is what kept a timed-out
            // fetch reading from dockerd until the proxy cut it 600 seconds later, while Whiskers had long
            // stopped waiting — the read loop behind 1.15 million syscalls per second (incident 2026-08-26).
            var read = await muxStream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (read.EOF) break;
            if (read.Count == 0) continue;

            var target = read.Target == MultiplexedStream.TargetStream.StandardError ? stderr : stdout;
            await target.WriteAsync(buffer.AsMemory(0, read.Count));
        }

        stdout.Position = 0;
        using var stdoutReader = new StreamReader(stdout);
        sb.Append(await stdoutReader.ReadToEndAsync());

        stderr.Position = 0;
        using var stderrReader = new StreamReader(stderr);
        sb.Append(await stderrReader.ReadToEndAsync());

        // What the read actually produced, so an empty result can be told apart from a failed one.
        _logger.LogDebug("Log read {Container} on {Server}: tail={Tail} since={Since} → {Out}B stdout, {Err}B stderr",
            containerId.Length > 12 ? containerId[..12] : containerId, serverId ?? "default",
            logParams.Tail, logParams.Since ?? "-", stdout.Length, stderr.Length);

        return sb.Length > 0 ? sb.ToString() : "(no logs available)";
    }

    public Task<(string State, int ExitCode, bool OomKilled)> InspectContainerStateAsync(string containerId, string? serverId = null)
        => InspectContainerStateAsync(containerId, serverId, CancellationToken.None);

    /// <summary>R1-41: <paramref name="ct"/> (a loop's stop) reaches the inspect and the wait for a slot and is not held
    /// against the server.</summary>
    public async Task<(string State, int ExitCode, bool OomKilled)> InspectContainerStateAsync(string containerId, string? serverId,
        CancellationToken ct)
    {
        // Plan-0001 WP6.3: under the budget. Both callers are background loops — the health monitor and the
        // auto-updater — so this is steady traffic, not something a person is waiting for.
        var inspect = await _connectionManager.ExecuteGuardedAsync(
            serverId, c => c.Containers.InspectContainerAsync(containerId, ct),
            singleFlightKey: $"inspectstate:{containerId}", callerToken: ct);
        return (
            inspect.State.Status,
            (int)inspect.State.ExitCode,
            inspect.State.OOMKilled
        );
    }

    /// <summary>The image reference the container was created from (<c>Config.Image</c>), or null when it cannot
    /// be inspected. A listing only shows the name while the tag still points at the running image — after a
    /// newer pull it shows the bare image ID, and only the inspect still knows which name to check.</summary>
    public Task<string?> GetConfiguredImageAsync(string containerId, string? serverId = null)
        => GetConfiguredImageAsync(containerId, serverId, CancellationToken.None);

    /// <summary>R1-41: a cancelled caller gets the cancellation, not a null that reads as "could not inspect".</summary>
    public async Task<string?> GetConfiguredImageAsync(string containerId, string? serverId, CancellationToken ct)
    {
        try
        {
            var inspect = await _connectionManager.ExecuteGuardedAsync(
                serverId, c => c.Containers.InspectContainerAsync(containerId, ct),
                singleFlightKey: $"configuredimage:{containerId}", callerToken: ct);
            return string.IsNullOrWhiteSpace(inspect.Config?.Image) ? null : inspect.Config.Image;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogDebug(ex, "Could not inspect container {Container} for its image", containerId);
            return null;
        }
    }

    /// <summary>Reads the container's log-driver configuration (Plan-0007 WP3).
    ///
    /// <para>Routed through the guarded path: this runs once per container per day across the whole fleet, and
    /// an inventory that floods the host it is inventorying would be the 2026-08-26 incident one level down.
    /// The single-flight key collapses concurrent asks about the same container.</para></summary>
    public async Task<ContainerLogConfiguration?> GetLogConfigurationAsync(string containerId, string? serverId = null)
    {
        return await _connectionManager.ExecuteGuardedAsync(serverId, async client =>
        {
            var inspect = await client.Containers.InspectContainerAsync(containerId);
            var log = inspect.HostConfig?.LogConfig;
            if (log is null) return null;

            var config = log.Config ?? new Dictionary<string, string>();
            config.TryGetValue("max-size", out var maxSize);
            config.TryGetValue("max-file", out var maxFile);

            return new ContainerLogConfiguration(
                string.IsNullOrWhiteSpace(log.Type) ? "json-file" : log.Type,
                string.IsNullOrWhiteSpace(maxSize) ? null : maxSize,
                string.IsNullOrWhiteSpace(maxFile) ? null : maxFile,
                string.IsNullOrWhiteSpace(inspect.LogPath) ? null : inspect.LogPath);
        }, singleFlightKey: $"logconfig:{containerId}");
    }

    public async Task<List<KeyValuePair<string, string>>> GetContainerEnvAsync(string containerId, string? serverId = null,
        CancellationToken ct = default)
    {
        var client = await GetClient(serverId).WaitAsync(ct);
        var inspect = await client.Containers.InspectContainerAsync(containerId, ct);
        var result = new List<KeyValuePair<string, string>>();

        foreach (var env in inspect.Config.Env ?? new List<string>())
        {
            var idx = env.IndexOf('=');
            if (idx > 0)
                result.Add(new KeyValuePair<string, string>(env[..idx], env[(idx + 1)..]));
            else
                result.Add(new KeyValuePair<string, string>(env, ""));
        }

        return result;
    }

    private static string ExtractHealthStatus(string status)
    {
        if (status.Contains("healthy", StringComparison.OrdinalIgnoreCase) &&
            !status.Contains("unhealthy", StringComparison.OrdinalIgnoreCase))
            return "healthy";
        if (status.Contains("unhealthy", StringComparison.OrdinalIgnoreCase))
            return "unhealthy";
        if (status.Contains("starting", StringComparison.OrdinalIgnoreCase))
            return "starting";
        return "none";
    }
}

// Internal DTOs for Docker stats JSON deserialization
internal class DockerStatsResponse
{
    public CpuStatsDto? CpuStats { get; set; }
    public CpuStatsDto? PrecpuStats { get; set; }
    public MemoryStatsDto? MemoryStats { get; set; }
    public Dictionary<string, NetworkStatsDto>? Networks { get; set; }
    public BlkioStatsDto? BlkioStats { get; set; }
}

internal class CpuStatsDto
{
    public CpuUsageDto? CpuUsage { get; set; }
    public long SystemCpuUsage { get; set; }
    public int OnlineCpus { get; set; }
}

internal class CpuUsageDto
{
    public long TotalUsage { get; set; }
}

internal class MemoryStatsDto
{
    public long Usage { get; set; }
    public long Limit { get; set; }
}

internal class NetworkStatsDto
{
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
}

internal class BlkioStatsDto
{
    public List<BlkioEntryDto>? IoServiceBytesRecursive { get; set; }
}

internal class BlkioEntryDto
{
    public string Op { get; set; } = "";
    public long Value { get; set; }
}
