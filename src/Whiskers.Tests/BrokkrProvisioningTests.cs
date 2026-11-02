using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Whiskers.Mcp;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Webhooks;
using Microsoft.AspNetCore.Http;

namespace Whiskers.Tests;

/// <summary>Contract 0001 (Brokkr provisioning): the app, its repository token and its GitHub webhook are set up over
/// MCP through the real filter and Mutation Gateway — owned by the calling key, refused on conflicts and stale
/// revisions, waiting for a person's approval when policy asks for one — and neither the token nor the webhook secret
/// reaches an answer it should not, the audit or the approval card. The tools are never offered to the in-app agent.</summary>
public sealed class BrokkrProvisioningTests : IDisposable
{
    /// <summary>A placeholder generated per run, so a leak is recognisable without any real credential in the file.</summary>
    private static readonly string RepoToken = "placeholder-" + Guid.NewGuid().ToString("N");

    private readonly McpFilterHarness _mcp = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brokkr-provisioning-{Guid.NewGuid():N}");
    private readonly GitDeployService _git;
    private readonly MemoryWebhooks _webhooks = new();
    private readonly IMcpPermissionService _permissions;
    private readonly ReadBackVerificationTests.Scripted<IMcpPermissionService> _permissionScript;
    private readonly HttpContextAccessor _http;

    public BrokkrProvisioningTests()
    {
        Directory.CreateDirectory(_root);
        var host = new ProtectedResourceResolutionTests.FakeHost();
        _git = new GitDeployService(host, TestVault.Usable, new ComposePlanner(new FakeDocker(), host),
            NullLogger<GitDeployService>.Instance, Path.Combine(_root, "apps.json"));
        (_permissions, _permissionScript) = ReadBackVerificationTests.Scripted<IMcpPermissionService>.Create();
        _permissionScript.On[nameof(IMcpPermissionService.IsToolAllowed)] = _ => true;
        _http = new HttpContextAccessor();
        // The gateway resolves targets as in production (R1-06): the app, its server, a stale revision.
        _mcp.Targets = new MutationTargetResolver(new ServiceCollection()
            .AddSingleton<IGitDeployService>(_git)
            .AddSingleton<IServerConfigService>(_servers)
            .AddSingleton(_webhooks.Service)
            .BuildServiceProvider());
        NewRequest();
    }

    private readonly FakeServerConfig _servers = new(
        new ServerConfig { Id = "edge", Name = "Edge", IsDefault = true },
        new ServerConfig { Id = "local", Name = "Local" });

    public void Dispose()
    {
        _mcp.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- Catalog and agent -------------------------------------------------

    [Theory]
    [InlineData("create_git_deploy_app", "gitdeploy.save")]
    [InlineData("set_git_deploy_token", "gitdeploy.set_token")]
    [InlineData("create_git_deploy_webhook", "webhook.save")]
    [InlineData("rotate_git_deploy_webhook_secret", "webhook.rotate_secret")]
    public void Each_setup_tool_is_its_capability_at_admin(string tool, string capability)
    {
        var found = new CapabilityCatalog().FindByMcpTool(tool);

        Assert.NotNull(found);
        Assert.Equal(capability, found.Id);
        Assert.Equal(McpPermissionLevels.Admin, found.RequiredLevel);
        Assert.Equal(McpPermissionLevels.Admin, McpPermissionLevels.DefaultToolLevels[tool]);
    }

    [Fact]
    public void Reading_an_app_is_a_read_tool_outside_the_catalog()
    {
        Assert.Null(new CapabilityCatalog().FindByMcpTool("get_git_deploy_app"));
        Assert.Equal(McpPermissionLevels.Read, McpPermissionLevels.DefaultToolLevels["get_git_deploy_app"]);
    }

    [Fact]
    public void The_in_app_agent_is_offered_none_of_the_setup_tools()
    {
        var tools = new AgentToolRegistry([typeof(GitDeployTools)]).Tools.Keys.ToHashSet();

        Assert.Contains("list_git_deploy_apps", tools);
        Assert.Contains("get_git_deploy_app", tools);
        Assert.DoesNotContain("create_git_deploy_app", tools);
        Assert.DoesNotContain("set_git_deploy_token", tools);
        Assert.DoesNotContain("create_git_deploy_webhook", tools);
        Assert.DoesNotContain("rotate_git_deploy_webhook_secret", tools);
    }

    // ---- App ---------------------------------------------------------------

    [Fact]
    public async Task An_app_is_created_through_the_gateway_owned_by_the_key_without_returning_its_token()
    {
        var (answer, outcome) = await CreateAppAsync("shop", RepoToken);

        Assert.Equal(McpCallResult.Executed, outcome.Result);
        Assert.DoesNotContain(RepoToken, answer);
        using var json = JsonDocument.Parse(answer);
        Assert.Equal("stored", json.RootElement.GetProperty("credentials").GetString());
        Assert.Equal("local", json.RootElement.GetProperty("serverId").GetString());
        var app = Assert.Single(await _git.GetAppsAsync());
        Assert.Equal(app.Id, json.RootElement.GetProperty("id").GetString());
        Assert.StartsWith("key:", app.Owner);
        Assert.True(app.HasToken);
        await AssertNotInAuditAsync(RepoToken);
    }

    [Fact]
    public async Task An_existing_name_is_refused_as_a_conflict()
    {
        await CreateAppAsync("shop", null);
        NewRequest();

        var (answer, outcome) = await CreateAppAsync("Shop", null);

        Assert.Equal(McpCallResult.Failed, outcome.Result);
        Assert.Equal("conflict", Code(answer));
        Assert.Single(await _git.GetAppsAsync());
    }

    [Fact]
    public async Task A_plain_http_repository_is_an_invalid_argument()
    {
        var (answer, outcome) = await CallAsync("create_git_deploy_app",
            new { name = "shop", repoUrl = "http://example.org/shop.git", serverId = "local" },
            () => GitDeployTools.CreateGitDeployApp(_http, _permissions, _git, _webhooks.Service,
                "shop", "http://example.org/shop.git", "local"));

        Assert.Equal(McpCallResult.Failed, outcome.Result);
        Assert.Equal("invalid_argument", Code(answer));
        Assert.Empty(await _git.GetAppsAsync());
    }

    [Fact]
    public async Task A_key_without_the_tool_is_refused_and_nothing_is_created()
    {
        _permissionScript.On[nameof(IMcpPermissionService.IsToolAllowed)] = _ => false;

        var (answer, outcome) = await CreateAppAsync("shop", null);

        Assert.Equal(McpCallResult.Failed, outcome.Result);
        Assert.Equal("denied", Code(answer));
        Assert.Empty(await _git.GetAppsAsync());
    }

    // ---- Token -------------------------------------------------------------

    [Fact]
    public async Task A_token_is_set_on_the_current_revision_and_refused_on_a_stale_one()
    {
        var (created, _) = await CreateAppAsync("shop", null);
        var (id, revision) = IdAndRevision(created);

        NewRequest();
        var (stale, staleOutcome) = await SetTokenAsync(id, revision - 1, RepoToken);
        Assert.Equal(McpCallResult.Denied, staleOutcome.Result);
        Assert.Equal("conflict", Code(stale));
        Assert.DoesNotContain(RepoToken, stale);
        Assert.False((await _git.GetAppAsync(id))!.HasToken);

        NewRequest();
        var (answer, outcome) = await SetTokenAsync(id, revision, RepoToken);
        Assert.Equal(McpCallResult.Executed, outcome.Result);
        Assert.DoesNotContain(RepoToken, answer);
        using (var json = JsonDocument.Parse(answer))
        {
            Assert.Equal("stored", json.RootElement.GetProperty("credentials").GetString());
            Assert.Equal(revision + 1, json.RootElement.GetProperty("revision").GetInt32());
        }
        await AssertNotInAuditAsync(RepoToken);

        NewRequest();
        var (removed, _) = await SetTokenAsync(id, revision + 1, "");
        using var cleared = JsonDocument.Parse(removed);
        Assert.Equal("none", cleared.RootElement.GetProperty("credentials").GetString());
    }

    [Fact]
    public async Task A_token_for_a_missing_app_is_refused()
    {
        var (answer, outcome) = await SetTokenAsync("0123456789ab", 1, RepoToken);

        Assert.Equal(McpCallResult.Denied, outcome.Result);
        Assert.Equal("not_found", Code(answer));
        Assert.DoesNotContain(RepoToken, answer);
    }

    // ---- Webhook -----------------------------------------------------------

    [Fact]
    public async Task A_webhook_is_bound_to_the_app_and_its_secret_is_only_in_that_answer()
    {
        var (created, _) = await CreateAppAsync("shop", null);
        var (id, _) = IdAndRevision(created);

        NewRequest();
        var (answer, outcome) = await CreateWebhookAsync(id);

        Assert.Equal(McpCallResult.Executed, outcome.Result);
        using var json = JsonDocument.Parse(answer);
        var webhookId = json.RootElement.GetProperty("webhookId").GetString()!;
        var secret = json.RootElement.GetProperty("secret").GetString()!;
        Assert.False(string.IsNullOrEmpty(secret));
        Assert.Equal($"/api/webhooks/{webhookId}", json.RootElement.GetProperty("url").GetString());
        Assert.False(json.RootElement.GetProperty("urlIsAbsolute").GetBoolean());

        var stored = Assert.Single(_webhooks.All);
        Assert.Equal("git-deploy", stored.Action);
        Assert.Equal(id, stored.TargetId);
        Assert.Equal("local", stored.ServerId);
        Assert.StartsWith("key:", stored.Owner);
        Assert.Equal(WebhookSignatures.GitHub, stored.Provider);

        // Reading the app names its webhook, never the secret.
        NewRequest();
        var read = Text(await GitDeployTools.GetGitDeployApp(_http, _permissions, _git, _webhooks.Service, id));
        Assert.Contains(webhookId, read);
        Assert.DoesNotContain(secret, read);
        await AssertNotInAuditAsync(secret);
    }

    [Fact]
    public async Task Only_github_is_accepted_as_provider()
    {
        var (created, _) = await CreateAppAsync("shop", null);
        var (id, _) = IdAndRevision(created);

        NewRequest();
        var (answer, outcome) = await CallAsync("create_git_deploy_webhook", new { appId = id, provider = "gitlab" },
            () => GitDeployTools.CreateGitDeployWebhook(_http, _permissions, _git, _webhooks.Service, Services(), id, "gitlab"));

        Assert.Equal(McpCallResult.Failed, outcome.Result);
        Assert.Equal("invalid_argument", Code(answer));
        Assert.Empty(_webhooks.All);
    }

    [Fact]
    public async Task Rotating_gives_a_new_secret_only_for_a_git_deploy_webhook()
    {
        var (created, _) = await CreateAppAsync("shop", null);
        var (id, _) = IdAndRevision(created);
        NewRequest();
        var (hook, _) = await CreateWebhookAsync(id);
        string webhookId, first;
        using (var json = JsonDocument.Parse(hook))
        {
            webhookId = json.RootElement.GetProperty("webhookId").GetString()!;
            first = json.RootElement.GetProperty("secret").GetString()!;
        }

        NewRequest();
        var (answer, outcome) = await RotateAsync(webhookId);

        Assert.Equal(McpCallResult.Executed, outcome.Result);
        using (var json = JsonDocument.Parse(answer))
        {
            var second = json.RootElement.GetProperty("secret").GetString()!;
            Assert.NotEqual(first, second);
            Assert.Equal(second, _webhooks.All.Single().Secret);
            await AssertNotInAuditAsync(second);
        }

        _webhooks.All.Add(new WebhookEntity { WebhookId = "restartweb00000", Name = "web", Action = "restart", TargetId = "web" });
        NewRequest();
        var (refused, refusedOutcome) = await RotateAsync("restartweb00000");
        Assert.Equal(McpCallResult.Failed, refusedOutcome.Result);
        Assert.Equal("invalid_argument", Code(refused));
    }

    [Fact]
    public async Task A_new_webhook_for_an_app_is_decided_on_the_apps_server()
    {
        var (created, _) = await CreateAppAsync("shop", null);
        var (id, _) = IdAndRevision(created);
        var resolver = new MutationTargetResolver(new ServiceCollection()
            .AddSingleton<IGitDeployService>(_git)
            .AddSingleton<IServerConfigService>(new FakeServerConfig(
                new ServerConfig { Id = "edge", Name = "Edge", IsDefault = true },
                new ServerConfig { Id = "local", Name = "Local" }))
            .BuildServiceProvider());
        var capability = new CapabilityCatalog().Find("webhook.save")!;

        var decided = await resolver.ResolveAsync(WebhookRequest(capability, new { appId = id }), capability, default);
        var elsewhere = await resolver.ResolveAsync(WebhookRequest(capability, new { appId = id, serverId = "edge" }),
            capability, default);
        var missing = await resolver.ResolveAsync(WebhookRequest(capability, new { appId = "000000000000" }),
            capability, default);

        Assert.Equal("local", decided.Target!.ServerId);     // the app's server, not the default "edge"
        Assert.Equal("shop", decided.Target.Name);
        Assert.Null(elsewhere.Target);
        Assert.Null(missing.Target);
    }

    // ---- Approval ----------------------------------------------------------

    [Fact]
    public async Task With_approval_required_the_setup_waits_for_a_person_and_then_runs_once()
    {
        _mcp.Policy.RequireConfirmationForWrites = true;

        var (waiting, pending) = await CreateAppAsync("shop", RepoToken);

        Assert.Equal(McpCallResult.PendingApproval, pending.Result);
        Assert.Empty(await _git.GetAppsAsync());
        var approval = Assert.Single(_mcp.Approvals.GetPending());
        Assert.DoesNotContain(RepoToken, approval.ParamsJson ?? "");
        Assert.DoesNotContain(RepoToken, approval.Reason ?? "");
        // A program reads the wait from a code, with the operation the identical repeat continues and the approval.
        string operationId;
        using (var json = JsonDocument.Parse(waiting))
        {
            Assert.Equal("pending_approval", json.RootElement.GetProperty("error").GetString());
            operationId = json.RootElement.GetProperty("operationId").GetString()!;
            Assert.Equal(approval.Id, json.RootElement.GetProperty("approvalId").GetString());
        }
        Assert.Equal(pending.OperationId, operationId);
        Assert.DoesNotContain(RepoToken, waiting);

        var decision = await _mcp.Approvals.DecideAsync(approval.Id, true,
            ApprovalResolver.User("admin@example.com", AppRole.Admin));
        Assert.Equal(ApprovalDecisionResult.Decided, decision);

        NewRequest();
        var (answer, done) = await CreateAppAsync("shop", RepoToken);

        Assert.Equal(McpCallResult.Executed, done.Result);
        Assert.Equal(operationId, done.OperationId);   // one operation from the wait to the run (R1-18)
        Assert.DoesNotContain(RepoToken, answer);
        Assert.Single(await _git.GetAppsAsync());
        await AssertNotInAuditAsync(RepoToken);
    }

    // ---- Review round 1 ----------------------------------------------------

    [Fact]
    public async Task Two_creates_with_one_name_at_once_store_one_app()
    {
        // Past the tool's early check: the service decides under its lock, so the second save is a conflict.
        var saves = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                await _git.SaveAppAsync(new GitDeployApp
                {
                    Name = "shop", RepoUrl = "https://example.org/shop.git", ServerId = "local",
                }, null, "key:k1");
                return true;
            }
            catch (GitDeployConflictException) { return false; }
        })).ToList();

        var results = await Task.WhenAll(saves);

        Assert.Equal(1, results.Count(r => r));
        Assert.Single(await _git.GetAppsAsync());
    }

    [Fact]
    public async Task A_webhook_is_not_bound_to_an_app_that_moved_after_the_decision()
    {
        var (created, _) = await CreateAppAsync("shop", null);
        var (id, _) = IdAndRevision(created);

        NewRequest();
        var (answer, outcome) = await CallAsync("create_git_deploy_webhook", new { appId = id }, async () =>
        {
            // Between the decision (on "local") and the run, the app is moved to another server.
            var app = (await _git.GetAppAsync(id))!;
            app.ServerId = "edge";
            await _git.SaveAppAsync(app, null, "key:other");
            return await GitDeployTools.CreateGitDeployWebhook(_http, _permissions, _git, _webhooks.Service, Services(), id);
        });

        Assert.Equal(McpCallResult.Failed, outcome.Result);
        Assert.Equal("conflict", Code(answer));
        Assert.Empty(_webhooks.All);
    }

    [Fact]
    public async Task A_missing_app_for_a_webhook_is_not_found_before_anything_runs()
    {
        var (answer, outcome) = await CreateWebhookAsync("000000000000");

        Assert.Equal(McpCallResult.Denied, outcome.Result);
        Assert.Equal("not_found", Code(answer));
        Assert.Empty(_webhooks.All);
    }

    [Fact]
    public async Task Reading_a_missing_app_is_a_tool_error_with_its_code()
    {
        var result = await GitDeployTools.GetGitDeployApp(_http, _permissions, _git, _webhooks.Service, "000000000000");

        Assert.True(result.IsError);
        Assert.Equal("not_found", Code(Text(result)));
    }

    [Fact]
    public async Task A_webhook_deleted_while_its_secret_is_rotated_is_not_found()
    {
        var (created, _) = await CreateAppAsync("shop", null);
        var (id, _) = IdAndRevision(created);
        NewRequest();
        var (hook, _) = await CreateWebhookAsync(id);
        string webhookId;
        using (var json = JsonDocument.Parse(hook))
            webhookId = json.RootElement.GetProperty("webhookId").GetString()!;
        _webhooks.VanishOnRotate = true;

        NewRequest();
        var (answer, outcome) = await RotateAsync(webhookId);

        Assert.Equal(McpCallResult.Failed, outcome.Result);
        Assert.Equal("not_found", Code(answer));
    }

    [Fact]
    public async Task Every_refusal_of_the_setup_tools_carries_a_contract_code()
    {
        // A refusal the filter makes before the gateway (here: a policy denial) is an envelope as well.
        _mcp.Policy.ToolDenyList = ["create_git_deploy_app"];

        var (answer, outcome) = await CreateAppAsync("shop", null);

        Assert.NotEqual(McpCallResult.Executed, outcome.Result);
        Assert.Contains(Code(answer), McpErrorEnvelope.Codes);
        Assert.Empty(await _git.GetAppsAsync());
    }

    // ---- Helpers -----------------------------------------------------------

    private static string Text(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>A fresh request of the key, as each MCP call is one.</summary>
    private void NewRequest()
    {
        _mcp.Http = new DefaultHttpContext();
        _mcp.Http.Request.Headers.Authorization = "Bearer brokkr-key";
        _http.HttpContext = _mcp.Http;
    }

    private Task<(string Answer, McpCallOutcome Outcome)> CreateAppAsync(string name, string? token) =>
        CallAsync("create_git_deploy_app",
            token is null
                ? new { name, repoUrl = "https://example.org/shop.git", serverId = "local" }
                : (object)new { name, repoUrl = "https://example.org/shop.git", serverId = "local", token },
            () => GitDeployTools.CreateGitDeployApp(_http, _permissions, _git, _webhooks.Service,
                name, "https://example.org/shop.git", "local", token: token));

    private Task<(string Answer, McpCallOutcome Outcome)> SetTokenAsync(string appId, int revision, string token) =>
        CallAsync("set_git_deploy_token", new { appId, revision, token },
            () => GitDeployTools.SetGitDeployToken(_http, _permissions, _git, _webhooks.Service, appId, revision, token));

    private Task<(string Answer, McpCallOutcome Outcome)> CreateWebhookAsync(string appId) =>
        CallAsync("create_git_deploy_webhook", new { appId },
            () => GitDeployTools.CreateGitDeployWebhook(_http, _permissions, _git, _webhooks.Service, Services(), appId));

    private Task<(string Answer, McpCallOutcome Outcome)> RotateAsync(string webhookId) =>
        CallAsync("rotate_git_deploy_webhook_secret", new { webhookId },
            () => GitDeployTools.RotateGitDeployWebhookSecret(_http, _permissions, _webhooks.Service, webhookId));

    /// <summary>The call as a direct MCP client makes it: through the filter and the gateway, the tool running as the
    /// gateway's delegate.</summary>
    private async Task<(string Answer, McpCallOutcome Outcome)> CallAsync(string tool, object arguments,
        Func<Task<string>> run)
    {
        string? answer = null;
        var result = await _mcp.CallAsync(tool, async _ =>
        {
            answer = await run();
            return new CallToolResult { Content = [new TextContentBlock { Text = answer }] };
        }, arguments);
        var text = answer ?? string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (text, McpCallOutcomes.Get(_mcp.Http).Last());
    }

    private IServiceProvider Services() =>
        new ServiceCollection().AddSingleton<IServerConfigService>(_servers).BuildServiceProvider();

    private static string? Code(string answer)
    {
        using var json = JsonDocument.Parse(answer);
        return json.RootElement.TryGetProperty("error", out var code) ? code.GetString() : null;
    }

    private static (string Id, int Revision) IdAndRevision(string answer)
    {
        using var json = JsonDocument.Parse(answer);
        return (json.RootElement.GetProperty("id").GetString()!, json.RootElement.GetProperty("revision").GetInt32());
    }

    private async Task AssertNotInAuditAsync(string value)
    {
        foreach (var row in await _mcp.EventsAsync())
        {
            Assert.DoesNotContain(value, row.Details ?? "");
            Assert.DoesNotContain(value, row.TargetName ?? "");
        }
    }

    private static MutationRequest WebhookRequest(Capability capability, object arguments)
    {
        var args = JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        return new MutationRequest(capability.Id,
            new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "brokkr", McpPermissionLevels.Admin, CredentialId: "k1"),
            MutationSurface.Mcp, MutationTargets.FromArguments(capability, args), args);
    }

    /// <summary>The webhook store in memory: what the setup tools need, with a generated secret as the real one.</summary>
    private sealed class MemoryWebhooks
    {
        public List<WebhookEntity> All { get; } = [];
        public IWebhookService Service { get; }

        public MemoryWebhooks()
        {
            var (service, script) = ReadBackVerificationTests.Scripted<IWebhookService>.Create();
            script.On[nameof(IWebhookService.CreateWebhookAsync)] = a =>
            {
                var hook = (WebhookEntity)a[0]!;
                hook.Secret = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                All.Add(hook);
                return Task.FromResult(hook);
            };
            script.On[nameof(IWebhookService.GetWebhookAsync)] = a =>
                Task.FromResult(All.FirstOrDefault(w => w.WebhookId == (string)a[0]!));
            script.On[nameof(IWebhookService.GetWebhooksAsync)] = _ => Task.FromResult(All.ToList());
            script.On[nameof(IWebhookService.RegenerateSecretAsync)] = a => VanishOnRotate
                ? Vanish((string)a[0]!)
                : Rotate((string)a[0]!);
            Service = service;
        }

        /// <summary>The webhook is deleted by someone else just before its secret is replaced.</summary>
        public bool VanishOnRotate { get; set; }

        private Task<string> Rotate(string id)
        {
            var hook = All.Single(w => w.WebhookId == id);
            hook.Secret = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            return Task.FromResult(hook.Secret);
        }

        private Task<string> Vanish(string id)
        {
            All.RemoveAll(w => w.WebhookId == id);
            throw new InvalidOperationException("Webhook not found");
        }
    }
}
