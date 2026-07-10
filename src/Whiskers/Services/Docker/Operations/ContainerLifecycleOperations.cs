using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Whiskers.Models;

namespace Whiskers.Services.Docker.Operations;

/// <summary>
/// Container create/recreate and C12 update-rollback operations for the <see cref="DockerService"/> facade.
/// </summary>
internal sealed class ContainerLifecycleOperations
{
    private readonly IDockerConnectionManager _connectionManager;
    private readonly ImageOperations _imageOperations;
    private readonly ILogger<DockerService> _logger;

    public ContainerLifecycleOperations(
        IDockerConnectionManager connectionManager,
        ImageOperations imageOperations,
        ILogger<DockerService> logger)
    {
        _connectionManager = connectionManager;
        _imageOperations = imageOperations;
        _logger = logger;
    }

    private async Task<DockerClient> GetClient(string? serverId)
        => await _connectionManager.GetClientAsync(serverId);

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
                // HostIP absent/empty = bind all interfaces (Docker default); a loopback bind from
                // `ip:host:container` compose syntax restricts publishing to that one interface.
                new() { HostPort = hostPort, HostIP = request.PortBindIps.GetValueOrDefault(hostPort) }
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
        await _imageOperations.PullImageAsync(image, progress, serverId);

        // 3. Stop container
        progress?.Report("Stopping container...");
        try
        {
            await client.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
        }
        catch { } // May already be stopped

        // 4. Rename the old container out of the way instead of removing it, so a failed create/start can
        //    be rolled back. Removing first (the previous behaviour) destroyed the container permanently if
        //    step 5/6 threw (name conflict, invalid multi-network config, tunnel drop mid-flight).
        var oldName = $"{name}-old-{DateTime.UtcNow:yyyyMMddHHmmss}";
        progress?.Report("Renaming old container...");
        await client.Containers.RenameContainerAsync(containerId,
            new ContainerRenameParameters { NewName = oldName }, CancellationToken.None);

        // 5. Create the new container. Attach only the FIRST network at create time — engines older than
        //    API 1.44 reject a multi-network EndpointsConfig on create — then connect the rest afterwards.
        var allNetworks = inspect.NetworkSettings?.Networks?
            .ToDictionary(kv => kv.Key, kv => kv.Value) ?? new();
        var firstNetwork = allNetworks.Take(1).ToDictionary(kv => kv.Key, kv => kv.Value);

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
            NetworkingConfig = new NetworkingConfig { EndpointsConfig = firstNetwork }
        };

        string newId;
        try
        {
            progress?.Report("Creating new container...");
            var response = await client.Containers.CreateContainerAsync(createParams);
            newId = response.ID;

            // Connect any remaining networks the old container had.
            foreach (var (netName, endpoint) in allNetworks.Skip(1))
            {
                await client.Networks.ConnectNetworkAsync(netName,
                    new NetworkConnectParameters { Container = newId, EndpointConfig = endpoint });
            }

            // 6. Start new container
            progress?.Report("Starting new container...");
            await client.Containers.StartContainerAsync(newId, new ContainerStartParameters());
        }
        catch (Exception ex)
        {
            // Roll back: restore the old container's name and restart it so recreate never loses a container.
            _logger.LogError(ex, "Recreate of {Name} failed — rolling back to the previous container", name);
            progress?.Report("Recreate failed — rolling back...");
            try
            {
                await client.Containers.RenameContainerAsync(containerId,
                    new ContainerRenameParameters { NewName = name }, CancellationToken.None);
                await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "Rollback of {Name} failed; old container remains as {OldName}", name, oldName);
            }
            throw;
        }

        // 7. Success — remove the renamed old container.
        progress?.Report("Removing old container...");
        try
        {
            await client.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "New container {Name} is up but removing the old one ({OldName}) failed", name, oldName);
        }

        progress?.Report($"Container updated successfully (new ID: {newId[..12]})");
        _logger.LogInformation("Container {Name} recreated: {OldId} → {NewId}", name, containerId[..12], newId[..12]);

        return newId;
    }

    // ─────────────────────────── C12 update-rollback ───────────────────────────

    // Serializable snapshot of the pre-update container, enough to recreate it from the OLD image.
    private sealed class ContainerRollbackSnapshot
    {
        public string Name { get; set; } = "";
        public Config? Config { get; set; }
        public HostConfig? HostConfig { get; set; }
        public IDictionary<string, EndpointSettings>? Networks { get; set; }
    }

    private static readonly JsonSerializerOptions RollbackJsonOptions =
        new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    // The snapshot is round-tripped through System.Text.Json. Docker.DotNet's models carry Newtonsoft
    // attributes, so STJ serializes by C# property name — symmetric here, since we deserialize the same way and
    // only hand the rebuilt object back to Docker.DotNet, which re-serializes it with the correct wire names.
    private static string SerializeRollbackSnapshot(ContainerRollbackSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, RollbackJsonOptions);

    private static ContainerRollbackSnapshot DeserializeRollbackSnapshot(string json)
        => JsonSerializer.Deserialize<ContainerRollbackSnapshot>(json, RollbackJsonOptions)
           ?? throw new InvalidOperationException("Rollback-Snapshot konnte nicht gelesen werden.");

    public async Task<(string ImageId, string ConfigJson)> CaptureRollbackSnapshotAsync(string containerId, string? serverId = null)
    {
        var client = await GetClient(serverId);
        var inspect = await client.Containers.InspectContainerAsync(containerId);
        var snapshot = new ContainerRollbackSnapshot
        {
            Name = inspect.Name.TrimStart('/'),
            Config = inspect.Config,
            HostConfig = inspect.HostConfig,
            Networks = inspect.NetworkSettings?.Networks
        };
        // inspect.Image is the concrete image ID (sha256:…) currently in use. After the update the tag moves to
        // the new image, but this ID still exists locally (until pruned), so a rollback can recreate from it.
        return (inspect.Image, SerializeRollbackSnapshot(snapshot));
    }

    public async Task<string> RollbackContainerAsync(string containerName, string imageId, string configJson, string? serverId = null, IProgress<string>? progress = null)
    {
        var client = await GetClient(serverId);
        var snap = DeserializeRollbackSnapshot(configJson);

        // 1. Find the CURRENT container by name — its id changed on the (failed) update.
        progress?.Report("Locating current container...");
        var existing = (await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }))
            .FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == containerName));
        var currentId = existing?.ID;

        // 2. Rename the current one out of the way (don't destroy it until the rollback container is up), same
        //    fail-safe as RecreateContainerAsync — a failed rollback must never leave the container gone.
        var stashName = $"{containerName}-rollbackfail-{DateTime.UtcNow:yyyyMMddHHmmss}";
        if (currentId != null)
        {
            progress?.Report("Stopping current container...");
            try { await client.Containers.StopContainerAsync(currentId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }); }
            catch { }
            await client.Containers.RenameContainerAsync(currentId,
                new ContainerRenameParameters { NewName = stashName }, CancellationToken.None);
        }

        // 3. Create the rollback container from the OLD image + saved config (attach first network on create,
        //    connect the rest after — mirrors RecreateContainerAsync for old-engine compatibility).
        var allNetworks = snap.Networks?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new();
        var firstNetwork = allNetworks.Take(1).ToDictionary(kv => kv.Key, kv => kv.Value);
        var createParams = new CreateContainerParameters
        {
            Image = imageId,          // roll back to the OLD image by ID
            Name = containerName,
            Env = snap.Config?.Env,
            Cmd = snap.Config?.Cmd,
            Entrypoint = snap.Config?.Entrypoint,
            ExposedPorts = snap.Config?.ExposedPorts,
            Labels = snap.Config?.Labels,
            WorkingDir = snap.Config?.WorkingDir,
            HostConfig = snap.HostConfig,
            NetworkingConfig = new NetworkingConfig { EndpointsConfig = firstNetwork }
        };

        string newId;
        try
        {
            progress?.Report("Creating rollback container...");
            var response = await client.Containers.CreateContainerAsync(createParams);
            newId = response.ID;

            foreach (var (netName, endpoint) in allNetworks.Skip(1))
            {
                await client.Networks.ConnectNetworkAsync(netName,
                    new NetworkConnectParameters { Container = newId, EndpointConfig = endpoint });
            }

            progress?.Report("Starting rollback container...");
            await client.Containers.StartContainerAsync(newId, new ContainerStartParameters());
        }
        catch (Exception ex)
        {
            // Rollback of the rollback: restore the stashed container so we never lose it.
            _logger.LogError(ex, "Rollback recreate of {Name} failed — restoring the stashed container", containerName);
            progress?.Report("Rollback failed — restoring previous container...");
            if (currentId != null)
            {
                try
                {
                    await client.Containers.RenameContainerAsync(currentId,
                        new ContainerRenameParameters { NewName = containerName }, CancellationToken.None);
                    await client.Containers.StartContainerAsync(currentId, new ContainerStartParameters());
                }
                catch (Exception rex)
                {
                    _logger.LogError(rex, "Restoring stashed container {Name} failed; it remains as {Stash}", containerName, stashName);
                }
            }
            throw;
        }

        // 4. Success — remove the stashed (failed-update) container.
        if (currentId != null)
        {
            progress?.Report("Removing failed-update container...");
            try { await client.Containers.RemoveContainerAsync(currentId, new ContainerRemoveParameters { Force = true }); }
            catch (Exception ex) { _logger.LogWarning(ex, "Rollback of {Name} is up, but removing the stashed container failed", containerName); }
        }

        progress?.Report($"Rollback complete (new ID: {newId[..12]})");
        _logger.LogInformation("Container {Name} rolled back to image {Image}", containerName, imageId.Length > 19 ? imageId[..19] : imageId);
        return newId;
    }
}
