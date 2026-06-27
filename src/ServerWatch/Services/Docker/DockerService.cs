using System.Collections.Concurrent;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;
using ServerWatch.Models;
using ServerWatch.Services.ServerConfig;
using ServerWatch.Services.Metrics;

namespace ServerWatch.Services.Docker;

public class DockerService : IDockerService
{
    private readonly DockerConnectionManager _connectionManager;
    private readonly ServerConfigService _serverConfigService;
    private readonly PrometheusMetricsSource _prometheusMetrics;
    private readonly ILogger<DockerService> _logger;
    private readonly MemoryCache _statsCache = new(new MemoryCacheOptions());
    private static readonly TimeSpan StatsCacheDuration = TimeSpan.FromSeconds(3);

    public DockerService(
        DockerConnectionManager connectionManager,
        ServerConfigService serverConfigService,
        PrometheusMetricsSource prometheusMetrics,
        ILogger<DockerService> logger)
    {
        _connectionManager = connectionManager;
        _serverConfigService = serverConfigService;
        _prometheusMetrics = prometheusMetrics;
        _logger = logger;
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

        return containers.Select(c => new ContainerInfo
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
            ServerId = server?.Id ?? "local",
            ServerName = server?.Name ?? "Local"
        }).ToList();
    }

    public async Task<IList<ContainerInfo>> ListAllContainersAsync(bool all = true)
    {
        var servers = _serverConfigService.GetEnabledServers();
        var tasks = servers.Select(async server =>
        {
            try
            {
                return await ListContainersAsync(all, server.Id);
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
            var response = await client.Containers.GetContainerStatsAsync(
                containerId,
                new ContainerStatsParameters { Stream = false },
                cts.Token);

            using var reader = new StreamReader(response);
            var json = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(json))
                return null;

            var stats = System.Text.Json.JsonSerializer.Deserialize<DockerStatsResponse>(json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
                });

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

    public async Task<string> GetContainerLogsAsync(string containerId, int tailLines = 100, string? serverId = null)
    {
        var client = await GetClient(serverId);
        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Tail = tailLines.ToString(),
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

    private const string HostShellImage = "alpine:latest";

    /// <summary>
    /// Runs a shell command on the host via a one-shot privileged container that nsenters into the
    /// host namespaces (pid 1). This is the SSH-free shell plane for TCP+mTLS servers: it goes over
    /// the same mTLS Docker connection, so no SSH key is involved. The container is created, started,
    /// waited on, its logs read, and then force-removed.
    /// </summary>
    public async Task<(string Output, string Error, int ExitCode)> RunHostShellAsync(
        string command, string? serverId = null, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var client = await GetClient(serverId);

        // Ensure the helper image exists on the target (pull once if missing).
        try
        {
            await client.Images.InspectImageAsync(HostShellImage);
        }
        catch (DockerImageNotFoundException)
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "alpine", Tag = "latest" },
                null, new Progress<JSONMessage>());
        }

        var create = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = HostShellImage,
            Cmd = new List<string> { "nsenter", "-t", "1", "-m", "-u", "-i", "-n", "-p", "--", "sh", "-c", command },
            HostConfig = new HostConfig { Privileged = true, PidMode = "host", AutoRemove = false }
        });
        var id = create.ID;

        try
        {
            await client.Containers.StartContainerAsync(id, new ContainerStartParameters());

            long exitCode;
            using (var cts = new CancellationTokenSource(effectiveTimeout))
            {
                try
                {
                    var wait = await client.Containers.WaitContainerAsync(id, cts.Token);
                    exitCode = wait.StatusCode;
                }
                catch (OperationCanceledException)
                {
                    try { await client.Containers.KillContainerAsync(id, new ContainerKillParameters()); } catch { /* best effort */ }
                    return ("", $"Command timed out after {effectiveTimeout.TotalSeconds}s", -1);
                }
            }

            using var mux = await client.Containers.GetContainerLogsAsync(id, false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true });
            var stdout = new MemoryStream();
            var stderr = new MemoryStream();
            await mux.CopyOutputToAsync(Stream.Null, stdout, stderr, CancellationToken.None);
            stdout.Position = 0;
            stderr.Position = 0;
            var outStr = await new StreamReader(stdout).ReadToEndAsync();
            var errStr = await new StreamReader(stderr).ReadToEndAsync();

            return (outStr, errStr, (int)exitCode);
        }
        finally
        {
            try { await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }); }
            catch { /* best effort cleanup */ }
        }
    }

    public async Task<string> CreateContainerAsync(DeploymentRequest request, string? serverId = null)
    {
        var client = await GetClient(serverId);
        var portBindings = new Dictionary<string, IList<PortBinding>>();
        var exposedPorts = new Dictionary<string, EmptyStruct>();

        foreach (var (hostPort, containerPort) in request.PortMappings)
        {
            var key = $"{containerPort}/tcp";
            exposedPorts[key] = default;
            portBindings[key] = new List<PortBinding>
            {
                new() { HostPort = hostPort }
            };
        }

        var restartPolicy = request.RestartPolicy switch
        {
            "always" => new RestartPolicy { Name = RestartPolicyKind.Always },
            "unless-stopped" => new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
            "on-failure" => new RestartPolicy { Name = RestartPolicyKind.OnFailure, MaximumRetryCount = 5 },
            _ => new RestartPolicy { Name = RestartPolicyKind.No }
        };

        var createParams = new CreateContainerParameters
        {
            Image = request.Image,
            Name = request.ContainerName,
            ExposedPorts = exposedPorts,
            Env = request.EnvironmentVars.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            HostConfig = new HostConfig
            {
                PortBindings = portBindings,
                Binds = request.Volumes,
                RestartPolicy = restartPolicy,
                NetworkMode = request.NetworkName ?? "bridge"
            }
        };

        var response = await client.Containers.CreateContainerAsync(createParams);
        await client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

        return response.ID;
    }

    public async Task PullImageAsync(string imageName, IProgress<string>? progress = null, string? serverId = null)
    {
        var client = await GetClient(serverId);
        var parts = imageName.Split(':');
        var repo = parts[0];
        var tag = parts.Length > 1 ? parts[1] : "latest";

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = repo, Tag = tag },
            null,
            new Progress<JSONMessage>(msg =>
            {
                progress?.Report(msg.Status ?? "");
            }));
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
                        var s = await GetContainerStatsAsync(c.ID, serverId);
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
        var servers = _serverConfigService.GetEnabledServers();
        var tasks = servers.Select(async s =>
        {
            var info = await GetServerSystemInfoAsync(s.Id);
            return (s.Id, info);
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

        // Use exec on a running container to read host /proc
        // We need two CPU samples with a small delay
        var script = "cat /host_proc/stat | head -1 && cat /host_proc/meminfo | head -3 && sleep 0.5 && cat /host_proc/stat | head -1";

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

    public async Task<string> RecreateContainerAsync(string containerId, string? serverId = null, IProgress<string>? progress = null)
    {
        var client = await GetClient(serverId);

        // 1. Inspect current container to capture all settings
        progress?.Report("Inspecting container...");
        var inspect = await client.Containers.InspectContainerAsync(containerId);
        var config = inspect.Config;
        var hostConfig = inspect.HostConfig;
        var name = inspect.Name.TrimStart('/');
        var image = config.Image;

        // 2. Pull latest image
        progress?.Report($"Pulling {image}...");
        await PullImageAsync(image, progress, serverId);

        // 3. Stop container
        progress?.Report("Stopping container...");
        try
        {
            await client.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
        }
        catch { } // May already be stopped

        // 4. Remove old container
        progress?.Report("Removing old container...");
        await client.Containers.RemoveContainerAsync(containerId,
            new ContainerRemoveParameters { Force = true });

        // 5. Create new container with same settings
        progress?.Report("Creating new container...");
        var createParams = new CreateContainerParameters
        {
            Image = image,
            Name = name,
            Env = config.Env,
            Cmd = config.Cmd,
            Entrypoint = config.Entrypoint,
            ExposedPorts = config.ExposedPorts,
            Labels = config.Labels,
            WorkingDir = config.WorkingDir,
            HostConfig = hostConfig,
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = inspect.NetworkSettings?.Networks?
                    .ToDictionary(kv => kv.Key, kv => kv.Value) ?? new()
            }
        };

        var response = await client.Containers.CreateContainerAsync(createParams);

        // 6. Start new container
        progress?.Report("Starting new container...");
        await client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

        progress?.Report($"Container updated successfully (new ID: {response.ID[..12]})");
        _logger.LogInformation("Container {Name} recreated: {OldId} → {NewId}", name, containerId[..12], response.ID[..12]);

        return response.ID;
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

    // === Networks ===

    public async Task<IList<NetworkInfo>> ListNetworksAsync(string? serverId = null)
    {
        var server = serverId != null
            ? _serverConfigService.GetServer(serverId)
            : _serverConfigService.GetDefaultServer();
        var client = await GetClient(serverId);
        var networks = await client.Networks.ListNetworksAsync();

        return networks.Select(n =>
        {
            var info = new NetworkInfo
            {
                Id = n.ID,
                Name = n.Name,
                Driver = n.Driver ?? "",
                Scope = n.Scope ?? "",
                Internal = n.Internal,
                ServerId = server?.Id ?? "local",
                ServerName = server?.Name ?? "Local"
            };

            if (n.IPAM?.Config?.Any() == true)
            {
                var ipamConfig = n.IPAM.Config.First();
                info.Subnet = ipamConfig.Subnet ?? "";
                info.Gateway = ipamConfig.Gateway ?? "";
            }

            if (n.Containers != null)
            {
                info.Containers = n.Containers.Select(c => new NetworkContainer
                {
                    ContainerId = c.Key,
                    Name = c.Value.Name ?? "",
                    IPv4Address = c.Value.IPv4Address ?? ""
                }).ToList();
            }

            return info;
        }).ToList();
    }

    public async Task<string> CreateNetworkAsync(string name, string driver = "bridge", string? serverId = null)
    {
        var client = await GetClient(serverId);
        var response = await client.Networks.CreateNetworkAsync(new global::Docker.DotNet.Models.NetworksCreateParameters
        {
            Name = name,
            Driver = driver
        });
        return response.ID;
    }

    public async Task RemoveNetworkAsync(string networkId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Networks.DeleteNetworkAsync(networkId);
    }

    public async Task ConnectContainerToNetworkAsync(string networkId, string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Networks.ConnectNetworkAsync(networkId, new global::Docker.DotNet.Models.NetworkConnectParameters
        {
            Container = containerId
        });
    }

    public async Task DisconnectContainerFromNetworkAsync(string networkId, string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        await client.Networks.DisconnectNetworkAsync(networkId, new global::Docker.DotNet.Models.NetworkDisconnectParameters
        {
            Container = containerId
        });
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
