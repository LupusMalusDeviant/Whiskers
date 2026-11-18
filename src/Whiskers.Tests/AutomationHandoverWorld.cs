using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Services.Scheduler;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0020: the real stores of the six automation kinds over one temp data directory and one SQLite database,
/// the real ledger and notices, the real owner resolver over fake roles/whitelist/keys/accounts, and a real automation
/// runner on a gateway (so a run carries the first-run note).
/// </summary>
internal sealed class AutomationHandoverWorld : IDisposable
{
    public const string Admin = "admin@example.com";
    public const string Gone = "gone@example.com";
    public const string NewOwner = "new@example.com";
    public const string Still = "still@example.com";
    public static readonly string AdminId = PrincipalIds.User(Admin);
    public static readonly string GoneId = PrincipalIds.User(Gone);
    public static readonly string NewId = PrincipalIds.User(NewOwner);
    public static readonly string StillId = PrincipalIds.User(Still);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"handover-{Guid.NewGuid():N}");
    private readonly EndpointCheckTestDoubles.Db _db = new();
    private readonly ServiceProvider _services;

    public DataPathOptions Paths { get; }
    public AutomationMutationRunnerTests.Roles Roles { get; } = new();
    public AutomationMutationRunnerTests.Whitelist Whitelist { get; } = new();
    public AutomationMutationRunnerTests.Keys Keys { get; } = new();
    public AutomationMutationRunnerTests.Accounts Accounts { get; } = new();
    public AutomationMutationRunnerTests.PrincipalAudit Audit { get; } = new();
    public FakeNotifications Notify { get; } = new();
    public IAutomationOwnerResolver Resolver { get; set; }
    public AutomationMutationRunner Runner { get; }
    public HandoverLedger Ledger { get; }
    public HandoverNotices Notices { get; }
    public SchedulerService Scheduler { get; }
    public WebhookService Webhooks { get; }
    public GitDeployService Git { get; }
    public AiTriggerStore Triggers { get; }
    public EndpointCheckStore Checks { get; }
    public AutomationOwnerStore Fleet { get; }
    public string TriggersPath { get; }

    public AutomationHandoverWorld()
    {
        Directory.CreateDirectory(_dir);
        Paths = new DataPathOptions(_dir);
        Whitelist.Allowed.UnionWith([Admin, NewOwner, Still]);
        Roles.Map[Admin] = AppRole.Admin;
        Roles.Map[NewOwner] = AppRole.Admin;
        Roles.Map[Still] = AppRole.Operator;
        Accounts.Deleted.Add(Gone);   // a local account that was deleted
        Resolver = new AutomationOwnerResolver(Roles, Whitelist, Keys, Accounts);

        Ledger = new HandoverLedger(Paths, NullLogger<HandoverLedger>.Instance);
        Notices = new HandoverNotices(Paths, NullLogger<HandoverNotices>.Instance);
        var policy = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(policy), Audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), policy),
            targets: TestTargets.Resolver());
        Runner = new AutomationMutationRunner(gateway, new CapabilityCatalog(), new LateResolver(this),
            new StaticOptionsMonitor<AutomationOptions>(new AutomationOptions()), handovers: Ledger);

        Scheduler = new SchedulerService(_db.Scopes, null!, null!, NullLogger<SchedulerService>.Instance, handovers: Ledger);
        Webhooks = new WebhookService(_db.Scopes, docker: null!, executor: null!, planner: null!,
            NullLogger<WebhookService>.Instance, handovers: Ledger);
        var host = new ProtectedResourceResolutionTests.FakeHost();
        Git = new GitDeployService(host, new ProtectedResourceResolutionTests.NoVault(),
            new ComposePlanner(new FakeDocker(), host), NullLogger<GitDeployService>.Instance,
            Path.Combine(_dir, "gitdeploy-apps.json"), handovers: Ledger);
        TriggersPath = Path.Combine(_dir, "ai-triggers.json");
        Triggers = new AiTriggerStore(TriggersPath, handovers: Ledger);
        Checks = new EndpointCheckStore(_db.Scopes,
            new FakeServerConfig(EndpointCheckTestDoubles.SshServer()), handovers: Ledger);
        Fleet = new AutomationOwnerStore(Paths, NullLogger<AutomationOwnerStore>.Instance, Ledger);

        var services = new ServiceCollection();
        services.AddSingleton<INotificationService>(Notify);
        _services = services.BuildServiceProvider();
    }

    /// <summary>The handover service over every store, with the world's (possibly replaced) resolver.</summary>
    public AutomationHandover Handover(IAutomationMutationRunner? runner = null) =>
        new(Resolver, runner ?? Runner, new CapabilityCatalog(), Ledger, Notices, scheduler: Scheduler, webhooks: Webhooks,
            gitDeploy: Git, triggers: Triggers, checks: Checks, fleet: Fleet, services: _services);

    public AutomationHold Hold() => new(Ledger, Scheduler, Webhooks, Git, Triggers, Checks, Fleet);

    /// <summary>The resolver the runner asks: the world's current one, so a test can swap it.</summary>
    private sealed class LateResolver(AutomationHandoverWorld world) : IAutomationOwnerResolver
    {
        public MutationPrincipal Resolve(string ownerId) => world.Resolver.Resolve(ownerId);
        public OwnerDescription Describe(string ownerId) => world.Resolver.Describe(ownerId);
    }

    /// <summary>A decision of <paramref name="capability"/> by <paramref name="principalId"/> (an Admin by default).</summary>
    public static MutationScope Decision(string capability, string? principalId = null, string level = McpPermissionLevels.Admin)
    {
        var id = principalId ?? AdminId;
        return MutationScope.Begin(capability, "op-" + Guid.NewGuid().ToString("N"),
            new MutationPrincipal(MutationPrincipalKind.Human, id, PrincipalIds.EmailOf(id) ?? id, level));
    }

    // ---- seeding ------------------------------------------------------------------------------------------------

    public async Task SeedTaskAsync(string id, string owner, bool enabled = true, string? via = null)
    {
        using var scope = _db.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.ScheduledTasks.Add(new ScheduledTaskEntity
        {
            TaskId = id, Name = "task " + id, CronExpression = "0 2 * * *", TaskType = ScheduledTaskType.ContainerRestart,
            TargetId = "web-1", Enabled = enabled, CreatedBy = owner, CreatedVia = via ?? owner,
        });
        await db.SaveChangesAsync();
    }

    public async Task<ScheduledTaskEntity> TaskAsync(string id) =>
        (await Scheduler.GetTasksAsync()).Single(t => t.TaskId == id);

    public async Task SeedWebhookAsync(string id, string owner, bool enabled = true, string action = "restart")
    {
        using var scope = _db.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.Webhooks.Add(new WebhookEntity
        {
            WebhookId = id, Name = "hook " + id, TargetId = "web", Action = action, Owner = owner, Enabled = enabled,
            Secret = "unused",
        });
        await db.SaveChangesAsync();
    }

    public async Task<WebhookEntity> WebhookAsync(string id) =>
        (await Webhooks.GetWebhooksAsync()).Single(w => w.WebhookId == id);

    public Task<GitDeployApp> SeedAppAsync(string id, string owner) =>
        Git.SaveAppAsync(new GitDeployApp { Id = id, Name = "app " + id, RepoUrl = "https://example.org/a.git", ServerId = "local" },
            token: null, owner);

    public async Task SeedTriggerAsync(string id, string owner, bool enabled = true)
    {
        await File.WriteAllTextAsync(TriggersPath, $$"""
            { "Triggers": [ { "Id": "{{id}}", "Name": "trigger {{id}}", "Enabled": {{(enabled ? "true" : "false")}},
                              "EventTypes": ["unhealthy"], "GuardrailPreset": "Standard",
                              "WriteCapabilities": ["container.restart"], "WriteTargets": ["web"],
                              "OwnerId": "{{owner}}" } ] }
            """);
        await Triggers.InitializeAsync();
    }

    public async Task<EndpointCheckDefinition> SeedCheckAsync(string owner, string name = "check") =>
        await Checks.SaveAsync(EndpointCheckTestDoubles.Draft(EndpointCheckKind.Tcp, "example.com", 443, name: name), owner, default);

    public Task SeedFleetAsync(string feature, string owner) => Fleet.SetAsync(feature, owner);

    /// <summary>Replaces <paramref name="path"/> with a directory: every read and write of it fails.</summary>
    public static void Break(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(path);
    }

    public static AgentPrincipal Editor(string email) =>
        new(AgentPrincipalKind.WebUser, email, McpPermissionLevels.Admin, null, UserEmail: email);

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp */ }
    }
}
