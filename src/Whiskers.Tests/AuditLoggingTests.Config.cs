using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

/// <summary>
/// R1-22: a configuration or policy write is one gateway event that carries what changed — the section's version
/// before and after, and each changed setting with its old and new value, redacted. Secrets show only whether they are
/// set; URLs only their origin; a failed or refused write carries no change.
/// </summary>
public sealed partial class AuditLoggingTests : IDisposable
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private readonly ApprovalDatabase _db = new();
    private readonly AgentToolTestHelpers.FixedGuardrailStore _store;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-auditlog-{Guid.NewGuid():N}");

    public AuditLoggingTests()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        _store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private MutationGateway Gateway() =>
        new(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store), new AuditLogMutationSink(_db.Scopes), [],
            NullLogger<MutationGateway>.Instance, decisions: new DbPolicyDecisionStore(_db.Scopes),
            journal: new DbOperationJournal(_db.Scopes));

    private static MutationRequest Config(string capability, string section, string operationId) =>
        new(capability, Admin, MutationSurface.Ui, new MutationTarget("config", section, section, null),
            new Dictionary<string, JsonElement>(), OperationId: operationId);

    private async Task<List<JsonElement>> ResultsOf(string operationId)
    {
        using var scope = _db.Scopes.CreateScope();
        var row = Assert.Single(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.OperationId == operationId).ToListAsync());
        using var details = JsonDocument.Parse(row.Details!);
        return details.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(r => r.Clone()).ToList()
            : [];
    }

    private async Task<string> EventText(string operationId)
    {
        using var scope = _db.Scopes.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .SingleAsync(a => a.OperationId == operationId)).Details!;
    }

    private static string Text(JsonElement result, string property) => result.GetProperty(property).GetString() ?? "";

    private static JsonElement Setting(List<JsonElement> results, string targetId) =>
        Assert.Single(results, r => Text(r, "targetType") == ConfigChange.SettingTarget && Text(r, "targetId") == targetId);

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    [Fact]
    public async Task A_whitelist_write_records_its_versions_and_each_changed_setting()
    {
        var roles = new RoleService(EmptyConfig(), NullLogger<RoleService>.Instance, Path.Combine(_dir, "roles.json"));
        var whitelist = new WhitelistService(EmptyConfig(), roles, NullLogger<WhitelistService>.Instance, new DataPathOptions(_dir));
        await whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = false, Emails = ["a@example.com", "c@example.com"] });

        await Gateway().ExecuteAsync(Config("config.whitelist", "whitelist", "op-wl"), async (_, _) =>
        {
            await whitelist.SaveWhitelistAsync(new WhitelistData
                { Enabled = true, Emails = ["a@example.com", "b@example.com", "c@example.com"] });
            return 1;
        });

        var results = await ResultsOf("op-wl");
        var version = Assert.Single(results, r => Text(r, "action") == ConfigChange.VersionAction);
        Assert.Matches(@"^[0-9a-f]{12} → [0-9a-f]{12}; 2 changed$", Text(version, "details"));
        Assert.Equal("false → true", Text(Setting(results, "whitelist:Enabled"), "details"));
        // An address added in the middle is one change, not a shift of every address after it.
        Assert.Equal("(none) → present", Text(Setting(results, "whitelist:Emails[b@example.com]"), "details"));
    }

    [Fact]
    public async Task A_role_change_is_keyed_by_the_account()
    {
        var roles = new RoleService(EmptyConfig(), NullLogger<RoleService>.Instance, Path.Combine(_dir, "roles.json"));
        await roles.SetRoleAsync("ops@example.com", AppRole.Viewer);

        await Gateway().ExecuteAsync(Config("config.roles", "roles", "op-role"), async (_, _) =>
        {
            await roles.SetRoleAsync("ops@example.com", AppRole.Operator);
            return 1;
        });

        var setting = Setting(await ResultsOf("op-role"), "roles:Roles[ops@example.com].Role");
        Assert.Equal($"{(int)AppRole.Viewer} → {(int)AppRole.Operator}", Text(setting, "details"));
    }

    [Fact]
    public async Task A_created_api_key_leaves_no_key_material_in_the_event()
    {
        var mcp = new McpPermissionService(NullLogger<McpPermissionService>.Instance, new DataPathOptions(_dir));
        await mcp.InitializeAsync();
        McpApiKeyConfig? created = null;

        await Gateway().ExecuteAsync(Config("mcp.key_create", "mcp-permissions", "op-key"), async (_, _) =>
        {
            created = await mcp.AddApiKeyAsync("ci", McpPermissionLevels.Read);
            return 1;
        });

        Assert.NotNull(created);
        var text = await EventText("op-key");
        Assert.DoesNotContain(created!.Key!, text);
        Assert.DoesNotContain(created.KeyHash, text);
        Assert.DoesNotContain(created.Key![^4..], text.Replace(created.Id, ""));
        Assert.Contains("[set #", text);
    }

    [Fact]
    public async Task A_failed_write_carries_no_change()
    {
        var outcome = await Gateway().ExecuteAsync<int>(Config("config.whitelist", "whitelist", "op-fail"),
            (_, _) => throw new IOException("disk full"));

        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.DoesNotContain(await ResultsOf("op-fail"), r => Text(r, "action") == ConfigChange.VersionAction);
    }

    [Fact]
    public async Task A_refused_write_carries_no_change()
    {
        await Gateway().RefuseAsync(Config("config.roles", "roles", "op-refused"), "Requires the Admin role.");

        Assert.Empty(await ResultsOf("op-refused"));
    }

    private static Whiskers.Models.Agent.AgentPrincipal Editor() =>
        new(Whiskers.Models.Agent.AgentPrincipalKind.WebUser, "admin@example.com", McpPermissionLevels.Admin, null,
            UserEmail: "admin@example.com");

    [Fact]
    public async Task A_policy_write_records_the_changed_rule()
    {
        var guardrails = new Whiskers.Services.Guardrails.GuardrailStore(filePath: Path.Combine(_dir, "guardrails.json"));
        await guardrails.InitializeAsync();
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Write;

        await Gateway().ExecuteAsync(Config("guardrails.save", "guardrails", "op-policy"), async (_, _) =>
        {
            await guardrails.SaveAsync(policy, Editor());
            return 1;
        });

        var results = await ResultsOf("op-policy");
        Assert.Single(results, r => Text(r, "action") == ConfigChange.VersionAction && Text(r, "targetId") == "guardrails");
        Assert.Contains(results, r => Text(r, "targetType") == ConfigChange.SettingTarget
                                      && Text(r, "targetId").EndsWith("MaxAutonomousLevel", StringComparison.Ordinal)
                                      && Text(r, "details").EndsWith($"→ {McpPermissionLevels.Write}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_trigger_write_records_the_added_trigger()
    {
        var triggers = new Whiskers.Services.Agent.Triggers.AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        await triggers.InitializeAsync();

        await Gateway().ExecuteAsync(Config("aitrigger.save", "ai-triggers", "op-trigger"), async (_, _) =>
        {
            await triggers.SaveAsync([new AiTrigger { Id = "t1", Name = "restart on crash", EventTypes = ["unhealthy"] }],
                Editor(), triggers.Revision);
            return 1;
        });

        var results = await ResultsOf("op-trigger");
        Assert.Single(results, r => Text(r, "action") == ConfigChange.VersionAction && Text(r, "targetId") == "ai-triggers");
        Assert.Contains(results, r => Text(r, "targetId").StartsWith("ai-triggers:", StringComparison.Ordinal)
                                      && Text(r, "targetId").Contains("[t1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_git_app_write_records_the_changed_input()
    {
        var git = new Whiskers.Services.GitDeploy.GitDeployService(new ProtectedResourceResolutionTests.FakeHost(),
            new ProtectedResourceResolutionTests.NoVault(), new ComponentTests.UiCreatedResourceAuditTests.Stub<IComposePlanner>().Object,
            NullLogger<Whiskers.Services.GitDeploy.GitDeployService>.Instance, Path.Combine(_dir, "apps.json"));
        var app = await git.SaveAppAsync(new GitDeployApp
            { Id = "0123456789ab", Name = "shop", RepoUrl = "https://example.org/shop.git", Branch = "main", ServerId = "local" },
            token: null, "user:admin@example.com");
        app.Branch = "release";

        await Gateway().ExecuteAsync(Config("gitdeploy.save", "gitdeploy", "op-git"), async (_, _) =>
        {
            await git.SaveAppAsync(app, token: null, "user:admin@example.com");
            return 1;
        });

        var results = await ResultsOf("op-git");
        Assert.Equal("main → release", Text(Setting(results, "gitdeploy:0123456789ab:Branch"), "details"));
    }

    [Theory]
    [InlineData("webhook.save", "webhook")]
    [InlineData("logalert.create", "log-alert")]
    public async Task Refused_and_completed_writes_are_told_apart(string capability, string targetType)
    {
        var request = Config(capability, "x", "op-ok") with { Target = new MutationTarget(targetType, "x", "x", null) };
        await Gateway().ExecuteAsync(request, (_, _) =>
        {
            MutationScope.Note("Webhook wh1 created");
            return Task.FromResult(1);
        });
        await Gateway().RefuseAsync(request with { OperationId = "op-no" }, "Requires the Operator role.");

        using var scope = _db.Scopes.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.OperationId == "op-ok" || a.OperationId == "op-no").ToListAsync();
        Assert.Equal("Executed", rows.Single(r => r.OperationId == "op-ok").Outcome);
        Assert.Equal("Denied", rows.Single(r => r.OperationId == "op-no").Outcome);
        Assert.Contains(await ResultsOf("op-ok"), r => Text(r, "details") == "Webhook wh1 created");
    }

    // ---- ConfigChange in isolation ------------------------------------------------------------------------------

    private sealed record Notifier(string Name, string WebhookUrl, string? Password, string Homeserver);

    [Fact]
    public void Secrets_show_only_whether_they_are_set_and_a_secret_alone_keeps_the_version()
    {
        var (before, v1) = ConfigChange.Describe(new Notifier("ops", "https://hooks.example.com/T0/B0/xyzsecret", "hunter2", "https://matrix.example.com"));
        var (after, v2) = ConfigChange.Describe(new Notifier("ops", "https://hooks.example.com/T0/B0/xyzsecret", "correct-horse", "https://matrix.example.com"));

        Assert.Equal(v1, v2);
        var change = Assert.Single(ConfigChange.Diff(before, after));
        Assert.Equal("Password", change.Path);
        Assert.Matches(@"^\[set #[0-9a-f]{6}\]$", change.Before);
        Assert.Matches(@"^\[set #[0-9a-f]{6}\]$", change.After);
        Assert.NotEqual(change.Before, change.After);
        var all = string.Join("|", before.Values.Concat(after.Values));
        Assert.DoesNotContain("hunter2", all);
        Assert.DoesNotContain("correct-horse", all);
        // A URL shows its origin only — a webhook carries its secret in the path.
        Assert.Equal("https://hooks.example.com/…", before["WebhookUrl"]);
        Assert.DoesNotContain("xyzsecret", all);
        Assert.Equal("[not set]", ConfigChange.Describe(new Notifier("ops", "", null, "")).Settings["Password"]);
    }

    [Fact]
    public void A_value_change_moves_the_version_and_equal_content_has_one_version()
    {
        var a = ConfigChange.Describe(new { Enabled = true, Interval = 30 });
        var b = ConfigChange.Describe(new { Enabled = true, Interval = 60 });
        var c = ConfigChange.Describe(new { Interval = 30, Enabled = true });

        Assert.NotEqual(a.Version, b.Version);
        Assert.Equal(a.Version, c.Version);
        Assert.Matches("^[0-9a-f]{12}$", a.Version);
    }

    [Fact]
    public async Task More_changes_than_the_cap_are_counted_not_dropped()
    {
        // Long values, so the event's details have to shrink: the version entry must survive whole.
        var before = Enumerable.Range(0, 30).ToDictionary(i => $"setting.with.a.long.path.number{i:00}",
            _ => string.Join(" ", Enumerable.Repeat("old", 30)));
        var after = before.ToDictionary(p => p.Key, _ => string.Join(" ", Enumerable.Repeat("new", 30)));

        await Gateway().ExecuteAsync(Config("config.whitelist", "whitelist", "op-many"), (_, _) =>
        {
            ConfigChange.Record("bulk", before, after);
            return Task.FromResult(1);
        });

        var results = await ResultsOf("op-many");
        Assert.Matches(@"^[0-9a-f]{12} → [0-9a-f]{12}; 30 changed$",
            Text(Assert.Single(results, r => Text(r, "action") == ConfigChange.VersionAction), "details"));
        Assert.Equal(ConfigChange.MaxChanges, results.Count(r => Text(r, "targetType") == ConfigChange.SettingTarget
                                                              && Text(r, "targetId") != "bulk:…"));
        Assert.Contains(results, r => Text(r, "details") == $"{30 - ConfigChange.MaxChanges} further setting(s) changed");
    }

    [Fact]
    public void Outside_a_gateway_scope_nothing_is_recorded()
    {
        Assert.Null(MutationScope.Current);
        Assert.False(ConfigChange.Record("whitelist", null, new WhitelistData()));
    }

    [Fact]
    public void Long_values_are_cut_and_credentials_in_a_url_are_removed()
    {
        var settings = ConfigChange.Describe(new
            { Description = string.Join(" ", Enumerable.Repeat("plain", 100)), Note = "see https://user:pw@host/x" }).Settings;

        Assert.Equal(ConfigChange.MaxValueChars + 1, settings["Description"].Length);
        Assert.DoesNotContain("pw@", settings["Note"]);
    }
}
