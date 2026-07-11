using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Metrics;

namespace Whiskers.Services.Docker.Operations;

/// <summary>
/// Server system info and host resource usage (reachability, CPU/memory, Docker counts) for the
/// <see cref="DockerService"/> facade.
/// </summary>
internal sealed class SystemInfoOperations
{
    private readonly IDockerConnectionManager _connectionManager;
    private readonly IServerConfigService _serverConfigService;
    private readonly IPrometheusMetricsSource _prometheusMetrics;
    private readonly ContainerOperations _containerOperations;
    private readonly ILogger<DockerService> _logger;
    private readonly MemoryCache _statsCache;

    public SystemInfoOperations(
        IDockerConnectionManager connectionManager,
        IServerConfigService serverConfigService,
        IPrometheusMetricsSource prometheusMetrics,
        ContainerOperations containerOperations,
        ILogger<DockerService> logger,
        MemoryCache statsCache)
    {
        _connectionManager = connectionManager;
        _serverConfigService = serverConfigService;
        _prometheusMetrics = prometheusMetrics;
        _containerOperations = containerOperations;
        _logger = logger;
        _statsCache = statsCache;
    }

    private async Task<DockerClient> GetClient(string? serverId)
        => await _connectionManager.GetClientAsync(serverId);

    public async Task<ServerSystemInfo> GetServerSystemInfoAsync(string? serverId = null)
    {
        var server = serverId != null
            ? _serverConfigService.GetServer(serverId)
            : _serverConfigService.GetDefaultServer();

        var info = new ServerSystemInfo
        {
            ServerId = server?.Id ?? "local",
            ServerName = server?.Name ?? "Local"
        };

        try
        {
            // Probe reachability through the retrying executor so a tunnel that died between polls
            // is rebuilt on this very call instead of reporting the server as unreachable.
            var (sysInfo, version) = await _connectionManager.ExecuteAsync(serverId, async c =>
                (await c.System.GetSystemInfoAsync(), await c.System.GetVersionAsync()));
            var client = await GetClient(serverId);

            info.OperatingSystem = sysInfo.OperatingSystem ?? "";
            info.OsVersion = sysInfo.OSVersion ?? "";
            info.KernelVersion = sysInfo.KernelVersion ?? "";
            info.Architecture = sysInfo.Architecture ?? "";
            info.CpuCount = (int)sysInfo.NCPU;
            info.MemoryTotalBytes = sysInfo.MemTotal;
            info.DockerVersion = version.Version ?? "";
            info.ContainersRunning = (int)sysInfo.ContainersRunning;
            info.ContainersStopped = (int)sysInfo.ContainersStopped;
            info.ContainersTotal = (int)sysInfo.Containers;
            info.ImagesCount = (int)sysInfo.Images;
            info.IsReachable = true;

            // Host CPU/memory usage. For Prometheus-configured servers, read from the TSDB
            // (node_exporter) so we don't spawn an SSH /proc-exec — OS/version/counts above still
            // come from the Docker API. Other servers use the Docker exec, with a container-stats
            // aggregate as fallback.
            if (server?.MetricsSource == MetricsSourceKind.Prometheus)
            {
                var p = await _prometheusMetrics.GetServerSystemInfoAsync(server.Id);
                if (p is { IsReachable: true })
                {
                    info.CpuUsagePercent = p.CpuUsagePercent;
                    info.MemoryUsedBytes = p.MemoryUsedBytes;
                }
                // If the TSDB has no data yet, leave CPU/Mem at 0 rather than falling back to an
                // SSH exec — that fallback would defeat the purpose of moving off SSH.
            }
            else
            {
                try
                {
                    var (hostCpu, hostMem) = await GetHostResourceUsageAsync(client, serverId);
                    info.CpuUsagePercent = hostCpu;
                    info.MemoryUsedBytes = hostMem;
                }
                catch
                {
                    // Fallback: aggregate from running containers (cached stats)
                    var containers = await client.Containers.ListContainersAsync(
                        new ContainersListParameters { All = false });
                    long totalMemUsed = 0;
                    double totalCpu = 0;
                    var statsTasks = containers.Select(async c =>
                    {
                        var s = await _containerOperations.GetContainerStatsAsync(c.ID, serverId);
                        if (s != null)
                        {
                            Interlocked.Add(ref totalMemUsed, s.MemoryUsageBytes);
                            double cur, nv;
                            do { cur = totalCpu; nv = cur + s.CpuPercent; }
                            while (cur != Interlocked.CompareExchange(ref totalCpu, nv, cur));
                        }
                    });
                    await Task.WhenAll(statsTasks);
                    info.MemoryUsedBytes = totalMemUsed;
                    info.CpuUsagePercent = Math.Round(totalCpu, 2);
                }
            }

            // IP address from Docker info
            if (server?.ConnectionType == ConnectionType.SSH)
                info.IpAddress = server.SshHost ?? "";
            else if (server?.ConnectionType == ConnectionType.TCP)
                info.IpAddress = server.TcpHost ?? "";
            else
                info.IpAddress = sysInfo.Name ?? "localhost";
        }
        catch (Exception ex)
        {
            info.IsReachable = false;
            info.Error = ex.Message;
            _logger.LogWarning(ex, "Failed to get system info for server {ServerId}", serverId);
        }

        return info;
    }

    public async Task<Dictionary<string, ServerSystemInfo>> GetAllServerSystemInfoAsync()
    {
        // Kubernetes servers are handled by the workload seam, not the Docker system-info probe.
        var servers = _serverConfigService.GetEnabledServers()
            .Where(s => s.ConnectionType != ConnectionType.Kubernetes).ToList();
        // Bound each server's reachability probe so one dead host (whose retrying executor can take up
        // to ~60s) can't stall the whole dashboard load — it's marked unreachable after 8s instead.
        var perServerTimeout = TimeSpan.FromSeconds(8);
        var tasks = servers.Select(async s =>
        {
            var infoTask = GetServerSystemInfoAsync(s.Id);
            var winner = await Task.WhenAny(infoTask, Task.Delay(perServerTimeout));
            if (winner == infoTask)
                return (s.Id, info: await infoTask);

            _ = infoTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            _logger.LogWarning("System info for server {ServerName} timed out ({Timeout}s) — marking unreachable (degraded view).",
                s.Name, perServerTimeout.TotalSeconds);
            return (s.Id, info: new ServerSystemInfo
            {
                ServerId = s.Id,
                ServerName = s.Name,
                IsReachable = false,
                Error = $"Zeitüberschreitung nach {perServerTimeout.TotalSeconds:0}s"
            });
        });

        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.Id, r => r.info);
    }

    /// <summary>
    /// Read real host CPU and memory usage via a short-lived alpine container
    /// that reads /proc/stat and /proc/meminfo from the host PID namespace.
    /// For local: reads directly from host /proc mounted into the container.
    /// </summary>
    private async Task<(double cpuPercent, long memUsedBytes)> GetHostResourceUsageAsync(
        DockerClient client, string? serverId)
    {
        var cacheKey = $"hostres:{serverId ?? "local"}";
        if (_statsCache.TryGetValue(cacheKey, out (double cpu, long mem) cachedRes))
            return cachedRes;

        // Try to exec into the serverwatch container itself (which has /proc/1/root access)
        // Simpler approach: read from the host /proc mount
        try
        {
            // For local server, the host's /proc is at /host_proc (we'll mount it)
            // For remote servers, we need a different approach
            var containers = await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = false, Limit = 1 });

            if (!containers.Any())
                throw new InvalidOperationException("No running containers");

            var containerId = containers.First().ID;
            var execParams = new ContainerExecCreateParameters
            {
                Cmd = new[] { "sh", "-c",
                    "head -1 /proc/stat && head -3 /proc/meminfo && sleep 0.3 && head -1 /proc/stat" },
                AttachStdout = true,
                AttachStderr = true
            };

            var exec = await client.Exec.ExecCreateContainerAsync(containerId, execParams);
            using var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, false);
            var (stdout, stderr) = await stream.ReadOutputToEndAsync(CancellationToken.None);

            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 4)
                throw new InvalidOperationException("Unexpected /proc output");

            // Parse CPU: "cpu  user nice system idle iowait irq softirq steal"
            var cpu1 = ParseCpuLine(lines[0]);
            var cpu2 = ParseCpuLine(lines[^1]); // last line = second sample

            var totalDelta = cpu2.total - cpu1.total;
            var idleDelta = cpu2.idle - cpu1.idle;
            var cpuPercent = totalDelta > 0 ? Math.Round((1.0 - (double)idleDelta / totalDelta) * 100, 1) : 0;

            // Parse Memory: MemTotal, MemFree, MemAvailable
            long memTotal = 0, memAvailable = 0;
            foreach (var line in lines.Skip(1).Take(3))
            {
                if (line.StartsWith("MemTotal:"))
                    memTotal = ParseMemLine(line);
                else if (line.StartsWith("MemAvailable:"))
                    memAvailable = ParseMemLine(line);
            }
            var memUsed = memTotal - memAvailable;

            var result = (cpuPercent, memUsed);
            _statsCache.Set(cacheKey, result, TimeSpan.FromSeconds(5));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read host /proc, falling back to container stats");
            throw;
        }
    }

    private static (long total, long idle) ParseCpuLine(string line)
    {
        // "cpu  3357 0 4313 1362393 ..."
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[0] != "cpu")
            return (0, 0);

        var values = parts.Skip(1).Select(long.Parse).ToArray();
        var total = values.Sum();
        var idle = values.Length > 3 ? values[3] : 0; // idle is 4th field
        if (values.Length > 4) idle += values[4]; // add iowait
        return (total, idle);
    }

    private static long ParseMemLine(string line)
    {
        // "MemTotal:       64345678 kB"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
            return kb * 1024; // convert kB to bytes
        return 0;
    }
}
