using ModelContextProtocol.Server;
using System.ComponentModel;
using ServerWatch.Services.Docker;
using ServerWatch.Services.ImageUpdate;
using ServerWatch.Services.Server;
using ServerWatch.Models;
using ServerWatch.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace ServerWatch.Mcp.Tools;

[McpServerToolType]
public class ContainerTools
{
    [McpServerTool, Description("List all Docker containers across all configured servers. Returns container name, image, state, health, server, and compose project.")]
    public static async Task<string> ListContainers(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
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

    [McpServerTool, Description("Get detailed information about a specific Docker container including its configuration, ports, labels, and stats.")]
    public static async Task<string> GetContainerDetails(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("The full container ID")] string containerId,
        [Description("Server ID where the container runs")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_container_details");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID is required.";

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        if (container == null) return $"Container not found: {containerId}";

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

    [McpServerTool, Description("Get logs from a Docker container.")]
    public static async Task<string> GetContainerLogs(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
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
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        var id = container?.Id ?? containerId;
        var logs = await docker.GetContainerLogsAsync(id, lines, serverId);
        return $"Logs for {container?.Name ?? containerId} (last {lines} lines):\n{logs}";
    }

    [McpServerTool, Description("Start a stopped Docker container.")]
    public static async Task<string> StartContainer(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "start_container");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID or name is required.";

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        var id = container?.Id ?? containerId;
        await docker.StartContainerAsync(id, serverId);
        return $"Container {container?.Name ?? containerId} started.";
    }

    [McpServerTool, Description("Stop a running Docker container.")]
    public static async Task<string> StopContainer(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "stop_container");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID or name is required.";

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        var id = container?.Id ?? containerId;
        await docker.StopContainerAsync(id, serverId);
        return $"Container {container?.Name ?? containerId} stopped.";
    }

    [McpServerTool, Description("Restart a Docker container.")]
    public static async Task<string> RestartContainer(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "restart_container");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID or name is required.";

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        var id = container?.Id ?? containerId;
        await docker.RestartContainerAsync(id, serverId);
        return $"Container {container?.Name ?? containerId} restarted.";
    }

    [McpServerTool, Description("Pull latest image and recreate a Docker container (update).")]
    public static async Task<string> UpdateContainer(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        ImageUpdateStore updateStore,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "update_container");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID or name is required.";

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        var id = container?.Id ?? containerId;

        var messages = new List<string>();
        var progress = new Progress<string>(msg => messages.Add(msg));
        var newId = await docker.RecreateContainerAsync(id, serverId, progress);
        updateStore.Remove(id, serverId ?? "local");
        return $"Container updated:\n{string.Join('\n', messages)}\nNew ID: {newId[..12]}";
    }

    [McpServerTool, Description("Check which containers have image updates available.")]
    public static async Task<string> GetUpdateStatus(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        ImageUpdateStore updateStore)
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

    [McpServerTool, Description("Deploy a new application on a server using a standardized template. Supports common app types like web apps, databases, and custom Docker images. Creates the container with sensible defaults and starts it.")]
    public static async Task<string> DeployApp(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
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
        if (denied != null) return denied;
        // Validate app name
        if (string.IsNullOrWhiteSpace(appName) || !System.Text.RegularExpressions.Regex.IsMatch(appName, @"^[a-zA-Z0-9._-]+$"))
            return "Error: Invalid app name. Use only letters, numbers, dots, hyphens, and underscores.";

        if (string.IsNullOrWhiteSpace(image))
            return "Error: Image is required.";

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

        try
        {
            // Pull image first
            await docker.PullImageAsync(image, null, serverId);

            // Create and start container
            var containerId = await docker.CreateContainerAsync(request, serverId);
            return $"App '{appName}' deployed successfully!\n" +
                   $"  Container ID: {containerId[..12]}\n" +
                   $"  Image: {image}\n" +
                   (request.PortMappings.Any() ? $"  Ports: {string.Join(", ", request.PortMappings.Select(p => $"{p.Key}→{p.Value}"))}\n" : "") +
                   $"  Restart: {restartPolicy}\n" +
                   $"  Server: {serverId ?? "local"}";
        }
        catch (Exception ex)
        {
            return $"Deployment failed: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get environment variables of a running Docker container. Sensitive values (keys, secrets, passwords, tokens) are masked for security.")]
    public static async Task<string> GetContainerEnv(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        [Description("Container ID or name")] string containerId,
        [Description("Server ID")] string? serverId = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_container_env");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(containerId))
            return "Container ID or name is required.";

        var containers = await docker.ListContainersAsync(all: true, serverId: serverId);
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        var id = container?.Id ?? containerId;

        var envVars = await docker.GetContainerEnvAsync(id, serverId);

        string[] sensitiveKeywords = ["KEY", "SECRET", "PASSWORD", "TOKEN", "CREDENTIAL", "AUTH", "PRIVATE", "PASSPHRASE"];
        bool IsSensitive(string key) => sensitiveKeywords.Any(kw => key.Contains(kw, StringComparison.OrdinalIgnoreCase));

        var lines = envVars.Select(e => IsSensitive(e.Key)
            ? $"  {e.Key}=••••••••"
            : $"  {e.Key}={e.Value}");

        return $"Environment variables for {container?.Name ?? containerId}:\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Set environment variables in a container's .env file and restart via docker compose. Only works for containers managed by docker-compose. Provide variables as 'KEY=VALUE' pairs separated by newlines or commas. Existing variables not included are kept unchanged.")]
    public static async Task<string> SetContainerEnv(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IDockerService docker,
        IHostCommandExecutor executor,
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
        var container = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId || c.Id.StartsWith(containerId));
        if (container == null) return $"Container not found: {containerId}";

        // Find compose working dir
        string? workingDir = null;
        if (container.Labels.TryGetValue("com.docker.compose.project.working_dir", out var wd))
            workingDir = wd;
        else if (container.ComposeProject != "Standalone")
        {
            var sid = serverId ?? "local";
            var check = await executor.ExecuteAsync(sid,
                $"test -f /opt/deployments/{container.ComposeProject}/docker-compose.yml && echo /opt/deployments/{container.ComposeProject}",
                TimeSpan.FromSeconds(5));
            if (check.Success && !string.IsNullOrWhiteSpace(check.Output))
                workingDir = check.Output.Trim();
        }

        if (workingDir == null)
            return $"Container '{container.Name}' is not part of a docker-compose project. Cannot edit .env file.";

        var sid2 = serverId ?? "local";

        // Load existing .env
        var existing = new Dictionary<string, string>();
        var readResult = await executor.ExecuteAsync(sid2, $"cat {workingDir}/.env 2>/dev/null", TimeSpan.FromSeconds(5));
        if (readResult.Success && !string.IsNullOrWhiteSpace(readResult.Output))
        {
            foreach (var line in readResult.Output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx > 0)
                    existing[trimmed[..eqIdx]] = trimmed[(eqIdx + 1)..];
            }
        }

        // Parse and merge new vars
        var changed = new List<string>();
        foreach (var pair in envVars.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = pair[..eqIdx].Trim();
                var val = pair[(eqIdx + 1)..].Trim();
                existing[key] = val;
                changed.Add(key);
            }
        }

        // Write merged .env
        var content = string.Join('\n', existing.Select(e => $"{e.Key}={e.Value}")) + "\n";
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        var writeResult = await executor.ExecuteAsync(sid2,
            $"echo '{b64}' | base64 -d > {workingDir}/.env",
            TimeSpan.FromSeconds(10));

        if (!writeResult.Success)
            return $"Failed to write .env file: {writeResult.Error}";

        // Restart via docker compose
        var restartResult = await executor.ExecuteAsync(sid2,
            $"cd {workingDir} && docker compose up -d 2>&1",
            TimeSpan.FromMinutes(2));

        if (!restartResult.Success)
            return $"Env vars written but restart failed: {restartResult.Output}\n{restartResult.Error}";

        return $"Updated {changed.Count} variable(s) in {workingDir}/.env and restarted:\n  {string.Join(", ", changed)}\n\n{restartResult.Output}";
    }

    [McpServerTool, Description("Deploy an application using a docker-compose.yml content string. Creates and starts all services defined in the compose file.")]
    public static async Task<string> DeployCompose(
        IHttpContextAccessor httpContextAccessor,
        McpPermissionService permissionService,
        IHostCommandExecutor executor,
        [Description("Server ID to deploy on")] string serverId,
        [Description("Name/directory for the deployment (e.g. 'my-app')")] string projectName,
        [Description("Full docker-compose.yml content")] string composeContent)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "deploy_compose");
        if (denied != null) return denied;
        if (string.IsNullOrWhiteSpace(projectName) || !System.Text.RegularExpressions.Regex.IsMatch(projectName, @"^[a-zA-Z0-9._-]+$"))
            return "Error: Invalid project name.";

        if (string.IsNullOrWhiteSpace(composeContent))
            return "Error: Compose content is required.";

        // Create directory and write compose file
        var dir = $"/opt/deployments/{projectName}";
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(composeContent));

        var setupResult = await executor.ExecuteAsync(serverId,
            $"mkdir -p {dir} && echo '{b64}' | base64 -d > {dir}/docker-compose.yml",
            TimeSpan.FromSeconds(10));

        if (!setupResult.Success)
            return $"Failed to write compose file: {setupResult.Error}";

        // Run docker compose up
        var deployResult = await executor.ExecuteAsync(serverId,
            $"cd {dir} && docker compose pull 2>&1 && docker compose up -d 2>&1",
            TimeSpan.FromMinutes(5));

        return deployResult.Success
            ? $"Compose deployment '{projectName}' succeeded:\n{deployResult.Output}"
            : $"Compose deployment failed:\n{deployResult.Output}\n{deployResult.Error}";
    }
}
