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

    private T Stub<T>() where T : class => new ComponentTests.UiCreatedResourceAuditTests.Stub<T>().Object;

    [Fact]
    public async Task A_webhook_lifecycle_records_each_change_and_a_rotated_secret_only_as_fingerprints()
    {
        var hooks = new Whiskers.Services.Webhooks.WebhookService(_db.Scopes, Stub<Whiskers.Services.Docker.IDockerService>(),
            Stub<Whiskers.Services.Server.IHostCommandExecutor>(), Stub<IComposePlanner>(),
            NullLogger<Whiskers.Services.Webhooks.WebhookService>.Instance);
        WebhookEntity? hook = null;
        string? secret = null;

        await Gateway().ExecuteAsync(Config("webhook.save", "webhook", "op-hook-new"), async (_, _) =>
        {
            hook = await hooks.CreateWebhookAsync(new WebhookEntity
                { Name = "redeploy", TargetId = "web", Action = "restart", Owner = "user:admin@example.com" });
            return 1;
        });
        await Gateway().ExecuteAsync(Config("webhook.rotate_secret", "webhook", "op-hook-rotate"), async (_, _) =>
        {
            secret = await hooks.RegenerateSecretAsync(hook!.WebhookId);
            return 1;
        });
        await Gateway().ExecuteAsync(Config("webhook.save", "webhook", "op-hook-take"), async (_, _) =>
            await hooks.SetOwnerAsync(hook!.WebhookId, "user:ops@example.com", "user:admin@example.com"));
        await Gateway().ExecuteAsync(Config("webhook.set_enabled", "webhook", "op-hook-off"), async (_, _) =>
        {
            await hooks.SetEnabledAsync(hook!.WebhookId, false);
            return 1;
        });
        await Gateway().ExecuteAsync(Config("webhook.delete", "webhook", "op-hook-del"), async (_, _) =>
        {
            await hooks.DeleteWebhookAsync(hook!.WebhookId);
            return 1;
        });

        var section = $"webhook:{hook!.WebhookId}";
        Assert.Equal("(none) → redeploy", Text(Setting(await ResultsOf("op-hook-new"), $"{section}:Name"), "details"));
        var rotated = Text(Setting(await ResultsOf("op-hook-rotate"), $"{section}:Secret"), "details");
        Assert.Matches(@"^\[set #[0-9a-f]{6}\] → \[set #[0-9a-f]{6}\]$", rotated);
        Assert.Equal("true → false", Text(Setting(await ResultsOf("op-hook-off"), $"{section}:Enabled"), "details"));
        Assert.Equal("redeploy → (none)", Text(Setting(await ResultsOf("op-hook-del"), $"{section}:Name"), "details"));
        Assert.Equal("user:admin@example.com → user:ops@example.com",
            Text(Setting(await ResultsOf("op-hook-take"), $"{section}:Owner"), "details"));
        var steps = new[] { "op-hook-new", "op-hook-rotate", "op-hook-take", "op-hook-off", "op-hook-del" };
        await AssertVersionsInOrder(steps);
        foreach (var op in steps)
            Assert.DoesNotContain(secret!, await EventText(op));
    }

    [Fact]
    public async Task A_log_alert_lifecycle_records_each_change_and_its_pattern_only_as_text()
    {
        var monitor = new Whiskers.Services.LogMonitor.LogMonitorService(_db.Scopes, Stub<Whiskers.Services.Docker.IDockerService>(),
            Stub<Whiskers.Services.ServerConfig.IServerConfigService>(), Stub<Whiskers.Services.Notifications.INotificationService>(),
            NullLogger<Whiskers.Services.LogMonitor.LogMonitorService>.Instance, Stub<Whiskers.Services.Docker.Budget.IServerBudget>(),
            Stub<Whiskers.Services.Observability.SelfMetrics.ISelfMetrics>(), Stub<Whiskers.Services.LogMonitor.Hygiene.ILogScanExclusions>(),
            Stub<Whiskers.Services.Observability.Outcomes.IActionOutcomeService>());
        const string pattern = "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.leaked";
        LogAlertRuleEntity? rule = null;

        await Gateway().ExecuteAsync(Config("logalert.create", "log-alert", "op-rule-new"), async (_, _) =>
        {
            rule = await monitor.CreateRuleAsync(new LogAlertRuleEntity { Name = "leak", Pattern = pattern, ContainerName = "web" });
            return 1;
        });
        await Gateway().ExecuteAsync(Config("logalert.toggle", "log-alert", "op-rule-off"), async (_, _) =>
        {
            await monitor.ToggleRuleAsync(rule!.RuleId, false);
            return 1;
        });
        await Gateway().ExecuteAsync(Config("logalert.delete", "log-alert", "op-rule-del"), async (_, _) =>
        {
            await monitor.DeleteRuleAsync(rule!.RuleId);
            return 1;
        });

        var section = $"log-alert:{rule!.RuleId}";
        // A new rule changes more settings than one event shows; the pattern, when shown, is text only.
        Assert.Matches(@"^\[text, \d+ chars #[0-9a-f]{6}\]$", ConfigChange.Describe(rule).Shown["Pattern"]);
        Assert.Contains(await ResultsOf("op-rule-new"), r => Text(r, "action") == ConfigChange.VersionAction
                                                             && Text(r, "targetId") == section);
        Assert.Equal("true → false", Text(Setting(await ResultsOf("op-rule-off"), $"{section}:Enabled"), "details"));
        Assert.Contains(await ResultsOf("op-rule-del"), r => Text(r, "action") == ConfigChange.VersionAction
                                                             && Text(r, "targetId") == section
                                                             && Text(r, "details").EndsWith("13 changed", StringComparison.Ordinal));
        foreach (var op in new[] { "op-rule-new", "op-rule-off", "op-rule-del" })
            Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", await EventText(op));
    }

    [Fact]
    public async Task A_key_permission_change_is_readable_while_the_key_material_stays_hidden()
    {
        var mcp = new McpPermissionService(NullLogger<McpPermissionService>.Instance, new DataPathOptions(_dir));
        await mcp.InitializeAsync();
        var key = await mcp.AddApiKeyAsync("ci", McpPermissionLevels.Read);
        var data = mcp.GetPermissionData();
        data.ApiKeys.Single(k => k.Id == key.Id).PermissionLevel = McpPermissionLevels.Write;

        await Gateway().ExecuteAsync(Config("config.mcp_permissions", "mcp-permissions", "op-level"), async (_, _) =>
        {
            await mcp.SavePermissionDataAsync(data);
            return 1;
        });

        var results = await ResultsOf("op-level");
        Assert.Equal($"{McpPermissionLevels.Read} → {McpPermissionLevels.Write}",
            Text(Setting(results, $"mcp-permissions:ApiKeys[{key.Id}].PermissionLevel"), "details"));
        Assert.Matches(@"^[0-9a-f]{12} → [0-9a-f]{12}; 1 changed$",
            Text(Assert.Single(results, r => Text(r, "action") == ConfigChange.VersionAction), "details"));
        var text = await EventText("op-level");
        Assert.DoesNotContain(key.KeyHash, text);
    }

    [Fact]
    public async Task Free_text_holding_a_webhook_url_never_reaches_the_event()
    {
        var triggers = new Whiskers.Services.Agent.Triggers.AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        await triggers.InitializeAsync();
        const string prompt = "On crash run curl https://hooks.slack.com/services/T0/B0/zzsecretpath and restart";

        await Gateway().ExecuteAsync(Config("aitrigger.save", "ai-triggers", "op-prompt"), async (_, _) =>
        {
            await triggers.SaveAsync([new AiTrigger { Id = "t1", Name = "crash", EventTypes = ["unhealthy"], Prompt = prompt }],
                Editor(), triggers.Revision);
            return 1;
        });

        var text = await EventText("op-prompt");
        Assert.DoesNotContain("zzsecretpath", text);
        Assert.DoesNotContain("hooks.slack.com/services", text);
        Assert.Matches(@"^\[text, \d+ chars #[0-9a-f]{6}\]$",
            ConfigChange.Describe(new AiTrigger { Prompt = prompt }).Shown["Prompt"]);
    }

    [Fact]
    public async Task A_save_of_many_sections_keeps_every_version_and_counts_what_it_leaves_out()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "appsettings.json"));
        var sections = Enumerable.Range(1, 6).ToDictionary(n => $"Channel{n}",
            n => (object)System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(
                Enumerable.Range(0, 6).ToDictionary(i => $"Setting{i}", i => $"value-{n}-{i}")))!);

        await Gateway().ExecuteAsync(Config("config.save", "settings", "op-sections"), async (_, _) =>
        {
            await store.SaveSectionsAsync(sections);
            return 1;
        });

        var results = await ResultsOf("op-sections");
        var versions = results.Where(r => Text(r, "action") == ConfigChange.VersionAction).ToList();
        Assert.Equal(6, versions.Count);
        Assert.All(versions, v => Assert.Matches(@"^[0-9a-f]{12} → [0-9a-f]{12}; 6 changed$", Text(v, "details")));
        var shown = results.Count(r => Text(r, "targetType") == ConfigChange.SettingTarget && !Text(r, "targetId").EndsWith(":…", StringComparison.Ordinal));
        Assert.Equal(ConfigChange.MaxChanges, shown);
        var counted = results.Where(r => Text(r, "targetId").EndsWith(":…", StringComparison.Ordinal))
            .Sum(r => int.Parse(Text(r, "details").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(36 - ConfigChange.MaxChanges, counted);
    }

    [Fact]
    public async Task A_registry_host_with_user_info_is_refused_and_never_reaches_the_event()
    {
        var registries = new Whiskers.Services.Registries.RegistryConfigService(
            new ProtectedResourceResolutionTests.NoVault(), Path.Combine(_dir, "registries.json"));

        RegistryConfig WithUserInfo() => new() { Id = "r1", Name = "ci", Host = "https://ci:owl42@registry.example" };
        await Assert.ThrowsAsync<ArgumentException>(() => registries.SaveRegistryAsync(WithUserInfo(), credential: null));
        await Gateway().ExecuteAsync(Config("registry.save", "registries", "op-reg"), async (_, _) =>
            await registries.SaveRegistryAsync(WithUserInfo(), credential: null));

        Assert.Empty(await registries.GetRegistriesAsync());
        Assert.DoesNotContain("owl42", await EventText("op-reg"));
        // A host stored before the check is still never shown or versioned.
        var legacy = ConfigChange.Describe(new { Host = "ci:owl42@registry.example" });
        Assert.DoesNotContain(legacy.Settings.Values, v => v.Shown.Contains("owl") || v.Versioned.Contains("owl"));
        Assert.Equal(ConfigChange.Describe(new { Host = "ci:other@registry.example" }).Version, legacy.Version);
    }

    [Fact]
    public async Task An_ntfy_topic_is_a_secret_in_the_event()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "appsettings.json"));
        await store.SaveSectionAsync(NtfySettings.SectionName, new NtfySettings { Topic = "alerts-q7wz" });

        await Gateway().ExecuteAsync(Config("config.save", "settings", "op-topic"), async (_, _) =>
        {
            await store.SaveSectionAsync(NtfySettings.SectionName, new NtfySettings { Topic = "alerts-m3vx" });
            return 1;
        });

        var results = await ResultsOf("op-topic");
        Assert.Matches(@"^\[set #[0-9a-f]{6}\] → \[set #[0-9a-f]{6}\]$", Text(Setting(results, "Ntfy:Topic"), "details"));
        // A topic-only rotation keeps the version: no hash of the topic is stored.
        Assert.Matches(@"^([0-9a-f]{12}) → \1; 1 changed$",
            Text(Assert.Single(results, r => Text(r, "action") == ConfigChange.VersionAction), "details"));
        var text = await EventText("op-topic");
        Assert.DoesNotContain("q7wz", text);
        Assert.DoesNotContain("m3vx", text);
    }

    [Fact]
    public async Task A_change_of_the_first_of_two_entries_for_one_account_is_recorded()
    {
        var roles = new RoleService(EmptyConfig(), NullLogger<RoleService>.Instance, Path.Combine(_dir, "roles.json"));
        await roles.SaveRoleDataAsync(new UserRoleData { Roles =
        [
            new() { Email = "ops@example.com", Role = AppRole.Viewer },
            new() { Email = "ops@example.com", Role = AppRole.Viewer },
        ] });

        await Gateway().ExecuteAsync(Config("config.roles", "roles", "op-dup"), async (_, _) =>
        {
            await roles.SaveRoleDataAsync(new UserRoleData { Roles =
            [
                new() { Email = "ops@example.com", Role = AppRole.Admin },
                new() { Email = "ops@example.com", Role = AppRole.Viewer },
            ] });
            return 1;
        });

        var results = await ResultsOf("op-dup");
        Assert.Equal($"{(int)AppRole.Viewer} → {(int)AppRole.Admin}",
            Text(Setting(results, "roles:Roles[ops@example.com].Role"), "details"));
        Assert.Matches(@"; 1 changed$", Text(Assert.Single(results, r => Text(r, "action") == ConfigChange.VersionAction), "details"));
        Assert.Contains("Roles[ops@example.com~2].Role", ConfigChange.Describe(roles.GetRoleData()).Settings.Keys);
    }

    /// <summary>Each event's version entry as (from, to); the saves of one section must form one chain from
    /// <paramref name="start"/>, so no event describes a state another save had already replaced.</summary>
    private async Task AssertOneVersionChain(string start, IEnumerable<string> operations)
    {
        var edges = new List<(string From, string To)>();
        foreach (var op in operations)
        {
            var details = Text(Assert.Single(await ResultsOf(op), r => Text(r, "action") == ConfigChange.VersionAction), "details");
            var match = System.Text.RegularExpressions.Regex.Match(details, "^([0-9a-f]{12}) → ([0-9a-f]{12});");
            Assert.True(match.Success, details);
            edges.Add((match.Groups[1].Value, match.Groups[2].Value));
        }
        var current = start;
        while (edges.Count > 0)
        {
            var next = Assert.Single(edges, e => e.From == current);
            edges.Remove(next);
            current = next.To;
        }
    }

    /// <summary>The steps of one section, in the order they ran: each starts at the version the one before ended at.</summary>
    private async Task AssertVersionsInOrder(IEnumerable<string> operations)
    {
        string? previous = null;
        foreach (var op in operations)
        {
            var details = Text(Assert.Single(await ResultsOf(op), r => Text(r, "action") == ConfigChange.VersionAction), "details");
            var match = System.Text.RegularExpressions.Regex.Match(details, "^([0-9a-f]{12}) → ([0-9a-f]{12});");
            Assert.True(match.Success, details);
            if (previous is not null) Assert.Equal(previous, match.Groups[1].Value);
            previous = match.Groups[2].Value;
        }
    }

    private sealed class MemoryVault : Whiskers.Services.Vault.IVaultService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _secrets = new();
        public bool IsEnabled => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<VaultEntry> ListSecrets() => _secrets.Keys.Select(k => new VaultEntry { Key = k }).ToList();
        public Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null,
            int? rotateAfterDays = null) { _secrets[key] = plainValue; return Task.CompletedTask; }
        public string? GetSecret(string key) => _secrets.TryGetValue(key, out var v) ? v : null;
        public Task DeleteSecretAsync(string key) { _secrets.TryRemove(key, out _); return Task.CompletedTask; }
        public List<VaultEntry> GetExpiringSecrets() => [];
    }

    [Fact]
    public async Task A_git_token_rotation_is_a_change_and_neither_it_nor_a_url_path_moves_the_version()
    {
        var git = new Whiskers.Services.GitDeploy.GitDeployService(new ProtectedResourceResolutionTests.FakeHost(),
            new MemoryVault(), Stub<IComposePlanner>(),
            NullLogger<Whiskers.Services.GitDeploy.GitDeployService>.Instance, Path.Combine(_dir, "apps.json"));
        var app = await git.SaveAppAsync(new GitDeployApp
            { Id = "0123456789ab", Name = "shop", RepoUrl = "https://example.org/team/shop.git", ServerId = "local" },
            token: "tok-first-q7", "user:admin@example.com");

        await Gateway().ExecuteAsync(Config("gitdeploy.save", "gitdeploy", "op-git-token"), async (_, _) =>
        {
            app = await git.SaveAppAsync(app, token: "tok-second-m3", "user:admin@example.com");
            return 1;
        });
        app.RepoUrl = "https://example.org/team/shop-renamed.git";
        await Gateway().ExecuteAsync(Config("gitdeploy.save", "gitdeploy", "op-git-path"), async (_, _) =>
        {
            app = await git.SaveAppAsync(app, token: null, "user:admin@example.com");
            return 1;
        });

        var token = await ResultsOf("op-git-token");
        Assert.Matches(@"^\[set #[0-9a-f]{6}\] → \[set #[0-9a-f]{6}\]$", Text(Setting(token, "gitdeploy:0123456789ab:Token"), "details"));
        Assert.Single(await ResultsOf("op-git-path"), r => Text(r, "targetId") == "gitdeploy:0123456789ab:RepoUrl");
        foreach (var op in new[] { "op-git-token", "op-git-path" })
        {
            Assert.Matches(@"^([0-9a-f]{12}) → \1; 1 changed$",
                Text(Assert.Single(await ResultsOf(op), r => Text(r, "action") == ConfigChange.VersionAction), "details"));
            Assert.DoesNotContain("tok-", await EventText(op));
        }
        await AssertVersionsInOrder(["op-git-token", "op-git-path"]);
    }

    /// <summary>A vault whose next write waits until <see cref="Release"/>; counts writes.</summary>
    private sealed class GatedVault : Whiskers.Services.Vault.IVaultService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _secrets = new();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Gated { get; set; }
        public int Writes;
        public void Release() => _gate.TrySetResult();
        public bool IsEnabled => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<VaultEntry> ListSecrets() => [];
        public async Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null,
            int? rotateAfterDays = null)
        {
            Interlocked.Increment(ref Writes);
            if (Gated)
            {
                Entered.TrySetResult();
                await _gate.Task;
            }
            _secrets[key] = plainValue;
        }
        public string? GetSecret(string key) => _secrets.TryGetValue(key, out var v) ? v : null;
        public Task DeleteSecretAsync(string key) { _secrets.TryRemove(key, out _); return Task.CompletedTask; }
        public List<VaultEntry> GetExpiringSecrets() => [];
    }

    [Fact]
    public async Task Of_two_overlapping_git_saves_the_loser_never_touches_the_token()
    {
        var vault = new GatedVault();
        var git = new Whiskers.Services.GitDeploy.GitDeployService(new ProtectedResourceResolutionTests.FakeHost(),
            vault, Stub<IComposePlanner>(),
            NullLogger<Whiskers.Services.GitDeploy.GitDeployService>.Instance, Path.Combine(_dir, "apps.json"));
        var app = await git.SaveAppAsync(new GitDeployApp
            { Id = "0123456789ab", Name = "shop", RepoUrl = "https://example.org/shop.git", ServerId = "local" },
            token: "tok-zero-k1", "user:admin@example.com");
        vault.Gated = true;

        var first = Gateway().ExecuteAsync(Config("gitdeploy.save", "gitdeploy", "op-git-a"), async (_, _) =>
            await git.SaveAppAsync(app.Clone(), token: "tok-alpha-k2", "user:admin@example.com"));
        await vault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the first save holds the app, inside the vault
        var second = git.SaveAppAsync(app.Clone(), token: "tok-beta-k3", "user:admin@example.com");
        vault.Release();
        await first;

        await Assert.ThrowsAsync<Whiskers.Services.GitDeploy.GitDeployConflictException>(() => second);
        Assert.Equal(2, vault.Writes);   // the initial save and the winner; the loser wrote nothing
        Assert.Equal("tok-alpha-k2", vault.GetSecret("git-token:0123456789ab"));
        Assert.Matches(@"^\[set #[0-9a-f]{6}\] → \[set #[0-9a-f]{6}\]$",
            Text(Setting(await ResultsOf("op-git-a"), "gitdeploy:0123456789ab:Token"), "details"));
    }

    [Fact]
    public async Task Overlapping_webhook_takeover_and_toggle_each_record_the_change_they_made()
    {
        var hooks = new Whiskers.Services.Webhooks.WebhookService(_db.Scopes, Stub<Whiskers.Services.Docker.IDockerService>(),
            Stub<Whiskers.Services.Server.IHostCommandExecutor>(), Stub<IComposePlanner>(),
            NullLogger<Whiskers.Services.Webhooks.WebhookService>.Instance);
        var hook = await hooks.CreateWebhookAsync(new WebhookEntity
            { Name = "redeploy", TargetId = "web", Action = "restart", Owner = "user:admin@example.com" });
        var stored = (await hooks.GetWebhookAsync(hook.WebhookId))!;
        var start = ConfigChange.Describe(RemoveCounters(JsonSerializer.SerializeToNode(stored)!.AsObject())).Version;

        await Task.WhenAll(
            Gateway().ExecuteAsync(Config("webhook.save", "webhook", "op-par-take"), async (_, _) =>
                await hooks.SetOwnerAsync(hook.WebhookId, "user:ops@example.com", "user:admin@example.com")),
            Gateway().ExecuteAsync(Config("webhook.set_enabled", "webhook", "op-par-off"), async (_, _) =>
            {
                await hooks.SetEnabledAsync(hook.WebhookId, false);
                return 1;
            }));

        await AssertOneVersionChain(start, ["op-par-take", "op-par-off"]);
    }

    private static JsonElement RemoveCounters(System.Text.Json.Nodes.JsonObject webhook)
    {
        webhook.Remove(nameof(WebhookEntity.LastTriggered));
        webhook.Remove(nameof(WebhookEntity.TriggerCount));
        return JsonSerializer.SerializeToElement(webhook);
    }

    [Fact]
    public async Task Taking_over_ones_own_suspended_automation_records_the_lifted_block()
    {
        var store = new AutomationOwnerStore(new DataPathOptions(_dir), NullLogger<AutomationOwnerStore>.Instance);
        await store.SetAsync("auto-update", "user:admin@example.com");
        Assert.Equal(SuspendResult.Suspended,
            await store.SuspendAsync("auto-update", "user:admin@example.com", "owner lost the Admin role"));

        await Gateway().ExecuteAsync(Config("automation.owner_set", "automation", "op-auto-take"), async (_, _) =>
            await store.SetAsync("auto-update", "user:admin@example.com"));

        var results = await ResultsOf("op-auto-take");
        Assert.Equal("true → false", Text(Setting(results, "automation-owner:auto-update:Blocked"), "details"));
        Assert.Contains(results, r => Text(r, "action") == ConfigChange.VersionAction
                                      && Text(r, "targetId") == "automation-owner:auto-update");
    }

    [Fact]
    public async Task Overlapping_toggles_of_one_log_alert_record_the_switch_once()
    {
        var monitor = new Whiskers.Services.LogMonitor.LogMonitorService(_db.Scopes, Stub<Whiskers.Services.Docker.IDockerService>(),
            Stub<Whiskers.Services.ServerConfig.IServerConfigService>(), Stub<Whiskers.Services.Notifications.INotificationService>(),
            NullLogger<Whiskers.Services.LogMonitor.LogMonitorService>.Instance, Stub<Whiskers.Services.Docker.Budget.IServerBudget>(),
            Stub<Whiskers.Services.Observability.SelfMetrics.ISelfMetrics>(), Stub<Whiskers.Services.LogMonitor.Hygiene.ILogScanExclusions>(),
            Stub<Whiskers.Services.Observability.Outcomes.IActionOutcomeService>());
        var rule = await monitor.CreateRuleAsync(new LogAlertRuleEntity { Name = "oom", Pattern = "out of memory" });
        var ops = Enumerable.Range(0, 6).Select(i => $"op-par-toggle-{i}").ToList();

        await Task.WhenAll(ops.Select(op => Gateway().ExecuteAsync(Config("logalert.toggle", "log-alert", op), async (_, _) =>
        {
            await monitor.ToggleRuleAsync(rule.RuleId, false);
            return 1;
        })));

        var switched = new List<string>();
        foreach (var op in ops)
            switched.AddRange((await ResultsOf(op))
                .Where(r => Text(r, "targetId") == $"log-alert:{rule.RuleId}:Enabled").Select(r => Text(r, "details")));
        Assert.Equal("true → false", Assert.Single(switched));
    }

    [Theory]
    [InlineData("hooks.slack.com/services/T0/B0/q7Secret")]
    [InlineData("not a url q7Secret")]
    public async Task A_url_setting_that_is_no_absolute_url_is_never_shown_or_versioned(string value)
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "appsettings.json"));
        await store.SaveSectionAsync("Slack", new { WebhookUrl = "https://hooks.slack.com/services/T0/B0/first" });

        await Gateway().ExecuteAsync(Config("config.save", "settings", "op-bare-url"), async (_, _) =>
        {
            await store.SaveSectionAsync("Slack", new { WebhookUrl = value });
            return 1;
        });

        Assert.DoesNotContain("q7Secret", await EventText("op-bare-url"));
        Assert.Matches(@"^\[text, \d+ chars #[0-9a-f]{6}\]$",
            Text(Setting(await ResultsOf("op-bare-url"), "Slack:WebhookUrl"), "details").Split(" → ")[1]);
        Assert.Equal(ConfigChange.Describe(new { WebhookUrl = "other q7Other" }).Version,
            ConfigChange.Describe(new { WebhookUrl = value }).Version);
    }

    [Fact]
    public void Two_secrets_whose_short_fingerprints_collide_are_still_a_change_shown_apart()
    {
        // Within a few thousand values two share their six-digit fingerprint (birthday bound of 24 bits).
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        (string A, string B)? pair = null;
        for (var i = 0; i < 200_000 && pair is null; i++)
        {
            var value = $"secret-{i}";
            var shown = ConfigChange.Describe(new { Password = value }).Settings["Password"].Shown;
            if (seen.TryGetValue(shown, out var earlier)) pair = (earlier, value);
            else seen[shown] = value;
        }
        Assert.NotNull(pair);

        var change = Assert.Single(ConfigChange.Diff(
            ConfigChange.Describe(new { Password = pair.Value.A }), ConfigChange.Describe(new { Password = pair.Value.B })));
        Assert.NotEqual(change.Before, change.After);
    }

    [Fact]
    public async Task Overlapping_role_and_whitelist_saves_each_record_the_change_they_made()
    {
        var roles = new RoleService(EmptyConfig(), NullLogger<RoleService>.Instance, Path.Combine(_dir, "roles.json"));
        await roles.SetRoleAsync("admin@example.com", AppRole.Admin);
        var whitelist = new WhitelistService(EmptyConfig(), roles, NullLogger<WhitelistService>.Instance, new DataPathOptions(_dir));
        var rolesStart = ConfigChange.Describe(roles.GetRoleData()).Version;
        var whitelistStart = ConfigChange.Describe(whitelist.GetWhitelist()).Version;
        var ids = Enumerable.Range(0, 8).ToList();

        await Task.WhenAll(ids.SelectMany(i => new[]
        {
            Gateway().ExecuteAsync(Config("config.roles", "roles", $"op-par-role-{i}"), async (_, _) =>
            {
                await roles.SaveRoleDataAsync(new UserRoleData { Roles =
                [
                    new() { Email = "admin@example.com", Role = AppRole.Admin },
                    new() { Email = $"u{i}@example.com", Role = AppRole.Viewer },
                ] });
                return 1;
            }),
            Gateway().ExecuteAsync(Config("config.whitelist", "whitelist", $"op-par-wl-{i}"), async (_, _) =>
            {
                await whitelist.SaveWhitelistAsync(new WhitelistData { Emails = [$"u{i}@example.com"] });
                return 1;
            }),
        }));

        await AssertOneVersionChain(rolesStart, ids.Select(i => $"op-par-role-{i}"));
        await AssertOneVersionChain(whitelistStart, ids.Select(i => $"op-par-wl-{i}"));
    }

    [Fact]
    public async Task A_server_edited_in_place_and_saved_records_its_change()
    {
        var servers = new Whiskers.Services.ServerConfig.ServerConfigService(
            Microsoft.Extensions.Options.Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<Whiskers.Services.ServerConfig.ServerConfigService>.Instance, storePath: Path.Combine(_dir, "servers.json"));
        await servers.InitializeAsync();
        await servers.AddServerAsync(new ServerConfig { Id = "edge", Name = "edge", ConnectionType = ConnectionType.TCP, TcpHost = "10.0.0.1" });

        // The pattern onboarding used: the cached instance itself is changed, then saved.
        var cached = servers.GetServer("edge")!;
        await Gateway().ExecuteAsync(Config("server.save", "server", "op-inplace"), async (_, _) =>
        {
            cached.TcpHost = "100.64.0.7";
            cached.TcpUseTls = true;
            await servers.UpdateServerAsync(cached);
            return 1;
        });

        var results = await ResultsOf("op-inplace");
        Assert.Equal("10.0.0.1 → 100.64.0.7", Text(Setting(results, "server:edge:TcpHost"), "details"));
        Assert.Equal("false → true", Text(Setting(results, "server:edge:TcpUseTls"), "details"));
    }

    // ---- ConfigChange in isolation ------------------------------------------------------------------------------

    private sealed record Notifier(string Name, string WebhookUrl, string? Password, string Homeserver);

    [Fact]
    public void Secrets_show_only_whether_they_are_set_and_a_secret_alone_keeps_the_version()
    {
        var before = ConfigChange.Describe(new Notifier("ops", "https://hooks.example.com/T0/B0/xyzsecret", "hunter2", "https://matrix.example.com"));
        var after = ConfigChange.Describe(new Notifier("ops", "https://hooks.example.com/T0/B0/xyzsecret", "correct-horse", "https://matrix.example.com"));

        Assert.Equal(before.Version, after.Version);
        var change = Assert.Single(ConfigChange.Diff(before, after));
        Assert.Equal("Password", change.Path);
        Assert.Matches(@"^\[set #[0-9a-f]{6}\]$", change.Before);
        Assert.Matches(@"^\[set #[0-9a-f]{6}\]$", change.After);
        Assert.NotEqual(change.Before, change.After);
        var all = string.Join("|", before.Shown.Values.Concat(after.Shown.Values));
        Assert.DoesNotContain("hunter2", all);
        Assert.DoesNotContain("correct-horse", all);
        // A URL shows its origin only — a webhook carries its secret in the path.
        Assert.Equal("https://hooks.example.com/…", before.Shown["WebhookUrl"]);
        Assert.DoesNotContain("xyzsecret", all);
        Assert.Equal("[not set]", ConfigChange.Describe(new Notifier("ops", "", null, "")).Shown["Password"]);
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
    public void Free_text_shows_only_its_length_and_fingerprint()
    {
        var shown = ConfigChange.Describe(new
            { Description = string.Join(" ", Enumerable.Repeat("plain", 100)), Note = "see https://user:pw@host/x", Label = "web 1" }).Shown;

        Assert.Matches(@"^\[text, 599 chars #[0-9a-f]{6}\]$", shown["Description"]);
        Assert.Matches(@"^\[text, \d+ chars #[0-9a-f]{6}\]$", shown["Note"]);
        Assert.Equal("web 1", shown["Label"]);
    }

    [Fact]
    public void Changes_past_the_shown_part_are_still_changes_but_hidden_parts_never_enter_the_version()
    {
        var longBefore = ConfigChange.Describe(new { Pattern = new string('a', 100) + "1" });
        var longAfter = ConfigChange.Describe(new { Pattern = new string('a', 100) + "2" });
        var urlBefore = ConfigChange.Describe(new { WebhookUrl = "https://hooks.example.com/T0/B0/one" });
        var urlAfter = ConfigChange.Describe(new { WebhookUrl = "https://hooks.example.com/T0/B0/two" });
        var plainBefore = ConfigChange.Describe(new { WebhookUrl = "https://hooks.example.com/T0/B0/one", Throttle = 5 });
        var plainAfter = ConfigChange.Describe(new { WebhookUrl = "https://hooks.example.com/T0/B0/one", Throttle = 6 });

        // Free text and a URL's path are changes, but no hash of them is stored: the version cannot be used to test
        // a guess. A plain setting moves the version.
        Assert.Single(ConfigChange.Diff(longBefore, longAfter));
        Assert.Equal(longBefore.Version, longAfter.Version);
        Assert.Equal(urlBefore.Version, urlAfter.Version);
        Assert.NotEqual(plainBefore.Version, plainAfter.Version);
        // Also from the bare origin to a path or a query: the version holds scheme, host and port only.
        var root = ConfigChange.Describe(new { WebhookUrl = "https://hooks.example.com/" });
        foreach (var edited in new[] { "https://hooks.example.com/private", "https://hooks.example.com/?token=x" })
        {
            var after = ConfigChange.Describe(new { WebhookUrl = edited });
            Assert.Equal(root.Version, after.Version);
            Assert.Single(ConfigChange.Diff(root, after));
        }
        var rotated = Assert.Single(ConfigChange.Diff(urlBefore, urlAfter));
        Assert.Matches(@"^https://hooks\.example\.com/… #[0-9a-f]{6}$", rotated.Before);
        Assert.NotEqual(rotated.Before, rotated.After);
    }

    [Theory]
    [InlineData("Use key abc123")]
    [InlineData("password=hunter2")]
    [InlineData("x")]
    public void Free_text_fields_are_never_shown_however_short(string value)
    {
        var shown = ConfigChange.Describe(new { Prompt = value, SystemPrompt = value, Pattern = value, Command = value }).Shown;

        Assert.All(shown.Values, v => Assert.Matches(@"^\[text, \d+ chars #[0-9a-f]{6}\]$", v));
    }

    [Fact]
    public void A_list_of_free_text_is_keyed_by_position_and_its_version_is_stable_across_processes()
    {
        var policy = new { ForbiddenArgPatterns = new[] { "rm\\s+-rf\\s+/", "(?i)drop\\s+database" }, Names = new[] { "web", "db" } };

        var snapshot = ConfigChange.Describe(policy);

        // No per-process fingerprint in a path or the version: the same content has the same version after a restart.
        Assert.DoesNotContain(snapshot.Settings.Keys, k => k.Contains('#'));
        Assert.Contains("ForbiddenArgPatterns[0]", snapshot.Settings.Keys);
        Assert.Contains("Names[web]", snapshot.Settings.Keys);
        Assert.Equal(ConfigChange.Describe(policy).Version, snapshot.Version);
        Assert.DoesNotContain(snapshot.Settings.Values, v => v.Versioned.Contains('#'));
    }

    [Fact]
    public async Task A_tool_switch_and_level_change_are_readable()
    {
        var mcp = new McpPermissionService(NullLogger<McpPermissionService>.Instance, new DataPathOptions(_dir));
        await mcp.InitializeAsync();
        var data = mcp.GetPermissionData();
        data.Tools["list_containers"] = new McpToolConfig { Enabled = true, RequiredLevel = McpPermissionLevels.Read };
        await mcp.SavePermissionDataAsync(data);

        await Gateway().ExecuteAsync(Config("config.mcp_permissions", "mcp-permissions", "op-tool-off"), async (_, _) =>
        {
            await mcp.SetToolEnabledAsync("list_containers", false);
            return 1;
        });
        var changed = mcp.GetPermissionData();
        changed.Tools["list_containers"].RequiredLevel = McpPermissionLevels.Write;
        await Gateway().ExecuteAsync(Config("config.mcp_permissions", "mcp-permissions", "op-tool-level"), async (_, _) =>
        {
            await mcp.SavePermissionDataAsync(changed);
            return 1;
        });

        Assert.Equal("true → false", Text(Setting(await ResultsOf("op-tool-off"), "mcp-permissions:Tools.list_containers.Enabled"), "details"));
        Assert.Equal($"{McpPermissionLevels.Read} → {McpPermissionLevels.Write}",
            Text(Setting(await ResultsOf("op-tool-level"), "mcp-permissions:Tools.list_containers.RequiredLevel"), "details"));
    }

    [Fact]
    public async Task A_git_app_takeover_records_the_owner_change_and_a_lost_race_records_nothing()
    {
        var git = new Whiskers.Services.GitDeploy.GitDeployService(new ProtectedResourceResolutionTests.FakeHost(),
            new ProtectedResourceResolutionTests.NoVault(), Stub<IComposePlanner>(),
            NullLogger<Whiskers.Services.GitDeploy.GitDeployService>.Instance, Path.Combine(_dir, "apps.json"));
        await git.SaveAppAsync(new GitDeployApp
            { Id = "0123456789ab", Name = "shop", RepoUrl = "https://example.org/shop.git", ServerId = "local" },
            token: null, "user:old@example.com");

        await Gateway().ExecuteAsync(Config("gitdeploy.save", "gitdeploy", "op-take"), async (_, _) =>
            await git.SetOwnerAsync("0123456789ab", "user:admin@example.com", "user:old@example.com"));
        await Gateway().ExecuteAsync(Config("gitdeploy.save", "gitdeploy", "op-lost"), async (_, _) =>
            await git.SetOwnerAsync("0123456789ab", "user:third@example.com", "user:old@example.com"));

        Assert.Equal("user:old@example.com → user:admin@example.com",
            Text(Setting(await ResultsOf("op-take"), "gitdeploy:0123456789ab:Owner"), "details"));
        Assert.DoesNotContain(await ResultsOf("op-lost"), r => Text(r, "action") == ConfigChange.VersionAction);
    }

    [Fact]
    public async Task Reports_beyond_the_scope_cap_are_counted()
    {
        await Gateway().ExecuteAsync(Config("config.whitelist", "whitelist", "op-notes"), (_, _) =>
        {
            for (var i = 0; i < 40; i++) MutationScope.Note($"note {i}");
            return Task.FromResult(1);
        });

        Assert.Contains(await ResultsOf("op-notes"), r => Text(r, "action") == MutationScope.OmittedAction
                                                          && Text(r, "details") == "20 further result(s) not recorded");
    }
}
