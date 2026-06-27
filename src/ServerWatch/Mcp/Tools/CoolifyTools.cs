using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using ServerWatch.Models.Coolify;
using ServerWatch.Services.Coolify;
using ServerWatch.Services.Mcp;
using Microsoft.AspNetCore.Http;

namespace ServerWatch.Mcp.Tools;

[McpServerToolType]
public class CoolifyTools
{
    [McpServerTool, Description("List all Coolify applications with their status, Git repository, branch, and domain.")]
    public static async Task<string> ListCoolifyApplications(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_coolify_applications");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";

        var apps = await coolify.ListApplicationsAsync();
        if (apps.Count == 0) return "Keine Coolify-Applikationen gefunden.";

        var lines = apps.Select(a =>
            $"- {a.Name} [{a.DisplayStatus}] | UUID: {a.Uuid} | Repo: {a.GitRepository ?? "(kein)"} | Branch: {a.GitBranch ?? "-"} | Domain: {a.Fqdn ?? "-"}");
        return $"Gefunden: {apps.Count} Applikation(en):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Get detailed information about a specific Coolify application including its Git repo, branch, build pack, status, and domain.")]
    public static async Task<string> GetCoolifyApplication(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config,
        [Description("The application UUID")] string uuid)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_coolify_application");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";
        if (string.IsNullOrWhiteSpace(uuid)) return "UUID ist erforderlich.";

        var app = await coolify.GetApplicationAsync(uuid);
        if (app == null) return $"Applikation nicht gefunden: {uuid}";

        var sb = new StringBuilder();
        sb.AppendLine($"Name: {app.Name}");
        sb.AppendLine($"UUID: {app.Uuid}");
        sb.AppendLine($"Status: {app.DisplayStatus}");
        sb.AppendLine($"Domain: {app.Fqdn ?? "(nicht gesetzt)"}");
        sb.AppendLine($"Git-Repository: {app.GitRepository ?? "(keins)"}");
        sb.AppendLine($"Git-Branch: {app.GitBranch ?? "-"}");
        sb.AppendLine($"Build-Pack: {app.BuildPack ?? "-"}");
        if (app.DockerComposeLocation != null)
            sb.AppendLine($"Docker-Compose: {app.DockerComposeLocation}");
        if (app.DockerfileLocation != null)
            sb.AppendLine($"Dockerfile: {app.DockerfileLocation}");
        if (app.HealthCheckEnabled == true)
            sb.AppendLine($"Health-Check: {app.HealthCheckPath ?? "/"}");
        sb.AppendLine($"Erstellt: {app.CreatedAt}");
        sb.AppendLine($"Aktualisiert: {app.UpdatedAt}");
        return sb.ToString();
    }

    [McpServerTool, Description("Trigger a deployment for a Coolify application. Optionally force a rebuild without cache.")]
    public static async Task<string> DeployCoolifyApplication(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config,
        [Description("The application UUID")] string uuid,
        [Description("Force rebuild without cache (default: false)")] bool force = false)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "deploy_coolify_application");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";
        if (string.IsNullOrWhiteSpace(uuid)) return "UUID ist erforderlich.";

        var result = await coolify.DeployApplicationAsync(uuid, force);
        return $"Deployment gestartet: {result.Message}\nDeployment-UUID: {result.DeploymentUuid ?? "-"}";
    }

    [McpServerTool, Description("Start a Coolify application.")]
    public static async Task<string> StartCoolifyApplication(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config,
        [Description("The application UUID")] string uuid)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "start_coolify_application");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";
        if (string.IsNullOrWhiteSpace(uuid)) return "UUID ist erforderlich.";

        await coolify.StartApplicationAsync(uuid);
        return $"Applikation {uuid} wird gestartet.";
    }

    [McpServerTool, Description("Stop a Coolify application.")]
    public static async Task<string> StopCoolifyApplication(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config,
        [Description("The application UUID")] string uuid)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "stop_coolify_application");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";
        if (string.IsNullOrWhiteSpace(uuid)) return "UUID ist erforderlich.";

        await coolify.StopApplicationAsync(uuid);
        return $"Applikation {uuid} wird gestoppt.";
    }

    [McpServerTool, Description("Restart a Coolify application.")]
    public static async Task<string> RestartCoolifyApplication(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config,
        [Description("The application UUID")] string uuid)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "restart_coolify_application");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";
        if (string.IsNullOrWhiteSpace(uuid)) return "UUID ist erforderlich.";

        await coolify.RestartApplicationAsync(uuid);
        return $"Applikation {uuid} wird neugestartet.";
    }

    [McpServerTool, Description("Get logs from a Coolify application.")]
    public static async Task<string> GetCoolifyApplicationLogs(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config,
        [Description("The application UUID")] string uuid)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_coolify_application_logs");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";
        if (string.IsNullOrWhiteSpace(uuid)) return "UUID ist erforderlich.";

        var logs = await coolify.GetApplicationLogsAsync(uuid);
        return string.IsNullOrWhiteSpace(logs) ? "Keine Logs verfügbar." : logs;
    }

    [McpServerTool, Description("List all Coolify servers with their status, IP, and Docker version.")]
    public static async Task<string> ListCoolifyServers(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_coolify_servers");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";

        var servers = await coolify.ListServersAsync();
        if (servers.Count == 0) return "Keine Coolify-Server gefunden.";

        var lines = servers.Select(s =>
            $"- {s.Name} [{(s.IsReachable ? "erreichbar" : "nicht erreichbar")}] | UUID: {s.Uuid} | IP: {s.Ip} | Docker: {s.Settings?.DockerVersion ?? "-"}");
        return $"Gefunden: {servers.Count} Server:\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("List all Coolify databases with their type and status.")]
    public static async Task<string> ListCoolifyDatabases(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_coolify_databases");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";

        var dbs = await coolify.ListDatabasesAsync();
        if (dbs.Count == 0) return "Keine Coolify-Datenbanken gefunden.";

        var lines = dbs.Select(d =>
            $"- {d.Name} [{d.Status}] | UUID: {d.Uuid} | Typ: {d.Type} | Image: {d.Image ?? "-"}");
        return $"Gefunden: {dbs.Count} Datenbank(en):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Deploy all Coolify applications matching a specific tag. Supports comma-separated tags.")]
    public static async Task<string> DeployCoolifyByTag(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config,
        [Description("Tag name (supports comma-separated)")] string tag,
        [Description("Force rebuild without cache (default: false)")] bool force = false)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "deploy_coolify_by_tag");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";
        if (string.IsNullOrWhiteSpace(tag)) return "Tag ist erforderlich.";

        var results = await coolify.DeployByTagAsync(tag, force);
        if (results.Count == 0) return $"Keine Ressourcen mit Tag '{tag}' gefunden.";

        var lines = results.Select(r =>
            $"- {r.ResourceUuid}: {r.Message} (Deployment: {r.DeploymentUuid ?? "-"})");
        return $"Batch-Deploy gestartet für Tag '{tag}' ({results.Count} Ressource(n)):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Get environment variables for a Coolify application.")]
    public static async Task<string> GetCoolifyEnvVars(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config,
        [Description("The application UUID")] string appUuid)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_coolify_env_vars");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";
        if (string.IsNullOrWhiteSpace(appUuid)) return "App-UUID ist erforderlich.";

        var vars = await coolify.GetEnvVarsAsync(appUuid);
        if (vars.Count == 0) return "Keine Umgebungsvariablen gefunden.";

        var lines = vars.Select(v =>
            $"- {v.Key}={v.Value}{(v.IsBuildTime ? " [build-time]" : "")}");
        return $"Gefunden: {vars.Count} Variable(n):\n{string.Join('\n', lines)}";
    }

    [McpServerTool, Description("Set an environment variable for a Coolify application.")]
    public static async Task<string> SetCoolifyEnvVar(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        ICoolifyService coolify,
        ICoolifyConfigService config,
        [Description("The application UUID")] string appUuid,
        [Description("Variable name (key)")] string key,
        [Description("Variable value")] string value,
        [Description("Is this a build-time variable? (default: false)")] bool isBuildTime = false)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "set_coolify_env_var");
        if (denied != null) return denied;
        if (!config.IsEnabled) return "Coolify-Integration ist nicht aktiviert.";
        if (string.IsNullOrWhiteSpace(appUuid)) return "App-UUID ist erforderlich.";
        if (string.IsNullOrWhiteSpace(key)) return "Variablenname ist erforderlich.";

        await coolify.SetEnvVarAsync(appUuid, key, value, isBuildTime);
        return $"Umgebungsvariable '{key}' für {appUuid} gesetzt.{(isBuildTime ? " (Build-Time)" : "")}";
    }
}
