using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Webhooks;
using Microsoft.AspNetCore.Http;

namespace Whiskers.Mcp.Tools;

/// <summary>The Git-deploy apps over MCP. Listing and reading them (Plan-0013 WP4), and setting one up for Brokkr
/// (contract 0001): the app, its repository token and its GitHub webhook. Triggering a deploy or a rollback stays out
/// of reach — those are the write tools GAP-3 designs, together with the health check and the automatic rollback that
/// make them safe to hand over. The setup tools run through the Mutation Gateway (the filter decides them by their
/// capability), answer in JSON for a program to read, never return the token, and are not offered to the in-app
/// agent (<c>AgentToolRegistry.NonAgentTools</c>).</summary>
[McpServerToolType]
public class GitDeployTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List the applications Whiskers deploys from Git: repository, branch, compose path, target server, and the outcome, time and commit of the last deploy. Read-only — this cannot start a deploy.")]
    public static async Task<string> ListGitDeployApps(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IGitDeployService gitDeploy)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_git_deploy_apps");
        if (denied != null) return denied;

        var apps = await gitDeploy.GetAppsAsync();
        if (apps.Count == 0) return "No Git-deploy applications are configured.";

        // Never the token itself — the model only carries whether one is stored, and that is all that is useful.
        var lines = apps.Select(a =>
        {
            var last = a.LastDeployedAt is null
                ? "never deployed"
                : $"last {(a.LastDeploySucceeded == true ? "OK" : a.LastDeploySucceeded == false ? "FAILED" : "unknown")} " +
                  $"at {a.LastDeployedAt:yyyy-MM-dd HH:mm} UTC" +
                  (string.IsNullOrEmpty(a.LastDeployedSha) ? "" : $" ({a.LastDeployedSha[..Math.Min(8, a.LastDeployedSha.Length)]})");

            return $"- {a.Name} [{a.Id}] | {a.RepoUrl} @ {a.Branch} | compose: {a.ComposePath} | " +
                   $"server: {a.ServerId} | credentials: {(a.HasToken ? "stored" : "none")} | {last}";
        });

        return $"Git-deploy applications ({apps.Count}):\n{string.Join('\n', lines)}";
    }

    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("Read one Git-deploy application as JSON: id, revision, name, repository, branch, compose path, server, whether a repository token is stored (never the token), the last deploy (time, result, commit) and the ids of its webhooks.")]
    public static async Task<CallToolResult> GetGitDeployApp(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IGitDeployService gitDeploy,
        IWebhookService webhooks,
        [Description("The application id (from list_git_deploy_apps or create_git_deploy_app)")] string appId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_git_deploy_app");
        if (denied != null) return McpErrorEnvelope.Result(McpErrorEnvelope.Denied, denied);

        var app = await gitDeploy.GetAppAsync(appId);
        if (app is null) return McpErrorEnvelope.Result(McpErrorEnvelope.NotFound, $"Git deployment app not found: {appId}");
        return new CallToolResult { Content = [new TextContentBlock { Text = Json(await ViewAsync(app, webhooks)) }] };
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Create a Git-deploy application (Brokkr provisioning). Whiskers clones the repository over HTTPS on the target server and runs its compose file. A private repository needs a read-only token (optional here, or later with set_git_deploy_token). An existing name is refused, and so is a branch that is not a plain branch name (leading letter or digit, only letters, digits and . _ / -, no '..'). Answers JSON with the new id and revision; never the token.")]
    public static async Task<string> CreateGitDeployApp(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IGitDeployService gitDeploy,
        IWebhookService webhooks,
        [Description("Unique name of the application")] string name,
        [Description("Repository URL, https:// only")] string repoUrl,
        [Description("Server id the application deploys to")] string serverId,
        [Description("Branch to deploy (default main). A plain branch name: starts with a letter or digit, only letters, digits and . _ / -, no '..'; anything else is refused as invalid_argument.")] string? branch = null,
        [Description("Compose file path relative to the repository root (default docker-compose.yml)")] string? composePath = null,
        [Description("Read-only repository token for a private repository (optional; never returned)")] string? token = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "create_git_deploy_app");
        if (denied != null) return Failed("denied", denied);
        // A person owns the app (R1-08, contract 0001): the one who approved this call, or the calling person. A push
        // later deploys it on that authority, re-checked then; a key could never pass that check unattended.
        var owner = AutomationOwners.PersonFor(MutationScope.Current);
        if (owner is null)
            return Failed("denied", NoPerson);
        if (string.IsNullOrWhiteSpace(name))
            return Failed("invalid_argument", "Name is required.");
        if ((await gitDeploy.GetAppsAsync()).Any(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Failed("conflict", $"A Git deployment app named '{name.Trim()}' exists already.");

        try
        {
            var saved = await gitDeploy.SaveAppAsync(new GitDeployApp
            {
                Name = name.Trim(),
                RepoUrl = repoUrl.Trim(),
                Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim(),
                ComposePath = string.IsNullOrWhiteSpace(composePath) ? "docker-compose.yml" : composePath.Trim(),
                ServerId = serverId,
            }, string.IsNullOrWhiteSpace(token) ? null : token, owner);
            MutationScope.Note($"Git deployment app {saved.Id}");

            // Read back from the store: what a later deployment will use.
            var stored = await gitDeploy.GetAppAsync(saved.Id);
            if (stored is null || stored.RepoUrl != saved.RepoUrl || stored.ServerId != saved.ServerId
                || stored.HasToken != !string.IsNullOrWhiteSpace(token))
                return Failed("failed", $"'{saved.Name}' was saved, but reading it back did not show what was saved.");
            return Json(await ViewAsync(stored, webhooks));
        }
        catch (Exception ex) when (ex is ArgumentException or GitDeployConflictException or InvalidOperationException)
        {
            return Failed(ex is GitDeployConflictException ? "conflict" : ex is ArgumentException ? "invalid_argument" : "failed",
                ex.Message);
        }
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Set (or with an empty string remove) the read-only repository token of a Git-deploy application. Needs the app's current revision (from get_git_deploy_app); a stale one is refused. The token is stored in the vault and never returned. Answers JSON with the new revision.")]
    public static async Task<string> SetGitDeployToken(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IGitDeployService gitDeploy,
        IWebhookService webhooks,
        [Description("The application id")] string appId,
        [Description("The app's current revision")] int revision,
        [Description("The repository token; an empty string removes it")] string token)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "set_git_deploy_token");
        if (denied != null) return Failed("denied", denied);
        var owner = AutomationOwners.PersonFor(MutationScope.Current);
        if (owner is null)
            return Failed("denied", NoPerson);

        try
        {
            var saved = await gitDeploy.SetTokenAsync(appId, revision, token ?? "", owner);
            var stored = await gitDeploy.GetAppAsync(saved.Id);
            if (stored is null || stored.HasToken != !string.IsNullOrWhiteSpace(token))
                return Failed("failed", $"The token of '{saved.Name}' was set, but reading the app back did not show it.");
            return Json(await ViewAsync(stored, webhooks));
        }
        catch (KeyNotFoundException ex)
        {
            return Failed("not_found", ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or GitDeployConflictException or InvalidOperationException)
        {
            return Failed(ex is GitDeployConflictException ? "conflict" : ex is ArgumentException ? "invalid_argument" : "failed",
                ex.Message);
        }
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Create a webhook that deploys a Git-deploy application when its repository is pushed. Whiskers generates the HMAC secret; it is in this answer ONLY — put it into the repository's webhook right away (content type JSON, header X-Hub-Signature-256). Provider: github. Answers JSON with webhookId, url and secret.")]
    public static async Task<string> CreateGitDeployWebhook(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IGitDeployService gitDeploy,
        IWebhookService webhooks,
        IServiceProvider services,
        [Description("The application id")] string appId,
        [Description("Who sends the deliveries: github")] string provider = WebhookSignatures.GitHub)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "create_git_deploy_webhook");
        if (denied != null) return Failed("denied", denied);
        var owner = AutomationOwners.PersonFor(MutationScope.Current);
        if (owner is null)
            return Failed("denied", NoPerson);
        if (!string.Equals(provider, WebhookSignatures.GitHub, StringComparison.Ordinal))
            return Failed("invalid_argument", $"Provider '{provider}' is not supported here; use '{WebhookSignatures.GitHub}'.");
        var app = await gitDeploy.GetAppAsync(appId);
        if (app is null) return Failed("not_found", $"Git deployment app not found: {appId}");
        // The webhook binds to the app the gateway decided on (R1-06): one moved to another server or renamed since
        // the decision (or the approval) is refused instead of bound under a name or server nobody approved.
        var appServer = (services.GetService(typeof(Whiskers.Services.ServerConfig.IServerConfigService))
            as Whiskers.Services.ServerConfig.IServerConfigService)?.GetServer(app.ServerId)?.Id ?? app.ServerId;
        if (MutationScope.Current?.Covers(appServer, app.Name) != true)
            return Failed("conflict", $"'{app.Name}' was changed after the call was decided; reload and try again.");

        try
        {
            var created = await webhooks.CreateWebhookAsync(new WebhookEntity
            {
                Name = app.Name,
                Action = "git-deploy",
                TargetType = "container",
                TargetId = app.Id,
                ServerId = app.ServerId,
                Owner = owner,
                Provider = WebhookSignatures.GitHub,
            });
            // The id is part of the gateway's event; the secret never is, and no log line may print it either.
            Whiskers.Utils.KnownSecrets.Register(created.Secret);
            MutationScope.Note($"Webhook {created.WebhookId}");

            var stored = await webhooks.GetWebhookAsync(created.WebhookId);
            if (stored is null || stored.Action != "git-deploy" || stored.TargetId != app.Id)
                return Failed("failed", "The webhook was created, but reading it back did not show it.");
            var publicUrl = services.GetService(typeof(WhiskersPublicUrl)) as WhiskersPublicUrl ?? WhiskersPublicUrl.None;
            return Json(new
            {
                webhookId = created.WebhookId,
                url = WebhookUrl(publicUrl, created.WebhookId),
                urlIsAbsolute = publicUrl.Base is not null,
                provider = created.Provider,
                secret = created.Secret,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Failed("failed", ex.Message);
        }
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Replace the HMAC secret of a Git-deploy webhook with a new one Whiskers generates. The new secret is in this answer ONLY; the old one stops working at once — update the repository's webhook right away. Answers JSON with webhookId and secret.")]
    public static async Task<string> RotateGitDeployWebhookSecret(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IWebhookService webhooks,
        [Description("The webhook id")] string webhookId)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "rotate_git_deploy_webhook_secret");
        if (denied != null) return Failed("denied", denied);
        var webhook = await webhooks.GetWebhookAsync(webhookId);
        if (webhook is null) return Failed("not_found", $"Webhook not found: {webhookId}");
        // Only the webhooks this tool family manages: a container or compose webhook is changed in Whiskers itself.
        if (webhook.Action != "git-deploy")
            return Failed("invalid_argument", "Only a Git-deploy webhook's secret can be rotated here.");

        string secret;
        try
        {
            secret = await webhooks.RegenerateSecretAsync(webhook.WebhookId);
        }
        catch (InvalidOperationException ex)
        {
            // Deleted between the check and the write: that is a missing webhook, not a failure of the rotation.
            return await webhooks.GetWebhookAsync(webhook.WebhookId) is null
                ? Failed("not_found", $"Webhook not found: {webhookId}")
                : Failed("failed", ex.Message);
        }
        Whiskers.Utils.KnownSecrets.Register(secret);
        MutationScope.Note($"Webhook {webhook.WebhookId}: secret rotated");

        // Read back: the webhook is still there and still a Git deployment's (the secret itself is not compared).
        var stored = await webhooks.GetWebhookAsync(webhook.WebhookId);
        if (stored is null || stored.Action != "git-deploy" || stored.TargetId != webhook.TargetId)
            return Failed("failed", "The secret was rotated, but reading the webhook back did not show it.");
        return Json(new { webhookId = webhook.WebhookId, secret });
    }

    /// <summary>What the read and setup tools answer about an app: never the token, only whether one is stored.</summary>
    private static async Task<object> ViewAsync(GitDeployApp app, IWebhookService webhooks)
    {
        var hooks = (await webhooks.GetWebhooksAsync())
            .Where(w => w.Action == "git-deploy" && w.TargetId == app.Id)
            .Select(w => w.WebhookId)
            .ToList();
        return new
        {
            id = app.Id,
            revision = app.Revision,
            name = app.Name,
            repoUrl = app.RepoUrl,
            branch = app.Branch,
            composePath = app.ComposePath,
            serverId = app.ServerId,
            credentials = app.HasToken ? "stored" : "none",
            lastDeploy = app.LastDeployedAt is null
                ? null
                : new
                {
                    at = app.LastDeployedAt,
                    result = app.LastDeploySucceeded switch { true => "ok", false => "failed", _ => "unknown" },
                    sha = app.LastDeployedSha,
                },
            webhooks = hooks,
        };
    }

    /// <summary>The delivery address: absolute when Whiskers knows its public URL (<c>Whiskers:PublicUrl</c>), else
    /// the path for the caller to put in front of its own Whiskers address.</summary>
    internal static string WebhookUrl(WhiskersPublicUrl publicUrl, string webhookId) =>
        publicUrl.Link($"api/webhooks/{webhookId}") ?? $"/api/webhooks/{webhookId}";

    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web);

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOut);

    private static string Error(string code, string message) => McpErrorEnvelope.Json(code, message);

    private const string NoPerson =
        "A person must own what this sets up, and none stands behind this call: let a person approve it in Whiskers "
        + "(Approvals) — the policy must ask for an approval for this tool — or call it as a signed-in person.";

    /// <summary>A refused or failed setup call: the operation fails (R1-20/37) and the caller reads the reason code.</summary>
    private static string Failed(string code, string message) => MutationScope.Fail(Error(code, message));
}
