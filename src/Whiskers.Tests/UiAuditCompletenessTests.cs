using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-21: an infrastructure action in the UI is exactly one audit event — the gateway's — whether it ran, the page's
/// role check refused it, the policy refused it or the service reported a failure. The page writes no row of its own;
/// what it knows about the result (a backup file, the template it deployed) and the resolved target are in that event.
/// </summary>
public sealed class UiAuditCompletenessTests : IDisposable
{
    /// <summary>The infrastructure pages migrated in R1-21 (the configuration pages follow in R1-22).</summary>
    public static readonly string[] MigratedPages =
    [
        "Pages/Dashboard.razor", "Pages/ContainerDetail.razor", "Pages/Deploy.razor", "Pages/AppStore.razor",
        "Pages/ComposeEditor.razor", "Pages/Networks.razor", "Pages/Firewall.razor", "Pages/AddFirewallRuleDialog.razor",
        "Pages/NginxManager.razor", "Pages/SystemdManager.razor", "Pages/SslCerts.razor", "Pages/CloudView.razor",
        "Pages/GitDeployView.razor", "Pages/ScheduledTasksView.razor", "Pages/VolumeBackupsView.razor",
    ];

    private readonly ApprovalDatabase _db = new();
    private readonly GuardrailPolicy _policy;
    private readonly AgentToolTestHelpers.FixedGuardrailStore _store;

    public UiAuditCompletenessTests()
    {
        _policy = GuardrailPolicy.SafeDefault();
        _policy.ProtectedResources = [];
        _policy.ForbiddenArgPatterns = [];
        _store = new AgentToolTestHelpers.FixedGuardrailStore(_policy);
    }

    public void Dispose() => _db.Dispose();

    private sealed class FixedUser(AppRole role) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("ops@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    private UiMutationRunner Runner(AppRole role)
    {
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store),
            new AuditLogMutationSink(_db.Scopes), [], NullLogger<MutationGateway>.Instance,
            decisions: new DbPolicyDecisionStore(_db.Scopes),
            approvals: new ApprovalGate(new ApprovalStore(policy: _store, persistence: _db.Persistence()), _store),
            journal: new DbOperationJournal(_db.Scopes));
        return new UiMutationRunner(gateway, new CapabilityCatalog(), new FixedUser(role));
    }

    private static readonly object Web = new { containerId = "web", name = "web", serverId = "local" };

    private async Task<AuditLogEntity> TheEventAsync()
    {
        using var scope = _db.Scopes.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("outcome", row.Phase);
        Assert.Equal("Ui", row.Surface);
        Assert.Equal("user:ops@example.com", row.PrincipalId);
        // The resolved target, as columns of the event.
        Assert.Equal("container", row.TargetType);
        Assert.Equal("web", row.TargetId);
        Assert.Equal("local", row.ServerId);
        return row;
    }

    [Fact]
    public async Task A_successful_action_is_one_executed_event_with_its_details()
    {
        await Runner(AppRole.Operator).RunAsync("container.restart", Web, _ =>
        {
            MutationScope.Note("Restarted after the config change.");
            return Task.FromResult(true);
        });

        var row = await TheEventAsync();
        Assert.Equal("Executed", row.Outcome);
        Assert.True(row.Success);
        Assert.Contains("Restarted after the config change.", row.Details);
    }

    [Fact]
    public async Task A_role_refusal_on_the_page_is_one_denied_event_and_nothing_runs()
    {
        // What a page's RequireAsync does for a Viewer in front of its confirmation dialog.
        await Runner(AppRole.Viewer).RefuseAsync("container.restart", Web, "Requires the Operator role.");

        var row = await TheEventAsync();
        Assert.Equal("Denied", row.Outcome);
        Assert.False(row.Success);
        Assert.Equal("container.restart", row.Action);
        Assert.Contains("Operator role", row.Details);
        Assert.NotNull(row.OperationId);
    }

    [Fact]
    public async Task A_policy_refusal_is_one_denied_event()
    {
        _policy.ProtectedResources = ["web"];
        var ran = false;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Runner(AppRole.Admin).RunAsync("container.restart", Web,
            _ => { ran = true; return Task.FromResult(true); }));

        Assert.False(ran);
        Assert.Equal("Denied", (await TheEventAsync()).Outcome);
    }

    [Fact]
    public async Task The_gateways_own_level_check_is_one_denied_event_too()
    {
        // A page that leaves the role to the gateway (the firewall rule dialog): the same single event.
        await Assert.ThrowsAsync<MutationRefusedException>(() => Runner(AppRole.Viewer).RunAsync("container.restart", Web,
            _ => Task.FromResult(true)));

        Assert.Equal("Denied", (await TheEventAsync()).Outcome);
    }

    public sealed record DeployResult(bool Success, string Output);

    public static TheoryData<string> ReportedFailures() => ["bool", "tuple", "result object"];

    [Theory]
    [MemberData(nameof(ReportedFailures))]
    public async Task A_failure_the_service_reports_is_one_failed_event_not_a_success(string shape)
    {
        // The delegate returns normally: "it returned" must not count as success (R1-21).
        var runner = Runner(AppRole.Operator);
        object answer = shape switch
        {
            "bool" => await runner.RunAsync("container.restart", Web, _ => Task.FromResult(false)),
            "tuple" => await runner.RunAsync("container.restart", Web, _ => Task.FromResult((false, "exit 1"))),
            _ => await runner.RunAsync("container.restart", Web, _ => Task.FromResult(new DeployResult(false, "pull failed"))),
        };

        Assert.NotNull(answer);   // the page still gets the service's answer to show
        var row = await TheEventAsync();
        Assert.Equal("Failed", row.Outcome);
        Assert.False(row.Success);
    }

    [Fact]
    public async Task A_service_that_throws_is_one_failed_event()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(AppRole.Operator).RunAsync("container.restart",
            Web, _ => throw new InvalidOperationException("docker is gone")));

        var row = await TheEventAsync();
        Assert.Equal("Failed", row.Outcome);
        Assert.Contains("docker is gone", row.Details);
    }

    [Fact]
    public void A_result_that_says_it_succeeded_is_believed()
    {
        Assert.Null(MutationResults.FailureOf(new DeployResult(true, "")));
        Assert.NotNull(MutationResults.FailureOf(new DeployResult(false, "")));
        Assert.Null(MutationResults.FailureOf("a message"));
    }

    // ── The pages ─────────────────────────────────────────────────────────────

    private static string Components()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Whiskers", "Components")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Whiskers", "Components");
    }

    public static TheoryData<string> Pages()
    {
        var data = new TheoryData<string>();
        foreach (var page in MigratedPages) data.Add(page);
        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void A_migrated_page_writes_no_audit_row_of_its_own(string page)
    {
        var source = File.ReadAllText(Path.Combine(Components(), page));
        Assert.DoesNotContain("IAuditLogService", source);
        Assert.DoesNotContain("AuditLog.LogAsync", source);
    }

    /// <summary>Every role check in front of a mutation records its refusal (RequireAsync); a plain Require is left
    /// only where the handler changes nothing.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void A_role_check_in_front_of_a_mutation_records_its_refusal(string page)
    {
        var source = File.ReadAllText(Path.Combine(Components(), page));
        var reads = new Dictionary<string, string[]>
        {
            ["Pages/ContainerDetail.razor"] = ["CheckMigrationStatus"],
            ["Pages/NginxManager.razor"] = ["TestConfig"],
        }.GetValueOrDefault(page, []);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source,
                     @"private async Task (\w+)\([^)]*\)\s*\{\s*if \(!Require\(AppRole"))
            Assert.Contains(m.Groups[1].Value, reads);
    }
}
