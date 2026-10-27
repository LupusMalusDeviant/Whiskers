using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Mutations;
using static Whiskers.Tests.AutomationMutationRunnerTests;

namespace Whiskers.Tests;

/// <summary>
/// R1-08 (S-05/U-29, INV-04/20/45): a Git deployment has an owner whose authority a webhook deploy runs on,
/// re-checked every time; the decision is made on the stored app (its name, server and revision) and the deployment
/// runs exactly that snapshot — an edit in between is refused; every container of its compose plan is decided; and
/// an unauthorised caller is refused before anything is cloned, built or brought up.
/// </summary>
public sealed class GitDeployMutationTests : IDisposable
{
    private const string Admin = "admin@example.com";
    private static readonly string AdminId = PrincipalIds.User(Admin);
    private const string AppId = "0123456789ab";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gitdeploy-{Guid.NewGuid():N}");
    private readonly ProtectedResourceResolutionTests.FakeHost _host = new();
    private readonly FakeDocker _docker = new(
        Compose("payments-web-1", "payments", "web"),
        Compose("payments-db", "payments", "db"));
    private readonly FakeServerConfig _servers = new(
        new ServerConfig { Id = "local", Name = "Local", IsDefault = true }, new ServerConfig { Id = "edge01", Name = "Edge" });
    private readonly Roles _roles = new();
    private readonly Whitelist _whitelist = new();
    private readonly AutomationOptions _options = new() { RequireOwner = true };

    public GitDeployMutationTests()
    {
        Directory.CreateDirectory(_root);
        _whitelist.Allowed.Add(Admin);
        _roles.Map[Admin] = AppRole.Admin;
        _host.Files[$"/opt/whiskers-git/{AppId}/repo/docker-compose.yml"] =
            "name: payments\nservices:\n  web:\n    image: nginx\n";
    }

    // ---------------------------------------------------------------- webhook: the owner's authority

    [Fact]
    public async Task A_webhook_deploy_runs_on_the_owner_authority_and_decides_every_planned_container()
    {
        var git = await AppAsync();

        var (success, output) = await GitDeployOwners.DeployAsOwnerAsync(git, Automation(), AppId);

        Assert.True(success, output);
        Assert.Contains(_host.Executed, c => c.Contains("-p 'payments'") && c.EndsWith("up -d --remove-orphans"));
    }

    [Fact]
    public async Task A_protected_orphan_of_the_plan_stops_the_webhook_deploy_before_the_build()
    {
        var git = await AppAsync();

        var (success, _) = await GitDeployOwners.DeployAsOwnerAsync(git, Automation(protect: "payments-db"), AppId);

        Assert.False(success);
        Assert.DoesNotContain(_host.Executed, c => c.Contains("docker compose"));
    }

    [Theory]
    [InlineData(AppRole.Operator)]
    [InlineData(AppRole.Viewer)]
    public async Task An_owner_who_lost_the_authority_is_refused_before_anything_is_fetched(AppRole now)
    {
        var git = await AppAsync();
        _roles.Map[Admin] = now;

        var (success, _) = await GitDeployOwners.DeployAsOwnerAsync(git, Automation(), AppId);

        Assert.False(success);
        Assert.Empty(_host.Executed);   // no clone, no build, no up
    }

    [Fact]
    public async Task An_owner_removed_from_the_whitelist_is_refused_too()
    {
        var git = await AppAsync();
        _whitelist.Allowed.Remove(Admin);

        var (success, _) = await GitDeployOwners.DeployAsOwnerAsync(git, Automation(), AppId);

        Assert.False(success);
        Assert.Empty(_host.Executed);
    }

    [Fact]
    public async Task A_legacy_app_needs_an_admin_to_take_it_over_before_a_webhook_deploys_it()
    {
        // Saved before owners were recorded: no owner, revision 0.
        await File.WriteAllTextAsync(StorePath, JsonSerializer.Serialize(new GitDeployData
        {
            Apps = [new GitDeployApp { Id = AppId, Name = "payments", RepoUrl = "https://example.org/p.git", ServerId = "local" }],
        }));
        var git = Service();

        var (before, message) = await GitDeployOwners.DeployAsOwnerAsync(git, Automation(), AppId);
        Assert.False(before);
        Assert.Contains("no owner", message);
        Assert.Empty(_host.Executed);

        Assert.True(await git.SetOwnerAsync(AppId, AdminId, expectedOwnerId: null));
        Assert.False(await git.SetOwnerAsync(AppId, PrincipalIds.User("other@example.com"), expectedOwnerId: null));

        var (after, output) = await GitDeployOwners.DeployAsOwnerAsync(git, Automation(), AppId);
        Assert.True(after, output);
    }

    // ---------------------------------------------------------------- one snapshot from decision to execution

    [Fact]
    public async Task An_edit_after_the_decision_is_refused_before_the_clone()
    {
        var git = await AppAsync();
        var decided = (await git.GetAppAsync(AppId))!;
        var edit = decided.Clone();
        edit.Branch = "evil";
        await git.SaveAppAsync(edit, token: null, AdminId);

        var (success, output) = await git.DeployAsync(decided, (_, proceed) => proceed());

        Assert.False(success);
        Assert.Contains("changed", output);
        Assert.Empty(_host.Executed);
    }

    [Fact]
    public async Task A_snapshot_with_other_inputs_than_the_stored_app_is_refused()
    {
        var git = await AppAsync();
        var forged = (await git.GetAppAsync(AppId))!;
        forged.RepoUrl = "https://example.org/other.git";   // same revision, different repository

        var (success, _) = await git.DeployAsync(forged, (_, proceed) => proceed());

        Assert.False(success);
        Assert.Empty(_host.Executed);
    }

    [Fact]
    public async Task A_save_from_a_stale_copy_is_refused()
    {
        var git = await AppAsync();
        var first = (await git.GetAppAsync(AppId))!;
        var second = first.Clone();
        first.Branch = "a";
        await git.SaveAppAsync(first, token: null, AdminId);
        second.Branch = "b";

        await Assert.ThrowsAsync<GitDeployConflictException>(() => git.SaveAppAsync(second, token: null, AdminId));
        Assert.Equal("a", (await git.GetAppAsync(AppId))!.Branch);
    }

    [Fact]
    public async Task Whoever_saves_the_app_owns_it_and_a_caller_cannot_change_the_stored_copy()
    {
        var git = await AppAsync();
        var copy = (await git.GetAppAsync(AppId))!;
        copy.ServerId = "edge01";   // edited without saving

        var stored = (await git.GetAppAsync(AppId))!;
        Assert.Equal(("local", AdminId), (stored.ServerId, stored.Owner));
    }

    // ---------------------------------------------------------------- the decision: stored app, its server

    [Fact]
    public async Task The_decision_is_made_on_the_stored_app_and_its_server()
    {
        await AppAsync();

        var (outcome, seen) = await DecideAsync(new { appId = AppId, name = "something else", serverId = "local", revision = 1 });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(("payments", "local"), (seen!.Name, seen.ServerId));
    }

    [Theory]
    [InlineData("edge01", 1)]   // another server than the app's
    [InlineData("local", 0)]    // a revision that is no longer stored
    public async Task A_decision_on_other_inputs_than_the_stored_app_is_refused(string serverId, int revision)
    {
        await AppAsync();

        var (outcome, _) = await DecideAsync(new { appId = AppId, serverId, revision });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Fact]
    public async Task An_unknown_app_is_refused_before_policy()
    {
        var (outcome, _) = await DecideAsync(new { appId = "fedcba987654", serverId = "local" });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetUnknown, outcome.ReasonCode);
    }

    [Fact]
    public async Task A_person_without_admin_is_refused_before_anything_is_cloned()
    {
        var git = await AppAsync();
        var app = (await git.GetAppAsync(AppId))!;
        var runner = UiRunner(AppRole.Operator);

        await Assert.ThrowsAsync<MutationRefusedException>(() => runner.RunAsync("gitdeploy.deploy",
            new { appId = app.Id, name = app.Name, serverId = app.ServerId, revision = app.Revision },
            _ => git.DeployAsync(app, runner.GateFor("gitdeploy.deploy"))));

        Assert.Empty(_host.Executed);
    }

    [Fact]
    public async Task The_page_deploys_the_snapshot_it_decided_on_with_every_planned_container_decided()
    {
        var git = await AppAsync();
        var app = (await git.GetAppAsync(AppId))!;
        var runner = UiRunner(AppRole.Admin);

        var (success, output) = await runner.RunAsync("gitdeploy.deploy",
            new { appId = app.Id, name = app.Name, serverId = app.ServerId, revision = app.Revision },
            _ => git.DeployAsync(app, runner.GateFor("gitdeploy.deploy")));

        Assert.True(success, output);
        Assert.Contains(_host.Executed, c => c.EndsWith("up -d --remove-orphans"));
    }

    // ---------------------------------------------------------------- helpers

    private string StorePath => Path.Combine(_root, "apps.json");

    private GitDeployService Service() => new(_host, new ProtectedResourceResolutionTests.NoVault(),
        new ComposePlanner(_docker, _host), NullLogger<GitDeployService>.Instance, StorePath);

    private GitDeployService? _git;

    private async Task<GitDeployService> AppAsync()
    {
        _git = Service();
        await _git.SaveAppAsync(new GitDeployApp
        {
            Id = AppId, Name = "payments", RepoUrl = "https://example.org/p.git", ServerId = "local",
        }, token: null, AdminId);
        return _git;
    }

    private MutationTargetResolver Resolver() => new(new TestServices(_docker, _servers, _git ?? Service()));

    private MutationGateway Gateway(string? protect = null)
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        policy.ProtectedResources = protect is null ? [] : [protect];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        return new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            targets: Resolver(), approvals: new ApprovalGate(new ApprovalStore(), store));
    }

    private AutomationMutationRunner Automation(string? protect = null) => new(Gateway(protect), new CapabilityCatalog(),
        new AutomationOwnerResolver(_roles, _whitelist, new Keys(), new Accounts()),
        new StaticOptionsMonitor<AutomationOptions>(_options));

    private UiMutationRunner UiRunner(AppRole role) => new(Gateway(), new CapabilityCatalog(), new Person(role));

    private async Task<(MutationOutcome<int> Outcome, MutationTarget? Seen)> DecideAsync(object arguments)
    {
        var catalog = new CapabilityCatalog();
        var args = JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        MutationTarget? seen = null;
        var principal = new MutationPrincipal(MutationPrincipalKind.Human, Admin, Admin, McpPermissionLevels.Admin);
        var outcome = await Gateway().ExecuteAsync(
            new MutationRequest("gitdeploy.deploy", principal, MutationSurface.Ui,
                MutationTargets.FromArguments(catalog.Find("gitdeploy.deploy")!, args), args),
            (ctx, _) => { seen = ctx.Request.Target; return Task.FromResult(0); });
        return (outcome, seen);
    }

    private static ContainerInfo Compose(string name, string project, string service) => new()
    {
        Id = name + "-id", Name = name, ServerId = "local", State = "running",
        Labels = new() { ["com.docker.compose.project"] = project, ["com.docker.compose.service"] = service },
    };

    private sealed class Person(AppRole role) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Admin);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}
