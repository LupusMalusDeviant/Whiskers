using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Services.Webhooks;
using static Whiskers.Tests.AutomationMutationRunnerTests;

namespace Whiskers.Tests;

/// <summary>
/// R1-09 (S-04/U-28, INV-04/20/38): a webhook has an owner whose authority an external call runs on, re-checked at
/// each delivery through the Mutation Gateway — for all four actions (restart, rebuild, compose deploy, Git deploy) —
/// and its management (create, take over, new secret, switch on/off, delete) is decided by the gateway too. Role
/// denial, the kill switch, a revoked owner and an unwritable audit each stop the action before anything runs; a
/// revoked owner also switches the webhook off and tells an admin. No secret reaches the gateway's arguments.
/// </summary>
public sealed class WebhookMutationTests : IDisposable
{
    private const string Admin = "admin@example.com";
    private static readonly string AdminId = PrincipalIds.User(Admin);
    private const string AppId = "0123456789ab";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"webhook-mutations-{Guid.NewGuid():N}.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"webhook-gitdeploy-{Guid.NewGuid():N}");
    private readonly ServiceProvider _sp;
    private readonly ProtectedResourceResolutionTests.FakeHost _host = new();
    private readonly FakeDocker _docker = new(
        Container("web-1", "aaa111", "running"),
        Container("worker", "bbb222", "exited"),
        Container("cache", "aaa999", "running"),
        Compose("shop-web-1", "shop", "web"),
        Compose("shop-db", "shop", "db"),
        Compose("payments-web-1", "payments", "web"))
    {
        AllowMutations = true,
    };
    private readonly FakeServerConfig _servers = new(new ServerConfig { Id = "local", Name = "Local", IsDefault = true },
        new ServerConfig { Id = "other", Name = "Other" });
    private readonly Roles _roles = new();
    private readonly Whitelist _whitelist = new();
    private readonly FakeNotifications _notify = new();
    private readonly RecordingAudit _audit = new();
    private readonly GitDeployService _git;
    private readonly ResourceLocks _locks;
    private bool _readOnly;
    private string? _protect;
    private bool _requireOwner = true;
    private bool _dockerThrows;
    private bool _dockerTimesOut;

    private readonly McpCallOutcomeTests.RecordingAuditLog _rows = new();

    public WebhookMutationTests()
    {
        _locks = new ResourceLocks(_servers);
        Directory.CreateDirectory(_root);
        _whitelist.Allowed.Add(Admin);
        _roles.Map[Admin] = AppRole.Admin;
        _host.Files["/opt/shop/docker-compose.yml"] = "name: shop\nservices:\n  web:\n    image: nginx\n";
        _host.Files[$"/opt/whiskers-git/{AppId}/repo/docker-compose.yml"] =
            "name: payments\nservices:\n  web:\n    image: nginx\n";
        _git = new GitDeployService(_host, new ProtectedResourceResolutionTests.NoVault(),
            new ComposePlanner(_docker, _host), NullLogger<GitDeployService>.Instance, Path.Combine(_root, "apps.json"),
            locks: _locks);
        _git.SaveAppAsync(new GitDeployApp
        {
            Id = AppId, Name = "payments", RepoUrl = "https://example.org/p.git", ServerId = "local",
        }, token: null, AdminId).GetAwaiter().GetResult();

        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        services.AddSingleton<IAutomationMutationRunner>(_ => Automation());
        services.AddSingleton<INotificationService>(_notify);
        services.AddSingleton<IGitDeployService>(_git);
        services.AddSingleton<Whiskers.Services.AuditLog.IAuditLogService>(_rows);
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public static TheoryData<string, string> Actions => new()
    {
        { "restart", "web-1" },
        { "rebuild", "web-1" },
        { "deploy", "/opt/shop" },
        { "git-deploy", AppId },
    };

    // ---------------------------------------------------------------- external delivery: the owner's authority

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Each_action_runs_on_the_owner_authority_through_the_gateway(string action, string target)
    {
        var webhook = Seed(action, target);

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.True(success, output);
        Assert.True(Ran(action));
        var principal = Assert.Single(_audit.Requests.Select(r => r.Principal).DistinctBy(p => p.Id),
            p => p.Id == "webhook:" + webhook.WebhookId);
        Assert.Equal(MutationPrincipalKind.Automation, principal.Kind);
        Assert.Equal(AdminId, principal.OnBehalfOf?.Id);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task An_owner_without_the_role_is_refused_and_the_webhook_is_switched_off(string action, string target)
    {
        var webhook = Seed(action, target);
        _roles.Map[Admin] = AppRole.Viewer;

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.StartsWith("Suspended", output);
        Assert.False(Ran(action));
        Assert.False(Stored(webhook.WebhookId).Enabled);
        Assert.Contains(_notify.Events, e => e.EventType == "automation_suspended");
        // The switch-off is a configuration change of its own: one row, written after the run's event (R1-22).
        var row = Assert.Single(_rows.Entries, r => r.Action == WebhookService.SuspendAction);
        Assert.Matches(@"^operation=[^;]+; config\.version ([0-9a-f]{12}) → (?!\1)[0-9a-f]{12}; Enabled: true → false$", row.Details);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task An_owner_removed_from_the_whitelist_is_refused_and_the_webhook_is_switched_off(string action, string target)
    {
        var webhook = Seed(action, target);
        _whitelist.Allowed.Remove(Admin);

        var (success, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.False(Ran(action));
        Assert.False(Stored(webhook.WebhookId).Enabled);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task The_kill_switch_refuses_every_action_and_leaves_the_webhook_on(string action, string target)
    {
        var webhook = Seed(action, target);
        _readOnly = true;

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.StartsWith("Refused", output);
        Assert.False(Ran(action));
        Assert.True(Stored(webhook.WebhookId).Enabled);
        Assert.Contains(_notify.Events, e => e.EventType == "automation_refused");
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task An_unwritable_audit_refuses_every_action(string action, string target)
    {
        var webhook = Seed(action, target);
        _audit.Fail = true;

        var (success, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.False(Ran(action));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task A_webhook_without_an_owner_is_decided_refused_and_switched_off(string action, string target)
    {
        var webhook = Seed(action, target, owner: null);

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.Contains("no owner", output);
        Assert.False(Ran(action));
        Assert.Contains(_audit.Requests, r => r.Principal.Id == "unowned:webhook:" + webhook.WebhookId);
        Assert.False(Stored(webhook.WebhookId).Enabled);
        Assert.Contains(_notify.Events, e => e.EventType == "automation_suspended");
    }

    public static TheoryData<string, string> UnresolvableTargets => new()
    {
        { "restart", "gone" },
        { "rebuild", "aaa" },             // ambiguous id prefix
        { "deploy", "/opt/missing" },     // no compose file: no plan
        { "git-deploy", "fedcba987654" }, // no such app
    };

    [Theory]
    [MemberData(nameof(UnresolvableTargets))]
    public async Task A_revoked_owner_switches_the_webhook_off_even_when_its_target_cannot_be_resolved(string action,
        string target)
    {
        var webhook = Seed(action, target);
        _roles.Map[Admin] = AppRole.Viewer;

        var (success, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.False(Ran(action));
        Assert.NotEmpty(_audit.Requests);
        Assert.False(Stored(webhook.WebhookId).Enabled);
        Assert.Contains(_notify.Events, e => e.EventType == "automation_suspended");
    }

    [Theory]
    [MemberData(nameof(UnresolvableTargets))]
    public async Task An_unresolvable_target_is_decided_and_refused_and_nothing_runs(string action, string target)
    {
        var webhook = Seed(action, target);

        var (success, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.False(Ran(action));
        Assert.NotEmpty(_audit.Requests);
        Assert.True(Stored(webhook.WebhookId).Enabled);
        Assert.Contains(_notify.Events, e => e.EventType == "automation_refused");
    }

    [Theory]
    [InlineData("admin")]   // an owner who still holds the authority: refused and reported, left on
    [InlineData("revoked")]
    [InlineData(null)]
    public async Task A_lookup_that_throws_is_still_decided_and_handled(string? owner)
    {
        var webhook = Seed("restart", "web-1", owner: owner == "revoked" ? "user:gone@example.com" : owner);
        _dockerThrows = true;   // e.g. the webhook's server was removed from the registry

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.DoesNotContain("socket", output);   // the lookup's detail stays in the log
        Assert.Empty(_docker.Actions);
        Assert.NotEmpty(_audit.Requests);
        Assert.Equal(owner == "admin", Stored(webhook.WebhookId).Enabled);
        Assert.Contains(_notify.Events, e => e.EventType == (owner == "admin" ? "automation_refused" : "automation_suspended"));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task A_webhook_without_an_owner_is_switched_off_also_when_owners_are_optional(string action, string target)
    {
        var webhook = Seed(action, target, owner: null);
        _requireOwner = false;   // the grace period for other automation

        var (success, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.False(Ran(action));
        Assert.False(Stored(webhook.WebhookId).Enabled);
        Assert.Contains(_notify.Events, e => e.EventType == "automation_suspended");
    }

    [Fact]
    public async Task A_lookup_that_times_out_is_still_decided_and_switches_off_a_revoked_owner()
    {
        var webhook = Seed("restart", "web-1", owner: "user:gone@example.com");
        _dockerThrows = true;
        _dockerTimesOut = true;

        var (success, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.NotEmpty(_audit.Requests);
        Assert.False(Stored(webhook.WebhookId).Enabled);
        Assert.Contains(_notify.Events, e => e.EventType == "automation_suspended");
    }

    [Fact]
    public async Task A_plan_error_never_carries_a_value_from_the_env_into_audit_notification_or_webhook_log()
    {
        const string Password = "S3nsitive!Value";
        _host.Files["/opt/leaky/docker-compose.yml"] = "services:\n  web:\n    image: nginx\n";
        _host.Files["/opt/leaky/.env"] = $"DB_PASSWORD='{Password}'\nCOMPOSE_PROJECT_NAME=${{DB_PASSWORD}}\n";
        var webhook = Seed("deploy", "/opt/leaky");
        // The planner itself quotes the value (the page's Test shows it to the person) …
        var (_, planError) = await WebhookTargets.ComposePlanAsync(new ComposePlanner(_docker, _host), webhook);
        Assert.Contains(Password, planError);

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.DoesNotContain(Password, output);
        Assert.DoesNotContain(_audit.Reasons, r => r.Contains(Password));
        Assert.DoesNotContain(_notify.Events, e => (e.ImageInfo ?? "").Contains(Password));
        using var scope = _sp.CreateScope();
        Assert.DoesNotContain(scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookLogs.ToList(),
            l => (l.Error ?? "").Contains(Password) || (l.Output ?? "").Contains(Password));
        Assert.Contains(_notify.Events, e => e.EventType == "automation_refused");
    }

    [Fact]
    public async Task A_project_name_interpolated_from_the_env_is_decided_and_audited_as_its_source()
    {
        const string Password = "a9c42de780fb";   // a valid project name: the plan succeeds and the deploy runs
        _host.Files["/opt/secretname/docker-compose.yml"] = "services:\n  web:\n    image: nginx\n";
        _host.Files["/opt/secretname/.env"] = $"DB_PASSWORD='{Password}'\nCOMPOSE_PROJECT_NAME=${{DB_PASSWORD}}\n";
        var webhook = Seed("deploy", "/opt/secretname");

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.True(success, output);
        Assert.Contains(_host.Executed, c => c.Contains($"-p '{Password}'"));   // compose still gets the real name
        var decision = Assert.Single(_audit.Requests, r => r.CapabilityId == "compose.deploy");
        Assert.Equal("${DB_PASSWORD}", decision.Arguments["projectName"].GetString());
        var recorded = string.Join("\n", _audit.Requests.Select(r => JsonSerializer.Serialize(r.Arguments) + r.Target))
                       + string.Join("\n", _audit.Reasons);
        Assert.DoesNotContain(Password, recorded);
    }

    [Theory]
    [InlineData(null, "COMPOSE_PROJECT_NAME=shop\n", null, null)]                      // literal in the .env
    [InlineData(null, "STAGE=prod\nCOMPOSE_PROJECT_NAME=app-${STAGE}\n", null, "app-${STAGE}")]
    [InlineData(null, "COMPOSE_PROJECT_NAME='${NOT_INTERPOLATED}'\n", null, null)]   // single quotes: literal
    [InlineData(null, "", "shop", null)]                                              // literal top-level name
    [InlineData(null, "STAGE=prod\n", "app-${STAGE}", "app-${STAGE}")]
    [InlineData("explicit", "STAGE=prod\nCOMPOSE_PROJECT_NAME=${STAGE}\n", "app-${STAGE}", null)]     // -p wins and is literal
    [InlineData(null, "", null, null)]                                                // the directory's name
    public void The_project_name_source_follows_the_order_compose_uses(string? explicitName, string env,
        string? declaredSource, string? expected)
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsed = ComposeModel.ParseEnv(env, templates);
        var declared = declaredSource is null ? null : ComposeModel.Interpolate(declaredSource, parsed, "name");

        Assert.Equal(expected, ComposeModel.ProjectNameSource(explicitName, parsed, templates, declared, declaredSource));
    }

    [Theory]
    [InlineData("gone")]      // the compose file disappears after the decision
    [InlineData("changed")]   // it now names another project, whose containers were not decided
    public async Task A_plan_that_changes_before_compose_up_is_a_reported_refusal(string change)
    {
        var webhook = Seed("deploy", "/opt/shop");
        _audit.OnBegin = r =>
        {
            if (r.CapabilityId != "compose.deploy" || !r.Arguments.ContainsKey("containerId")) return;
            if (change == "gone") _host.Files.Remove("/opt/shop/docker-compose.yml");
            else _host.Files["/opt/shop/docker-compose.yml"] = "name: payments\nservices:\n  web:\n    image: nginx\n";
        };

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.StartsWith("Refused", output);
        Assert.False(Ran("deploy"));
        Assert.Contains(_notify.Events, e => e.EventType == "automation_refused");
    }

    [Fact]
    public async Task A_plan_pass_that_times_out_right_before_compose_up_is_a_reported_refusal()
    {
        var webhook = Seed("deploy", "/opt/shop");
        _audit.OnBegin = r =>
        {
            // The first plan was made; Docker times out from the per-container decisions on.
            if (r.CapabilityId == "compose.deploy" && r.Arguments.ContainsKey("containerId"))
                (_dockerThrows, _dockerTimesOut) = (true, true);
        };

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.StartsWith("Refused", output);
        Assert.False(Ran("deploy"));
        Assert.Contains(_notify.Events, e => e.EventType == "automation_refused");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("take-over")]
    [InlineData("webhook.rotate_secret")]
    [InlineData("webhook.set_enabled")]
    [InlineData("webhook.delete")]
    public async Task A_revoked_owner_neither_blocks_an_admin_nor_keeps_management_authority(string capability)
    {
        // The owner lost Admin: the webhook stays manageable by an admin who holds it now …
        var webhook = Seed("restart", "web-1", owner: AdminId);
        const string Current = "current@example.com";
        _whitelist.Allowed.Add(Current);
        _roles.Map[Current] = AppRole.Admin;
        _roles.Map[Admin] = AppRole.Viewer;

        // … while the revoked owner, signed in with the role they hold now, can no longer manage anything.
        await Assert.ThrowsAsync<MutationRefusedException>(() => Manage(UiRunner(AppRole.Viewer), capability, webhook));
        Assert.Equal(Snapshot(webhook), Snapshot(Stored(webhook.WebhookId)));
        Assert.Equal(1, Count());

        await Manage(UiRunner(AppRole.Admin, Current), capability, webhook);
    }

    [Fact]
    public async Task An_unwritable_audit_at_a_container_of_a_git_deploy_is_a_reported_refusal()
    {
        var webhook = Seed("git-deploy", AppId);
        // Only the per-container decisions inside the Git deployment cannot be audited.
        _audit.FailWhen = r => r.CapabilityId == "gitdeploy.deploy" && r.Arguments.ContainsKey("containerId");

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.StartsWith("Refused", output);
        Assert.DoesNotContain(_host.Executed, c => c.Contains("docker compose"));
        Assert.Contains(_notify.Events, e => e.EventType == "automation_refused");
    }

    [Fact]
    public async Task A_protected_container_of_the_compose_plan_stops_the_deploy_before_compose_up()
    {
        var webhook = Seed("deploy", "/opt/shop");
        _protect = "shop-web-1";   // an existing container `up` recreates

        var (success, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.DoesNotContain(_host.Executed, c => c.Contains("docker compose"));
    }

    [Fact]
    public async Task A_git_deployment_waits_for_its_compose_project_whoever_holds_it()
    {
        var webhook = Seed("git-deploy", AppId);
        // Another deployment of the app's compose project ("payments") — a compose webhook, another app — holds it.
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));

        var test = Service().TriggerSignedTestAsync(webhook.WebhookId);
        await Task.Delay(500);
        Assert.False(Ran("git-deploy"));

        held.Dispose();
        var (success, output) = await test;
        Assert.True(success, output);
        Assert.True(Ran("git-deploy"));
        Assert.Equal(0, _locks.Count);
    }

    [Theory]
    [InlineData("git-deploy", AppId)]
    [InlineData("deploy", "/opt/shop")]
    public async Task A_deployment_waits_while_a_container_action_holds_its_server(string action, string target)
    {
        var webhook = Seed(action, target);
        // A webhook restart or rebuild on the server is running.
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));

        var test = Service().TriggerSignedTestAsync(webhook.WebhookId);
        await Task.Delay(500);
        Assert.False(Ran(action));

        held.Dispose();
        var (success, output) = await test;
        Assert.True(success, output);
        Assert.True(Ran(action));
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public async Task A_git_deployment_does_not_touch_its_workspace_while_another_deployment_reads_it()
    {
        var webhook = Seed("git-deploy", AppId);
        var workDir = GitDeployCommands.WorkDir(AppId);
        // A compose webhook deploying from the app's checkout holds the workspace.
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));

        var test = Service().TriggerSignedTestAsync(webhook.WebhookId);
        await Task.Delay(500);
        Assert.DoesNotContain(_host.Executed, c => c.Contains(workDir));   // no credentials, no fetch, no checkout

        held.Dispose();
        var (success, output) = await test;
        Assert.True(success, output);
        Assert.True(Ran("git-deploy"));
    }

    [Theory]
    [InlineData("restart", "web-1", "LOCAL")]
    [InlineData("restart", "web-1", "Local")]
    [InlineData("deploy", "/opt/shop", "LOCAL")]
    [InlineData("deploy", "/opt/shop", "Local")]
    public async Task A_webhook_naming_its_server_by_an_alias_waits_for_the_same_server(string action, string target,
        string alias)
    {
        // The id in another case and the display name are the same server: one lock.
        var webhook = Seed(action, target, serverId: alias);
        var service = Service();
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));

        var test = service.TriggerSignedTestAsync(webhook.WebhookId);
        await Task.Delay(500);
        Assert.False(test.IsCompleted, test.IsCompleted ? test.Result.Output : "");
        Assert.False(Ran(action));

        held.Dispose();
        var (success, output) = await test;
        // A compose deployment on an alias is refused once it may go (its plan is not the decided one): either way
        // it waited for the server.
        if (action == "restart") Assert.True(success, output);
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public void Every_alias_of_a_server_is_one_lock()
    {
        var locks = new ResourceLocks(new FakeServerConfig(new ServerConfig { Id = "rabenhof", Name = "Rabenhof (Hetzner)" }));

        Assert.Equal(ResourceLocks.Server("rabenhof"), locks.ServerKey("rabenhof"));
        Assert.Equal(ResourceLocks.Server("rabenhof"), locks.ServerKey("RABENHOF"));
        Assert.Equal(ResourceLocks.Server("rabenhof"), locks.ServerKey("rabenhof (hetzner)"));
        Assert.Equal(ResourceLocks.Server("gone"), locks.ServerKey("gone"));
    }

    [Fact]
    public async Task A_git_deployment_on_a_server_alias_waits_for_the_same_server()
    {
        var app = (await _git.GetAppAsync(AppId))!;
        app.ServerId = "LOCAL";
        await _git.SaveAppAsync(app, token: null, AdminId);
        var webhook = Seed("git-deploy", AppId);
        var service = Service();
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));

        var test = service.TriggerSignedTestAsync(webhook.WebhookId);
        await Task.Delay(500);
        Assert.False(test.IsCompleted, test.IsCompleted ? test.Result.Output : "");
        Assert.False(Ran("git-deploy"));

        held.Dispose();
        await test;
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public async Task A_git_webhook_whose_app_moves_to_another_server_while_it_waits_takes_that_server_and_is_checked_again()
    {
        var webhook = Seed("git-deploy", AppId);
        var service = Service();
        var local = await _locks.AcquireAsync(ResourceLocks.Server("local"));
        var other = await _locks.AcquireAsync(ResourceLocks.Server("other"));

        var test = service.TriggerSignedTestAsync(webhook.WebhookId);
        await Task.Delay(500);
        // Moved while the delivery waits for the old server; the new one is busy too.
        var app = (await _git.GetAppAsync(AppId))!;
        app.ServerId = "other";
        await _git.SaveAppAsync(app, token: null, AdminId);
        local.Dispose();
        await Task.Delay(500);
        Assert.False(test.IsCompleted);

        // Switched off while it waits for the new server: once it gets it, the webhook is read again.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            (await db.Webhooks.SingleAsync(w => w.WebhookId == webhook.WebhookId)).Enabled = false;
            await db.SaveChangesAsync();
        }
        other.Dispose();

        var (success, _) = await test;
        Assert.False(success);
        Assert.DoesNotContain(_host.Executed, c => c.Contains("git "));
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public async Task A_git_webhook_whose_app_was_missing_when_it_took_its_lock_does_not_deploy_an_app_saved_since()
    {
        var webhook = Seed("git-deploy", AppId);
        await _git.DeleteAppAsync(AppId);
        _host.Executed.Clear();
        var service = Service();
        // The run finds no app: it locks the app's id and waits for it.
        var held = await _locks.AcquireAsync("git:" + AppId);

        var test = service.TriggerSignedTestAsync(webhook.WebhookId);
        await Task.Delay(500);
        // Saved again meanwhile: the run holds no server for it.
        await _git.SaveAppAsync(new GitDeployApp
        {
            Id = AppId, Name = "payments", RepoUrl = "https://example.org/p.git", ServerId = "local",
        }, token: null, AdminId);
        held.Dispose();

        var (success, _) = await test;
        Assert.False(success);
        Assert.DoesNotContain(_host.Executed, c => c.Contains("git "));
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public async Task A_git_deployment_of_an_app_saved_after_the_delivery_got_its_server_is_refused()
    {
        var revision = (await _git.GetAppAsync(AppId))!.Revision;
        var app = (await _git.GetAppAsync(AppId))!;
        app.ServerId = "other";
        await _git.SaveAppAsync(app, token: null, AdminId);

        var (success, output, refused) = await GitDeployOwners.RunAsOwnerAsync(_git, Automation(), AppId,
            revision: revision);

        Assert.False(success);
        Assert.True(refused, output);
        Assert.DoesNotContain(_host.Executed, c => c.Contains("git "));
    }

    [Fact]
    public async Task Deleting_a_git_app_waits_for_its_server_before_removing_the_checkout()
    {
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));

        var delete = _git.DeleteAppAsync(AppId);
        await Task.Delay(500);
        Assert.False(delete.IsCompleted);
        Assert.DoesNotContain(_host.Executed, c => c.StartsWith("rm -rf"));

        held.Dispose();
        await delete;
        Assert.Contains(_host.Executed, c => c.StartsWith("rm -rf"));
        Assert.Null(await _git.GetAppAsync(AppId));
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public async Task A_git_webhook_switched_off_while_it_waits_for_its_server_does_not_check_out()
    {
        var webhook = Seed("git-deploy", AppId);
        var service = Service();
        // Another deployment holds the app's server.
        var held = await _locks.AcquireAsync(_locks.ServerKey("local"));

        var test = service.TriggerSignedTestAsync(webhook.WebhookId);
        await Task.Delay(500);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            (await db.Webhooks.SingleAsync(w => w.WebhookId == webhook.WebhookId)).Enabled = false;
            await db.SaveChangesAsync();
        }
        held.Dispose();

        var (success, _) = await test;
        Assert.False(success);
        Assert.DoesNotContain(_host.Executed, c => c.Contains("git "));
        Assert.False(Ran("git-deploy"));
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public async Task A_compose_webhook_on_a_symlink_or_an_outside_compose_file_waits_while_a_git_deployment_holds_the_server()
    {
        // A directory that is a symlink to a checkout, or whose .env points COMPOSE_FILE into it: no path tells.
        _host.Files["/srv/shop/docker-compose.yml"] = "name: payments\nservices:\n  web:\n    image: nginx\n";
        var compose = Seed("deploy", "/srv/shop");
        var git = Seed("git-deploy", AppId);
        var service = Service();
        // The Git deployment is in its checkout: it holds the server.
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));

        var test = service.TriggerSignedTestAsync(compose.WebhookId);
        await Task.Delay(500);
        Assert.False(Ran("deploy"));

        held.Dispose();
        var (success, output) = await test;
        Assert.True(success, output);
        Assert.True(Ran("deploy"));
        Assert.True((await service.TriggerSignedTestAsync(git.WebhookId)).Success);
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public async Task A_compose_webhook_on_a_git_checkout_waits_for_its_workspace()
    {
        var compose = Seed("deploy", $"{GitDeployCommands.WorkDir(AppId)}/repo");
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));

        var test = Service().TriggerSignedTestAsync(compose.WebhookId);
        await Task.Delay(500);
        Assert.False(Ran("deploy"));

        held.Dispose();
        var (success, output) = await test;
        Assert.True(success, output);
        Assert.True(Ran("deploy"));
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public async Task A_git_app_changed_while_its_deployment_waited_for_the_project_does_not_deploy()
    {
        // A deployment decided on the app (from the Git deploy page) waits for its server.
        var decided = (await _git.GetAppAsync(AppId))!;
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));
        var test = _git.DeployAsync(decided, (_, proceed) => proceed());
        await Task.Delay(500);

        // Saved again while the deployment waits: it is no longer the app the deployment was decided on.
        var app = (await _git.GetAppAsync(AppId))!;
        app.Branch = "release";
        await _git.SaveAppAsync(app, token: null, AdminId);
        held.Dispose();

        var (success, _) = await test;
        Assert.False(success);
        Assert.False(Ran("git-deploy"));
    }

    [Fact]
    public async Task A_compose_webhook_and_a_git_deployment_of_one_project_share_its_lock()
    {
        _host.Files["/opt/payments/docker-compose.yml"] = "name: payments\nservices:\n  web:\n    image: nginx\n";
        var compose = Seed("deploy", "/opt/payments");
        var service = Service();
        var held = await _locks.AcquireAsync(ResourceLocks.Server("local"));

        var test = service.TriggerSignedTestAsync(compose.WebhookId);
        await Task.Delay(500);
        Assert.False(Ran("deploy"));

        held.Dispose();
        var (success, output) = await test;
        Assert.True(success, output);
        Assert.True(Ran("deploy"));
    }

    [Fact]
    public async Task A_stopped_container_is_a_target_too()
    {
        var webhook = Seed("restart", "worker");

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.True(success, output);
        Assert.Contains(_docker.Actions, a => a.Op == "restart" && a.ContainerId.StartsWith("bbb222") && a.ServerId == "local");
    }

    [Fact]
    public async Task An_ambiguous_prefix_is_refused_rather_than_the_first_match()
    {
        var webhook = Seed("restart", "aaa");

        var (success, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.Empty(_docker.Actions);
    }

    [Fact]
    public async Task A_git_deploy_needs_the_webhook_owner_and_the_app_owner()
    {
        const string Other = "other@example.com";
        _whitelist.Allowed.Add(Other);
        _roles.Map[Other] = AppRole.Admin;
        var webhook = Seed("git-deploy", AppId, owner: PrincipalIds.User(Other));
        _roles.Map[Admin] = AppRole.Viewer;   // the app's owner lost Admin; the webhook's owner still has it

        var (success, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);

        Assert.False(success);
        Assert.StartsWith("Refused", output);
        Assert.DoesNotContain(_host.Executed, c => c.Contains("docker compose"));
        Assert.Contains(_notify.Events, e => e.EventType == "automation_refused");
        Assert.True(Stored(webhook.WebhookId).Enabled);   // the webhook's own owner still holds the authority
    }

    // ---------------------------------------------------------------- management through the gateway

    [Fact]
    public async Task An_admin_creates_a_webhook_as_its_owner_and_no_secret_reaches_the_gateway()
    {
        var runner = UiRunner(AppRole.Admin);
        var entity = new WebhookEntity { Name = "ci", Action = "restart", TargetId = "web-1", ServerId = "local", Owner = AdminId };

        var created = await runner.RunAsync("webhook.save",
            new { name = entity.Name, action = entity.Action, target = entity.TargetId, serverId = entity.ServerId, owner = AdminId },
            _ => Service().CreateWebhookAsync(entity));
        var secret = await runner.RunAsync("webhook.rotate_secret",
            new { webhookId = created.WebhookId, name = created.Name, serverId = created.ServerId },
            _ => Service().RegenerateSecretAsync(created.WebhookId));

        Assert.Equal(AdminId, Stored(created.WebhookId).Owner);
        Assert.Equal(64, secret.Length);
        var arguments = string.Join("\n", _audit.Requests.Select(r => JsonSerializer.Serialize(r.Arguments)));
        Assert.DoesNotContain(secret, arguments);
        Assert.DoesNotContain(created.Secret, arguments);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("take-over")]
    [InlineData("webhook.rotate_secret")]
    [InlineData("webhook.set_enabled")]
    [InlineData("webhook.delete")]
    public async Task An_operator_cannot_manage_webhooks(string capability)
    {
        var webhook = Seed("restart", "web-1");

        await Assert.ThrowsAsync<MutationRefusedException>(() => Manage(UiRunner(AppRole.Operator), capability, webhook));

        Assert.Equal(Snapshot(webhook), Snapshot(Stored(webhook.WebhookId)));
        Assert.Equal(1, Count());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("take-over")]
    [InlineData("webhook.rotate_secret")]
    [InlineData("webhook.set_enabled")]
    [InlineData("webhook.delete")]
    public async Task The_kill_switch_and_an_unwritable_audit_stop_management_writes(string capability)
    {
        var webhook = Seed("restart", "web-1");

        _readOnly = true;
        await Assert.ThrowsAsync<MutationRefusedException>(() => Manage(UiRunner(AppRole.Admin), capability, webhook));
        _readOnly = false;
        _audit.Fail = true;
        await Assert.ThrowsAsync<MutationRefusedException>(() => Manage(UiRunner(AppRole.Admin), capability, webhook));

        Assert.Equal(Snapshot(webhook), Snapshot(Stored(webhook.WebhookId)));
        Assert.Equal(1, Count());
    }

    [Fact]
    public async Task An_admin_recovers_a_webhook_whose_owner_was_revoked_by_taking_it_over()
    {
        const string Gone = "gone@example.com";
        var webhook = Seed("restart", "web-1", owner: PrincipalIds.User(Gone));   // never whitelisted: no authority

        var (before, _) = await Service().TriggerSignedTestAsync(webhook.WebhookId);
        Assert.False(before);
        Assert.False(Stored(webhook.WebhookId).Enabled);

        var runner = UiRunner(AppRole.Admin);
        Assert.True(await runner.RunAsync("webhook.save",
            new { webhookId = webhook.WebhookId, name = webhook.Name, serverId = webhook.ServerId, owner = AdminId },
            _ => Service().SetOwnerAsync(webhook.WebhookId, AdminId, PrincipalIds.User(Gone))));
        await runner.RunAsync("webhook.set_enabled",
            new { webhookId = webhook.WebhookId, name = webhook.Name, serverId = webhook.ServerId, enabled = true },
            _ => Service().SetEnabledAsync(webhook.WebhookId, true));

        var (after, output) = await Service().TriggerSignedTestAsync(webhook.WebhookId);
        Assert.True(after, output);
        Assert.Equal(AdminId, Stored(webhook.WebhookId).Owner);
    }

    [Fact]
    public async Task A_webhook_whose_server_was_removed_can_still_be_switched_off_and_deleted()
    {
        var webhook = Seed("restart", "web-1", serverId: "edge-removed");
        var runner = UiRunner(AppRole.Admin);

        await runner.RunAsync("webhook.set_enabled",
            new { webhookId = webhook.WebhookId, name = webhook.Name, serverId = webhook.ServerId, enabled = false },
            _ => Service().SetEnabledAsync(webhook.WebhookId, false));
        Assert.False(Stored(webhook.WebhookId).Enabled);
        await runner.RunAsync("webhook.delete",
            new { webhookId = webhook.WebhookId, name = webhook.Name, serverId = webhook.ServerId },
            _ => Service().DeleteWebhookAsync(webhook.WebhookId));

        Assert.Equal(0, Count());
    }

    [Fact]
    public async Task Management_is_decided_on_the_stored_webhook()
    {
        var webhook = Seed("restart", "web-1");

        await UiRunner(AppRole.Admin).RunAsync("webhook.set_enabled",
            new { webhookId = webhook.WebhookId, name = "renamed-on-the-page", serverId = "elsewhere", enabled = false },
            _ => Service().SetEnabledAsync(webhook.WebhookId, false));
        await Assert.ThrowsAsync<MutationRefusedException>(() => UiRunner(AppRole.Admin).RunAsync("webhook.delete",
            new { webhookId = "does-not-exist", name = "x" }, _ => Task.CompletedTask));

        var decided = _audit.Requests.First(r => r.CapabilityId == "webhook.set_enabled").Target;
        Assert.Equal(webhook.Name, decided.Name);
        Assert.Equal("local", decided.ServerId);
    }

    [Fact]
    public async Task A_takeover_only_replaces_the_owner_it_was_shown()
    {
        var webhook = Seed("restart", "web-1", owner: null);
        var service = Service();

        Assert.True(await service.SetOwnerAsync(webhook.WebhookId, AdminId, expectedOwnerId: null));
        Assert.False(await service.SetOwnerAsync(webhook.WebhookId, "user:late@example.com", expectedOwnerId: null));
        Assert.Equal(AdminId, Stored(webhook.WebhookId).Owner);
    }

    // ---------------------------------------------------------------- helpers

    private WebhookService Service()
    {
        // The Docker service of the webhook (lookup and planning) starts failing whenever a test says so.
        var docker = ThrowingDocker.Wrap(_docker, () => _dockerThrows, () => _dockerTimesOut);
        return new(_sp.GetRequiredService<IServiceScopeFactory>(), docker, _host, new ComposePlanner(docker, _host),
            NullLogger<WebhookService>.Instance, locks: _locks, vault: TestVault.Usable);
    }

    /// <summary>A Docker service whose container listing fails, as for a server that is no longer registered.</summary>
    public class ThrowingDocker : System.Reflection.DispatchProxy
    {
        private Whiskers.Services.Docker.IDockerService _inner = null!;
        private Func<bool> _throws = () => false;
        private Func<bool> _timeout = () => false;

        public static Whiskers.Services.Docker.IDockerService Wrap(Whiskers.Services.Docker.IDockerService inner,
            Func<bool> throws, Func<bool> timeout)
        {
            var proxy = Create<Whiskers.Services.Docker.IDockerService, ThrowingDocker>();
            var self = (ThrowingDocker)(object)proxy;
            (self._inner, self._throws, self._timeout) = (inner, throws, timeout);
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method!.Name != nameof(Whiskers.Services.Docker.IDockerService.ListContainersAsync) || !_throws()
                ? method.Invoke(_inner, args)
                : _timeout()
                    ? throw new TaskCanceledException("The host deadline expired.")
                    : throw new InvalidOperationException("Cannot connect to the docker socket of 'edge-removed'");
    }

    private bool Ran(string action) => action switch
    {
        "restart" => _docker.Actions.Any(a => a.Op == "restart"),
        "rebuild" => _docker.Actions.Any(a => a.Op == "recreate"),
        _ => _host.Executed.Any(c => c.Contains("docker compose") && c.Contains(" up ")),
    };

    private WebhookEntity Seed(string action, string target, string? owner = "admin", string serverId = "local")
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var webhook = new WebhookEntity
        {
            Name = $"wh-{action}", Secret = WebhookService.GenerateSecret(), Action = action,
            TargetType = action == "deploy" ? "compose" : "container", TargetId = target, ServerId = serverId,
            Owner = owner == "admin" ? AdminId : owner,
        };
        db.Webhooks.Add(webhook);
        db.SaveChanges();
        return webhook;
    }

    private WebhookEntity Stored(string webhookId)
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Webhooks.AsNoTracking()
            .Single(w => w.WebhookId == webhookId);
    }

    private int Count()
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Webhooks.Count();
    }

    private static string Snapshot(WebhookEntity w) => $"{w.Name}|{w.Secret}|{w.Enabled}|{w.Owner}|{w.TargetId}";

    private Task Manage(IUiMutationRunner runner, string capability, WebhookEntity webhook) => capability switch
    {
        "create" => runner.RunAsync("webhook.save",
            new { name = "new", action = "restart", target = "web-1", serverId = "local", owner = AdminId },
            _ => Service().CreateWebhookAsync(new WebhookEntity
            {
                Name = "new", Action = "restart", TargetId = "web-1", ServerId = "local", Owner = AdminId,
            })),
        "take-over" => runner.RunAsync("webhook.save",
            new { webhookId = webhook.WebhookId, name = webhook.Name, serverId = webhook.ServerId, owner = "user:x@example.com" },
            _ => Service().SetOwnerAsync(webhook.WebhookId, "user:x@example.com", webhook.Owner)),
        "webhook.rotate_secret" => runner.RunAsync("webhook.rotate_secret",
            new { webhookId = webhook.WebhookId, name = webhook.Name, serverId = webhook.ServerId },
            _ => Service().RegenerateSecretAsync(webhook.WebhookId)),
        "webhook.set_enabled" => runner.RunAsync("webhook.set_enabled",
            new { webhookId = webhook.WebhookId, name = webhook.Name, serverId = webhook.ServerId, enabled = false },
            _ => Service().SetEnabledAsync(webhook.WebhookId, false)),
        _ => runner.RunAsync("webhook.delete",
            new { webhookId = webhook.WebhookId, name = webhook.Name, serverId = webhook.ServerId },
            _ => Service().DeleteWebhookAsync(webhook.WebhookId)),
    };

    private MutationGateway Gateway()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        policy.ReadOnlyMode = _readOnly;
        policy.ProtectedResources = _protect is null ? [] : [_protect];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        return new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance,
            targets: new MutationTargetResolver(new TestServices(_docker, _servers, _git, Service())),
            approvals: new ApprovalGate(new ApprovalStore(), store));
    }

    // Built per call so the kill switch and protected names set by a test apply.
    private IAutomationMutationRunner Automation() => new LateRunner(() => new AutomationMutationRunner(Gateway(),
        new CapabilityCatalog(), new AutomationOwnerResolver(_roles, _whitelist, new Keys(), new Accounts()),
        new StaticOptionsMonitor<AutomationOptions>(new AutomationOptions { RequireOwner = _requireOwner })));

    private UiMutationRunner UiRunner(AppRole role, string email = Admin) =>
        new(Gateway(), new CapabilityCatalog(), new Person(role, email));

    private static ContainerInfo Container(string name, string id, string state) => new()
    {
        Id = id + new string('0', 58), Name = name, ServerId = "local", State = state,
    };

    private static ContainerInfo Compose(string name, string project, string service) => new()
    {
        Id = name + "-id", Name = name, ServerId = "local", State = "running",
        Labels = new() { ["com.docker.compose.project"] = project, ["com.docker.compose.service"] = service },
    };

    /// <summary>An automation runner whose gateway is built when it runs, so a test's settings apply.</summary>
    private sealed class LateRunner(Func<AutomationMutationRunner> build) : IAutomationMutationRunner
    {
        public Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task<T>> action, CancellationToken ct = default) =>
            build().RunAsync(actor, capabilityId, arguments, action, ct);

        public Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task> action, CancellationToken ct = default) =>
            build().RunAsync(actor, capabilityId, arguments, action, ct);

        public string? LostAuthority(AutomationActor actor, string capabilityId) =>
            build().LostAuthority(actor, capabilityId);
    }

    /// <summary>Remembers every request the gateway audits; <see cref="Fail"/> makes the intent unwritable.</summary>
    private sealed class RecordingAudit : IMutationAuditSink
    {
        public bool Fail { get; set; }
        public Func<MutationRequest, bool>? FailWhen { get; set; }
        public Action<MutationRequest>? OnBegin { get; set; }
        public List<MutationRequest> Requests { get; } = [];
        public List<string> Reasons { get; } = [];

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            if (Fail || FailWhen?.Invoke(request) == true) throw new InvalidOperationException("database is locked");
            Requests.Add(request);
            OnBegin?.Invoke(request);
            return Task.FromResult(1L);
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct)
        {
            if (reason is not null) Reasons.Add(reason);
            return Task.CompletedTask;
        }

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            Requests.Add(request);
            Reasons.Add(reason);
            return Task.CompletedTask;
        }
    }

    private sealed class Person(AppRole role, string email = Admin) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(email);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { File.Delete(_dbPath); } catch { /* temp file */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}
