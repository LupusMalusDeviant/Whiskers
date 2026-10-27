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
    private readonly FakeServerConfig _servers = new(new ServerConfig { Id = "local", Name = "Local", IsDefault = true });
    private readonly Roles _roles = new();
    private readonly Whitelist _whitelist = new();
    private readonly FakeNotifications _notify = new();
    private readonly RecordingAudit _audit = new();
    private readonly GitDeployService _git;
    private bool _readOnly;
    private string? _protect;

    public WebhookMutationTests()
    {
        Directory.CreateDirectory(_root);
        _whitelist.Allowed.Add(Admin);
        _roles.Map[Admin] = AppRole.Admin;
        _host.Files["/opt/shop/docker-compose.yml"] = "name: shop\nservices:\n  web:\n    image: nginx\n";
        _host.Files[$"/opt/whiskers-git/{AppId}/repo/docker-compose.yml"] =
            "name: payments\nservices:\n  web:\n    image: nginx\n";
        _git = new GitDeployService(_host, new ProtectedResourceResolutionTests.NoVault(),
            new ComposePlanner(_docker, _host), NullLogger<GitDeployService>.Instance, Path.Combine(_root, "apps.json"));
        _git.SaveAppAsync(new GitDeployApp
        {
            Id = AppId, Name = "payments", RepoUrl = "https://example.org/p.git", ServerId = "local",
        }, token: null, AdminId).GetAwaiter().GetResult();

        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<IAutomationMutationRunner>(_ => Automation());
        services.AddSingleton<INotificationService>(_notify);
        services.AddSingleton<IGitDeployService>(_git);
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

    private WebhookService Service() => new(_sp.GetRequiredService<IServiceScopeFactory>(), _docker, _host,
        new ComposePlanner(_docker, _host), NullLogger<WebhookService>.Instance);

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
        new StaticOptionsMonitor<AutomationOptions>(new AutomationOptions { RequireOwner = true })));

    private UiMutationRunner UiRunner(AppRole role) => new(Gateway(), new CapabilityCatalog(), new Person(role));

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
        public List<MutationRequest> Requests { get; } = [];

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("database is locked");
            Requests.Add(request);
            return Task.FromResult(1L);
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class Person(AppRole role) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Admin);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    public void Dispose()
    {
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* temp file */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}
