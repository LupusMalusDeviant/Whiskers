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
        MemoryCache statsCache)
    {
        _connectionManager = connectionManager;
        _serverConfigService = serverConfigService;
        _logger = logger;
        _statsCache = statsCache;
    }

    private async Task<DockerClient> GetClient(string? serverId)
        => await _connectionManager.GetClientAsync(serverId);

    public async Task<IList<ContainerInfo>> ListContainersAsync(bool all = true, string? serverId = null)
    {
        var server = serverId != null
            ? _serverConfigService.GetServer(serverId)
            : _serverConfigService.GetDefaultServer();

        var containers = await _connectionManager.ExecuteAsync(serverId, c =>
            c.Containers.ListContainersAsync(new ContainersListParameters { All = all }));

        return containers.Select(c => ToContainerInfo(c, server?.Id ?? "local", server?.Name ?? "Local")).ToList();
    }

    private ContainerInfo ToContainerInfo(ContainerListResponse c, string serverId, string serverName) => new()
    {
        Id = c.ID,
        Name = c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12],
        Image = c.Image,
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
    {
        // Kubernetes servers are not Docker hosts — their workloads come from the workload seam
        // (Services/Workloads), not from this Docker-only aggregation.
        var servers = _serverConfigService.GetEnabledServers()
            .Where(s => s.ConnectionType != ConnectionType.Kubernetes).ToList();
        // Bound each server with a short timeout so ONE unreachable host can't blank the whole
        // dashboard: a slow/dead server is skipped after 8s (returns empty) while the reachable ones
        // render immediately. Unreachability itself is surfaced separately via GetServerSystemInfoAsync.
        var perServerTimeout = TimeSpan.FromSeconds(8);
        var tasks = servers.Select(async server =>
        {
            try
            {
                var listTask = ListContainersAsync(all, server.Id);
                var winner = await Task.WhenAny(listTask, Task.Delay(perServerTimeout));
                if (winner == listTask)
                    return await listTask;

                // Timed out — observe any later fault so it isn't an unobserved exception, then degrade.
                _ = listTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                _logger.LogWarning("Listing containers for server {ServerName} timed out ({Timeout}s) — skipping (degraded view).",
                    server.Name, perServerTimeout.TotalSeconds);
                return (IList<ContainerInfo>)new List<ContainerInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list containers for server {ServerName}", server.Name);
                return (IList<ContainerInfo>)new List<ContainerInfo>();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    public async Task<ContainerStats?> GetContainerStatsAsync(string containerId, string? serverId = null)
    {
        var cacheKey = $"stats:{serverId ?? "local"}:{containerId}";
        if (_statsCache.TryGetValue(cacheKey, out ContainerStats? cached))
            return cached;

        try
        {
            var client = await GetClient(serverId);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Single-shot (Stream=false) returns the stats JSON as a Stream — all we need here. The
            // non-obsolete overload requires an IProgress<JSONMessage> streaming callback; keep the
            // simpler obsolete overload deliberately.
#pragma warning disable CS0618
            var response = await client.Containers.GetContainerStatsAsync(
                containerId,
                new ContainerStatsParameters { Stream = false },
                cts.Token);
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

    public async Task<string> GetContainerLogsAsync(string containerId, int tailLines = 100, string? serverId = null, DateTime? since = null)
    {
        var client = await GetClient(serverId);
        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            // With a since-timestamp, return every line after it (log monitoring wants all new lines, not
            // just the tail); otherwise keep the tail limit.
            Tail = since.HasValue ? "all" : tailLines.ToString(),
            Since = since.HasValue
                ? ((DateTimeOffset)DateTime.SpecifyKind(since.Value, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString()
                : null,
            Timestamps = true
        };

        using var muxStream = await client.Containers.GetContainerLogsAsync(containerId, false, logParams);

        var sb = new StringBuilder();
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        await muxStream.CopyOutputToAsync(Stream.Null, stdout, stderr, CancellationToken.None);

        stdout.Position = 0;
        using var stdoutReader = new StreamReader(stdout);
        sb.Append(await stdoutReader.ReadToEndAsync());

        stderr.Position = 0;
        using var stderrReader = new StreamReader(stderr);
        sb.Append(await stderrReader.ReadToEndAsync());

        return sb.Length > 0 ? sb.ToString() : "(no logs available)";
    }

    public async Task<(string State, int ExitCode, bool OomKilled)> InspectContainerStateAsync(string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        var inspect = await client.Containers.InspectContainerAsync(containerId);
        return (
            inspect.State.Status,
            (int)inspect.State.ExitCode,
            inspect.State.OOMKilled
        );
    }

    public async Task<List<KeyValuePair<string, string>>> GetContainerEnvAsync(string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        var inspect = await client.Containers.InspectContainerAsync(containerId);
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
