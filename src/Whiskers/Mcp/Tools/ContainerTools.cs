using ModelContextProtocol.Server;
using System.ComponentModel;
using Whiskers.Services.Docker;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Server;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.AuditLog;
using Whiskers.Utils;
using Microsoft.AspNetCore.Http;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp.Tools;

[McpServerToolType]
public class ContainerTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List all Docker containers across all configured servers. Returns container name, image, state, health, server, and compose project.")]
    public static async Task<string> ListContainers(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        [Description("Optional server ID to filter by. Omit for all servers.")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_containers");
        if (denied != null) return denied;
        var containers = serverId != null
            ? await docker.ListContainersAsync(all: true, serverId: serverId)
            : await docker.ListAllContainersAsync(all: true);

        var lines = containers.Select(c =>
            $"- {c.Name} [{c.State}] ({c.HealthStatus}) | Image: {c.Image} | Server: {c.ServerName} | Project: {c.ComposeProject}");
        return $"Found {containers.Count} containers:\n{string.Join('\n', lines)}";
    }

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("Get detailed information about a specific Docker container including its configuration, ports, labels, and stats.")]
    public static async Task<string> GetContainerDetails(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        [Description("The full container ID")] string containerId,
        [Description("Server ID where the container runs")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_container_details");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID is required.";

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var (container, resolveError) = McpInputValidation.Resolve(containers, containerId);
        if (container is null) return resolveError ?? $"Container not found: {containerId}";

        var stats = await docker.GetContainerStatsAsync(container.Id, serverId);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Name: {container.Name}");
        sb.AppendLine($"ID: {container.Id}");
        sb.AppendLine($"Image: {container.Image}");
        sb.AppendLine($"State: {container.State}");
        sb.AppendLine($"Health: {container.HealthStatus}");
        sb.AppendLine($"Status: {container.Status}");
        sb.AppendLine($"Created: {container.Created}");
        sb.AppendLine($"Server: {container.ServerName} ({container.ServerId})");
        if (container.Ports.Any())
            sb.AppendLine($"Ports: {string.Join(", ", container.Ports.Select(p => $"{p.PublicPort}→{p.PrivatePort}/{p.Type}"))}");
        if (container.Labels.Any())
            sb.AppendLine($"Labels: {string.Join(", ", container.Labels.Select(l => $"{l.Key}={l.Value}"))}");
        if (stats != null)
        {
            sb.AppendLine($"CPU: {stats.CpuPercent:F1}%");
            sb.AppendLine($"Memory: {stats.MemoryUsageBytes / 1048576.0:F1} MB / {stats.MemoryLimitBytes / 1073741824.0:F1} GB");
        }
        return sb.ToString();
    }

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("Get logs from a Docker container.")]
    public static async Task<string> GetContainerLogs(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        [Description("Container ID or name")] string containerId,
        [Description("Number of tail lines (default 100)")] int lines = 100,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_container_logs");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID or name is required.";

        if (lines <= 0 || lines > 10000)
            lines = 100;

        // Find the container to resolve name to ID
        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var (container, resolveError) = McpInputValidation.Resolve(containers, containerId);
        if (container is null) return resolveError ?? $"Container not found: {containerId}";
        var id = container.Id;
        var logs = await docker.GetContainerLogsAsync(id, lines, serverId);
        return $"Logs for {container?.Name ?? containerId} (last {lines} lines):\n{logs}";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Start a stopped Docker container.")]
    public static async Task<string> StartContainer(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IToolAuditLog auditLog,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "start_container");
        if (denied != null) return MutationScope.Fail(denied);
        if (string.IsNullOrWhiteSpace(containerId))
            return MutationScope.Fail("Container ID or name is required.");

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var (container, resolveError) = McpInputValidation.Resolve(containers, containerId);
        if (container is null) return MutationScope.Fail(resolveError ?? $"Container not found: {containerId}");
        var id = container.Id;
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        try
        {
            await docker.StartContainerAsync(id, serverId);
        }
        catch
        {
            await auditLog.LogAsync(actor, actorType, "container.start", "container", id, container?.Name ?? containerId, serverId: serverId, success: false);
            throw;
        }
        await auditLog.LogAsync(actor, actorType, "container.start", "container", id, container?.Name ?? containerId, serverId: serverId);
        return $"Container {container?.Name ?? containerId} started.";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Stop a running Docker container.")]
    public static async Task<string> StopContainer(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IToolAuditLog auditLog,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "stop_container");
        if (denied != null) return MutationScope.Fail(denied);
        if (string.IsNullOrWhiteSpace(containerId))
            return MutationScope.Fail("Container ID or name is required.");

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var (container, resolveError) = McpInputValidation.Resolve(containers, containerId);
        if (container is null) return MutationScope.Fail(resolveError ?? $"Container not found: {containerId}");
        var id = container.Id;
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        try
        {
            await docker.StopContainerAsync(id, serverId);
        }
        catch
        {
            await auditLog.LogAsync(actor, actorType, "container.stop", "container", id, container?.Name ?? containerId, serverId: serverId, success: false);
            throw;
        }
        await auditLog.LogAsync(actor, actorType, "container.stop", "container", id, container?.Name ?? containerId, serverId: serverId);
        return $"Container {container?.Name ?? containerId} stopped.";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Restart a Docker container.")]
    public static async Task<string> RestartContainer(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IToolAuditLog auditLog,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "restart_container");
        if (denied != null) return MutationScope.Fail(denied);
        if (string.IsNullOrWhiteSpace(containerId))
            return MutationScope.Fail("Container ID or name is required.");

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var (container, resolveError) = McpInputValidation.Resolve(containers, containerId);
        if (container is null) return MutationScope.Fail(resolveError ?? $"Container not found: {containerId}");
        var id = container.Id;
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        try
        {
            await docker.RestartContainerAsync(id, serverId);
        }
        catch
        {
            await auditLog.LogAsync(actor, actorType, "container.restart", "container", id, container?.Name ?? containerId, serverId: serverId, success: false);
            throw;
        }
        await auditLog.LogAsync(actor, actorType, "container.restart", "container", id, container?.Name ?? containerId, serverId: serverId);
        return $"Container {container?.Name ?? containerId} restarted.";
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Pull latest image and recreate a Docker container (update).")]
    public static async Task<string> UpdateContainer(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IImageUpdateStore updateStore,
        IToolAuditLog auditLog,
        Whiskers.Services.AutoUpdate.IAutoUpdateService autoUpdate,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "update_container");
        if (denied != null) return MutationScope.Fail(denied);
        if (string.IsNullOrWhiteSpace(containerId))
            return MutationScope.Fail("Container ID or name is required.");

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var (container, resolveError) = McpInputValidation.Resolve(containers, containerId);
        if (container is null) return MutationScope.Fail(resolveError ?? $"Container not found: {containerId}");
        var id = container.Id;

        var messages = new List<string>();
        var progress = new Progress<string>(msg => messages.Add(msg));
        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);

        // Capture the way back BEFORE the recreate — the same snapshot the Dashboard's update button and the
        // auto-updater take. This path did not, so every update driven by an agent or a script left no
        // rollback at all: the one caller least able to notice, and least able to fix it by hand. Found on
        // 2026-08-28 by checking UpdateRollbacks after an update that reported success.
        try { await autoUpdate.CaptureSnapshotAsync(container); }
        catch (Exception ex)
        {
            // Not fatal — but it must be said. An update that silently lost its way back looks exactly like
            // one that kept it.
            messages.Add($"WARNING: could not capture a rollback snapshot ({ex.Message}). " +
                         "The update will proceed, but it cannot be rolled back from here.");
        }

        string newId;
        try
        {
            newId = await docker.RecreateContainerAsync(id, serverId, progress);
        }
        catch
        {
            await auditLog.LogAsync(actor, actorType, "container.update", "container", id, container?.Name ?? containerId, serverId: serverId, success: false);
            throw;
        }
        updateStore.Remove(id, serverId ?? "local");
        await auditLog.LogAsync(actor, actorType, "container.update", "container", newId, container?.Name ?? containerId, serverId: serverId);
        return $"Container updated:\n{string.Join('\n', messages)}\nNew ID: {newId[..12]}";
    }

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("Check which containers have image updates available.")]
    public static async Task<string> GetUpdateStatus(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IImageUpdateStore updateStore)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_update_status");
        if (denied != null) return denied;
        var containers = await docker.ListAllContainersAsync(all: false);
        var updates = containers
            .Select(c => (Container: c, Update: updateStore.Get(c.Id, c.ServerId)))
            .Where(x => x.Update?.UpdateAvailable == true)
            .ToList();

        if (!updates.Any()) return "All containers are up to date.";

        var lines = updates.Select(x => $"- {x.Container.Name} ({x.Container.Image}): Local {x.Update!.LocalDigest[..12]}… → Remote {x.Update.RemoteDigest[..12]}…");
        return $"{updates.Count} updates available:\n{string.Join('\n', lines)}";
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Deploy a new application on a server using a standardized template. Supports common app types like web apps, databases, and custom Docker images. Creates the container with sensible defaults and starts it.")]
    public static async Task<string> DeployApp(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IToolAuditLog auditLog,
        [Description("Application name (used as container name)")] string appName,
        [Description("Docker image to deploy (e.g. 'nginx:latest', 'postgres:17', 'node:22-alpine')")] string image,
        [Description("Server ID to deploy on")] string? serverId = null,
        [Description("Port mappings as 'hostPort:containerPort' comma-separated (e.g. '8080:80,8443:443')")] string? ports = null,
        [Description("Environment variables as 'KEY=VALUE' comma-separated (e.g. 'DB_HOST=localhost,DB_PORT=5432')")] string? envVars = null,
        [Description("Volume mounts as 'hostPath:containerPath' comma-separated (e.g. '/data/myapp:/app/data')")] string? volumes = null,
        [Description("Restart policy: 'always', 'unless-stopped', 'on-failure', 'no' (default: 'unless-stopped')")] string restartPolicy = "unless-stopped",
        [Description("Docker network to connect to (default: 'bridge')")] string? network = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "deploy_app");
        if (denied != null) return MutationScope.Fail(denied);
        // Validate app name
        if (string.IsNullOrWhiteSpace(appName) || !System.Text.RegularExpressions.Regex.IsMatch(appName, @"^[a-zA-Z0-9._-]+$"))
            return MutationScope.Fail("Error: Invalid app name. Use only letters, numbers, dots, hyphens, and underscores.");

        if (string.IsNullOrWhiteSpace(image))
            return MutationScope.Fail("Error: Image is required.");

        var request = new DeploymentRequest
        {
            ContainerName = appName,
            Image = image,
            RestartPolicy = restartPolicy,
            NetworkName = network
        };

        // Parse port mappings
        if (!string.IsNullOrWhiteSpace(ports))
        {
            foreach (var mapping in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = mapping.Split(':');
                if (parts.Length == 2)
                    request.PortMappings[parts[0].Trim()] = parts[1].Trim();
            }
        }

        // Parse environment variables
        if (!string.IsNullOrWhiteSpace(envVars))
        {
            foreach (var ev in envVars.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eqIdx = ev.IndexOf('=');
                if (eqIdx > 0)
                    request.EnvironmentVars[ev[..eqIdx].Trim()] = ev[(eqIdx + 1)..].Trim();
            }
        }

        // Parse volumes
        if (!string.IsNullOrWhiteSpace(volumes))
        {
            request.Volumes = volumes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        try
        {
            // Pull image first
            await docker.PullImageAsync(image, null, serverId);

            // Create and start container
            var containerId = await docker.CreateContainerAsync(request, serverId);
            await auditLog.LogAsync(actor, actorType, "deploy.app", "container", containerId, appName, details: $"Image: {image}", serverId: serverId);
            return $"App '{appName}' deployed successfully!\n" +
                   $"  Container ID: {containerId[..12]}\n" +
                   $"  Image: {image}\n" +
                   (request.PortMappings.Any() ? $"  Ports: {string.Join(", ", request.PortMappings.Select(p => $"{p.Key}→{p.Value}"))}\n" : "") +
                   $"  Restart: {restartPolicy}\n" +
                   $"  Server: {serverId ?? "local"}";
        }
        catch (Exception ex)
        {
            await auditLog.LogAsync(actor, actorType, "deploy.app", "container", appName, appName, details: $"Image: {image}; {ex.Message}", serverId: serverId, success: false);
            return MutationScope.Fail($"Deployment failed: {ex.Message}");
        }
    }

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("Get environment variables of a running Docker container. Sensitive values (keys, secrets, passwords, tokens) are masked for security.")]
    public static async Task<string> GetContainerEnv(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_container_env");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID or name is required.";

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var (container, resolveError) = McpInputValidation.Resolve(containers, containerId);
        if (container is null) return resolveError ?? $"Container not found: {containerId}";
        var id = container.Id;

        var envVars = await docker.GetContainerEnvAsync(id, serverId);

        string[] sensitiveKeywords = ["KEY", "SECRET", "PASSWORD", "TOKEN", "CREDENTIAL", "AUTH", "PRIVATE", "PASSPHRASE"];
        bool IsSensitive(string key) => sensitiveKeywords.Any(kw => key.Contains(kw, StringComparison.OrdinalIgnoreCase));

        var lines = envVars.Select(e => IsSensitive(e.Key)
            ? $"  {e.Key}=••••••••"
            : $"  {e.Key}={e.Value}");

        return $"Environment variables for {container?.Name ?? containerId}:\n{string.Join('\n', lines)}";
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Set environment variables in a container's .env file and restart via docker compose. Only works for containers managed by docker-compose. Provide variables as 'KEY=VALUE' pairs separated by newlines or commas. Existing variables not included are kept unchanged.")]
    public static async Task<string> SetContainerEnv(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IDockerService docker,
        IHostCommandExecutor executor,
        IComposePlanner planner,
        IToolAuditLog auditLog,
        [Description("Container ID or name")] string containerId,
        [Description("Environment variables to set as 'KEY=VALUE' pairs, comma-separated (e.g. 'OPENAI_API_KEY=sk-...,MODEL=gpt-4')")] string envVars,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "set_container_env");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID or name is required.";
        if (string.IsNullOrWhiteSpace(envVars))
            return "Environment variables are required.";

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var (container, resolveError) = McpInputValidation.Resolve(containers, containerId);
        if (container is null) return resolveError ?? $"Container not found: {containerId}";

        var sid = serverId ?? "local";
        // The plan (R1-07): the merged .env for the container's project and every container `compose up` then
        // touches. The gateway decided on the same plan (the target's related containers); a container that turns
        // up only now was not decided and stops the change before anything is written.
        ComposePlan plan;
        try
        {
            plan = await planner.PlanEnvChangeAsync(container, sid, existing => DotEnvMerge.Merge(existing, envVars).Content);
            plan.EnsureDecided(MutationScope.Current);
        }
        catch (ComposePlanException ex)
        {
            return MutationScope.Fail($"Cannot change the .env of '{container.Name}': {ex.Message}");
        }
        var changed = DotEnvMerge.Merge(null, envVars).Changed;
        var workingDir = plan.ProjectDirectory;

        // Write the planned .env and bring the project up with the planned files
        var restartResult = await executor.ExecuteAsync(sid, plan.DeployScript(pull: false), TimeSpan.FromMinutes(2));

        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        if (!restartResult.Success)
        {
            await auditLog.LogAsync(actor, actorType, "env.save", "env", container.Id, container.Name, details: $"{changed.Count} var(s): {string.Join(", ", changed)}", serverId: serverId, success: false);
            return MutationScope.Fail($"Writing the .env or the restart failed: {restartResult.Output}\n{restartResult.Error}");
        }

        await auditLog.LogAsync(actor, actorType, "env.save", "env", container.Id, container.Name, details: $"{changed.Count} var(s): {string.Join(", ", changed)}", serverId: serverId);
        return $"Updated {changed.Count} variable(s) in {workingDir}/.env and restarted:\n  {string.Join(", ", changed)}\n\n{restartResult.Output}";
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Deploy an application using a docker-compose.yml content string. Creates and starts all services defined in the compose file.")]
    public static async Task<string> DeployCompose(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IHostCommandExecutor executor,
        IComposePlanner planner,
        IToolAuditLog auditLog,
        [Description("Server ID to deploy on")] string serverId,
        [Description("Name/directory for the deployment (e.g. 'my-app')")] string projectName,
        [Description("Full docker-compose.yml content")] string composeContent)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "deploy_compose");
        if (denied != null) return MutationScope.Fail(denied);
        if (!McpInputValidation.IsSafeProjectName(projectName))
            return MutationScope.Fail("Error: Invalid project name.");

        if (string.IsNullOrWhiteSpace(composeContent))
            return MutationScope.Fail("Error: Compose content is required.");

        // The plan (R1-07): the project compose will use and every existing container it recreates or collides
        // with. The gateway decided on the same plan; a container that turns up only now stops the deployment
        // before anything is written. The command then uses exactly the planned project, directory and file.
        ComposePlan plan;
        try
        {
            plan = await planner.PlanAsync(ComposeDeployment.FromContent(serverId,
                ComposeDeployment.DeploymentDirectory(projectName), composeContent));
            plan.EnsureDecided(MutationScope.Current);
        }
        catch (ComposePlanException ex)
        {
            return MutationScope.Fail($"Error: {ex.Message}");
        }

        var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
        var deployResult = await executor.ExecuteAsync(serverId, plan.DeployScript(pull: true), TimeSpan.FromMinutes(5));

        await auditLog.LogAsync(actor, actorType, "deploy.compose", "container", projectName, projectName, serverId: serverId, success: deployResult.Success);
        if (deployResult.Success)
            return $"Compose deployment '{projectName}' succeeded:\n{deployResult.Output}";
        MutationScope.Fail("Compose deployment failed.");
        return $"Compose deployment failed:\n{deployResult.Output}\n{deployResult.Error}";
    }
}

/// <summary>Merges <c>KEY=VALUE</c> pairs (comma or newline separated) into an existing <c>.env</c>: changed keys are
/// replaced, others kept. Shared by <c>set_container_env</c> and the gateway's plan of it (R1-07), so both see the
/// same new file.</summary>
public static class DotEnvMerge
{
    public static (string Content, IReadOnlyList<string> Changed) Merge(string? existingContent, string envVars)
    {
        var existing = new Dictionary<string, string>();
        foreach (var line in (existingContent ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx > 0)
                existing[trimmed[..eqIdx]] = trimmed[(eqIdx + 1)..];
        }
        var changed = new List<string>();
        foreach (var pair in (envVars ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = pair[..eqIdx].Trim();
                existing[key] = pair[(eqIdx + 1)..].Trim();
                changed.Add(key);
            }
        }
        return (string.Join('\n', existing.Select(e => $"{e.Key}={e.Value}")) + "\n", changed);
    }
}
