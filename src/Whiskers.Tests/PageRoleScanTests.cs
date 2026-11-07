using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Xunit;

namespace Whiskers.Tests;

/// <summary>
/// INV-13, defence in depth at the source. The authoritative enforcement is the gateway, which decides every capability
/// at its catalog level for the signed-in user on every surface. This scan keeps pages from stating a role of their own
/// in front of a capability, in the forms the codebase uses or that are plausibly written by accident (also by copying
/// an existing pattern); each form has a self-test below. Deliberately obfuscated code (reflection, a role built from
/// arithmetic, a helper in another file) is out of scope.
/// <para>Structurally, no component member takes a role except Scheduled tasks' <c>RequireRoleAsync</c> (two pinned
/// call sites) and two display helpers; role checks in front of a capability are <c>RequireAsync(capabilityId, …)</c>
/// helpers that ask <see cref="CapabilityCatalog.RoleOf"/>, and the reads a page guards itself take their role from
/// <c>PageReads</c>. In the C# of every component (comments removed, <c>this.</c> ignored) the scan holds:</para>
/// <list type="bullet">
/// <item>each role literal, <c>HasRoleAsync(</c> and role comparison takes its role from the catalog (or the
/// <c>required</c> a helper got from it, or <c>PageReads</c>), or is a listed line — keyed by file, member and line,
/// occurring exactly once, in a member that runs no capability;</item>
/// <item>"runs a capability" is any use of the runner <c>Mutations</c> (a call, passing it on, copying it), or a
/// <c>Require…Async</c> / <c>RefuseAsync</c> call, and is transitive through members of the same file;</item>
/// <item>gate fields (a field assigned from a role check, e.g. <c>_isAdmin</c>) are denied by default: outside their
/// assignment and declaration they appear only on listed lines in members that run no capability, or — for a gate
/// decided for a catalog id — as <c>if (!gate)</c> in a handler whose literal capability ids need no more;</item>
/// <item>a handler's literal catalog check (<c>RequireAsync("id")</c>, <c>RoleOf("id")</c> in a comparison or
/// <c>HasRoleAsync</c>, a gate) is at least the strictest literal capability id it runs, also through same-file
/// helpers;</item>
/// <item>a capability id computed at run time is run only next to <c>RequireAsync(x, …)</c> / <c>RoleOf(x)</c> of that
/// very expression, through a hand-off parameter every caller fills with a literal id, or on a listed line;</item>
/// <item>read roles and role-taking display helpers are never reached from a member that runs a capability;</item>
/// <item>no role alias in the product, no <c>RoleFor</c>, <c>default(AppRole)</c> or enumeration of roles in a
/// component outside "Run now", and the role ↔ level rule has one spelling in the whole product.</item>
/// </list>
/// Not checked here: whether a computed check (<c>RequireAsync(capability, …)</c> with an id chosen at run time) matches
/// the literal ids the handler then runs (Dashboard rows, cloud actions) — the gateway holds those. Methods are found
/// by their modifiers (or, in a .razor code block, at the top level without them); code the member parser does not see
/// still falls under the line rules and the gate rule, which run over the whole code region.
/// </summary>
public class PageRoleScanTests
{
    // ── The listed lines ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lines a component may hold although they state a role or read a gate field: only where they decide
    /// what is loaded or shown, or hold data. Each entry is one exact (trimmed) line in one member ("(field)" outside
    /// any member), present exactly once, never in a member that runs a capability.</summary>
    private static readonly (string File, string Member, string Line)[] Listed =
    [
        // Admin-only pages and panels: whether to load and show their content.
        ("AgentHistory.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
        ("AgentView.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
        ("AiOperationsView.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
        ("AiTriggersView.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
        ("AuditLog.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
        ("GuardrailsView.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
        ("SelfStatus.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
        ("LocalAccountsPanel.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
        ("DiskHealthPanel.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"),
        ("IdentityBindingsPanel.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"),
        ("MetricRetentionPanel.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"),
        ("OutboundExceptionsPanel.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"),
        ("QuietHoursPanel.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"),
        // Where those gate fields are read: loading, or a load step returning early. None of these members runs a
        // capability; their writes go through the gateway or a catalog check.
        ("AgentHistory.razor", "OnInitializedAsync", "if (_isAdmin) await LoadData();"),
        ("AgentHistory.razor", "LoadData", "if (!_isAdmin) return;"),
        ("AgentView.razor", "OnAfterRenderAsync", "if (firstRender && _isAdmin && (_llmProvider == \"claude-code\" || !string.IsNullOrWhiteSpace(EffectiveKey)))"),
        ("AiTriggersView.razor", "OnInitializedAsync", "if (!_isAdmin) return;"),
        ("AuditLog.razor", "OnInitializedAsync", "if (_isAdmin) await LoadData();"),
        ("AuditLog.razor", "LoadData", "if (!_isAdmin) return;"),
        ("AuditLog.razor", "VerifyAsync", "if (!_isAdmin) return;"),
        ("GuardrailsView.razor", "OnInitializedAsync", "if (!_isAdmin) return;"),
        ("SelfStatus.razor", "RefreshTimelineAsync", "var audit = _isAdmin ? await AuditLog.GetRecentAsync(count: 200) : [];"),
        ("IdentityBindingsPanel.razor", "OnInitializedAsync", "if (_isAdmin) Load();"),
        ("LocalAccountsPanel.razor", "OnInitializedAsync", "if (!_isAdmin) return;"),
        ("MetricRetentionPanel.razor", "OnInitializedAsync", "if (_isAdmin) Load();"),
        ("OutboundExceptionsPanel.razor", "OnInitializedAsync", "if (!_isAdmin) return;"),
        ("QuietHoursPanel.razor", "OnInitializedAsync", "if (_isAdmin) Load();"),
        // Settings holds secrets: an admin-only load gate.
        ("Settings.razor", "OnInitializedAsync", "if (_role < AppRole.Admin) return;"),
        // What is shown: tabs, the first-server card, the add dialog opened from a link, masked check targets.
        ("ContainerDetail.razor", "EnvTabIndex", "private int EnvTabIndex => _role >= AppRole.Operator ? 5 : -1;"),
        ("ContainerDetail.razor", "DbTabIndex", "private int DbTabIndex => _container?.IsDatabase == true && _role >= AppRole.Operator ? 6 : -1;"),
        ("Dashboard.razor", "OnInitializedAsync", "_showFirstServerCard = _role >= AppRole.Admin &&"),
        ("Servers.razor", "OnInitializedAsync", "if (add is \"onboard\" or \"classic\" && _role >= AppRole.Admin)"),
        ("EndpointChecks.razor", "DisplayTarget", "if (role < AppRole.Admin && target.IndexOf('?') is >= 0 and var query)"),
        ("EndpointChecks.razor", "DisplayDetail", "role >= AppRole.Admin ? detail ?? \"\" : EndpointCheckRunner.MaskInternalAddresses(detail);"),
        // Data, not checks: form defaults, a parameter's fail-closed default, the RoleGuard default.
        ("Settings.razor", "(field)", "private Whiskers.Models.AppRole _defaultRole = Whiskers.Models.AppRole.Viewer;"),
        ("Settings.razor", "(field)", "private Whiskers.Models.AppRole _newRoleRole = Whiskers.Models.AppRole.Operator;"),
        ("LocalAccountsPanel.razor", "(field)", "private AppRole _newRole = AppRole.Viewer;"),
        ("ContainerDatabasePanel.razor", "(field)", "[Parameter] public AppRole Role { get; set; } = AppRole.Viewer;"),
        ("RoleGuard.razor", "(field)", "[Parameter] public Whiskers.Models.AppRole RequiredRole { get; set; } = Whiskers.Models.AppRole.Operator;"),
        // The reads a page guards itself, one named role each (not capabilities).
        ("PageReads.cs", "MigrationStatus", "public static AppRole MigrationStatus => AppRole.Operator;"),
        ("PageReads.cs", "NginxConfigTest", "public static AppRole NginxConfigTest => AppRole.Admin;"),
        ("PageReads.cs", "ContainerEnvironment", "public static AppRole ContainerEnvironment => AppRole.Operator;"),
        // "Run now": the task's own requirement from SchedulerRequirementResolver, Admin when it cannot answer.
        ("ScheduledTasksView.razor", "RunNowRole", "? Whiskers.Services.Mutations.CapabilityCatalog.RoleFor(scheduler.LevelToRun(task))"),
        ("ScheduledTasksView.razor", "RunNowRole", ": AppRole.Admin;"),
        ("ScheduledTasksView.razor", "RunNowRole", "return AppRole.Admin;"),
    ];

    /// <summary>The role every page holds before it is loaded: Viewer, fail closed.</summary>
    private const string RoleDefault = "private AppRole _role = AppRole.Viewer;";

    /// <summary>Members that take a role, each for a fixed reason.</summary>
    private static readonly (string File, string Member)[] RoleParameters =
    [
        ("ScheduledTasksView.razor", "RequireRoleAsync"),    // RequireAsync's core, and "Run now" with the task's requirement
        ("EndpointChecks.razor", "DisplayTarget"),           // display only: masks internal addresses for non-admins
        ("EndpointChecks.razor", "DisplayDetail"),
    ];

    /// <summary>The two calls of RequireRoleAsync (member, line), each exactly once.</summary>
    private static readonly (string File, string Member, string Line)[] RequireRoleCalls =
    [
        ("ScheduledTasksView.razor", "RequireAsync", "RequireRoleAsync(CapabilityCatalog.RoleOf(capabilityId), capabilityId, arguments);"),
        ("ScheduledTasksView.razor", "RunNow", "if (!await RequireRoleAsync(RunNowRole(task), \"scheduler.run\","),
    ];

    /// <summary>A capability id computed at run time that the scan cannot tie to a catalog check on the page.</summary>
    private static readonly (string File, string Member, string Id)[] ComputedRuns =
    [
        // The retention delete of a backup task (volume / database backup delete, chosen by RetentionDecision). It runs
        // only after "Run now" passed the task's own requirement (SchedulerRequirementResolver); the gateway decides the
        // delete capability at its catalog level.
        ("ScheduledTasksView.razor", "RunWithRetentionAsync", "d.Capability"),
    ];

    private static readonly CapabilityCatalog Catalog = new();

    // ── The tests ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_component_states_its_own_role_in_front_of_a_capability()
    {
        var files = ComponentFiles();
        var stats = new Stats();
        var findings = files.SelectMany(f => Findings(Path.GetFileName(f), File.ReadAllText(f), Listed, stats)).ToList();
        Assert.True(findings.Count == 0, "INV-13:\n" + string.Join("\n", findings));

        // The scan sees what it is about.
        Assert.True(stats.Handlers > 130, $"only {stats.Handlers} members run a capability: the member scan is broken");
        Assert.True(stats.Compared > 60, $"only {stats.Compared} handlers had their check compared with what they run");
        Assert.True(stats.CatalogChecks > 25, $"only {stats.CatalogChecks} role checks ask the catalog");
        Assert.True(stats.GateUses >= 15, $"only {stats.GateUses} gate-field uses seen: the gate scan is broken");
    }

    /// <summary>Every listed line (and each RequireRoleAsync call) exists exactly once in its member, so the lists
    /// cannot hide a stale or a duplicated entry.</summary>
    [Fact]
    public void Every_listed_line_exists_exactly_once_where_it_is_listed()
    {
        var lines = ComponentFiles().SelectMany(f => MemberLines(Path.GetFileName(f), File.ReadAllText(f))).ToList();
        foreach (var entry in Listed.Concat(RequireRoleCalls))
            Assert.True(lines.Count(l => l == entry) == 1, $"{entry.File}:{entry.Member}: '{entry.Line}' is not there exactly once");
    }

    /// <summary>Wherever the mutation runner is injected, it is called <c>Mutations</c>: the scan recognises a capability
    /// by that name.</summary>
    [Fact]
    public void The_mutation_runner_is_injected_as_Mutations_everywhere()
    {
        var product = Path.Combine(SourceRoot(), "src", "Whiskers");
        var injections = ProductFiles(product)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?:@inject|\[Inject\][^\n]*?)\s+(?:[\w.]+\.)?IUiMutationRunner\s+(\w+)")
                .Select(m => (File: Path.GetFileName(f), Name: m.Groups[1].Value)))
            .ToList();
        Assert.True(injections.Count > 30, $"only {injections.Count} injections of IUiMutationRunner found");
        Assert.All(injections, i => Assert.True(i.Name == "Mutations", $"{i.File} injects the runner as {i.Name}"));
    }

    /// <summary>The database panel's role parameter, which its own checks compare against, is the page role of the
    /// container page that hosts it — set in markup, so pinned here.</summary>
    [Fact]
    public void The_database_panel_gets_the_page_role()
    {
        var tags = ComponentFiles().Where(f => f.EndsWith(".razor", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"<ContainerDatabasePanel\b[^>]*?/?>", RegexOptions.Singleline)
                .Select(m => (File: Path.GetFileName(f), Tag: m.Value)))
            .ToList();
        var tag = Assert.Single(tags);
        Assert.Equal("ContainerDetail.razor", tag.File);
        Assert.Single(Regex.Matches(tag.Tag, @"\bRole\s*="));
        Assert.Matches(@"\bRole=""@_role""", tag.Tag);
    }

    /// <summary>The account service re-checks create, reset and delete with the catalog role of exactly that
    /// capability; the only other role it asks for is the account list, a read.</summary>
    [Fact]
    public void The_account_service_rechecks_with_the_catalog_role()
    {
        var code = StripComments(File.ReadAllText(Path.Combine(SourceRoot(), "src", "Whiskers", "Services", "Auth", "LocalAccountService.cs")));
        Assert.Matches(@"AccountAdminAsync\(string capabilityId\)\s*=>[^;]*AdminAsync\(CapabilityCatalog\.RoleOf\(capabilityId\)\);", code);
        foreach (var (method, id) in new[] { ("CreateAsync", "account.create"), ("ResetPasswordAsync", "account.password_reset"), ("DeleteAsync", "account.delete") })
        {
            var start = code.IndexOf($" {method}(", StringComparison.Ordinal);
            Assert.True(start > 0, method);
            var body = code[start..MatchingBrace(Skeleton(code), code.IndexOf('{', start))];
            Assert.Single(Regex.Matches(body, @"\b(?:Account)?AdminAsync\("));
            Assert.Contains($"AccountAdminAsync(\"{id}\")", body);
        }
        // AdminAsync is reached from AccountAdminAsync and from the list (a read, Admin) only; it compares with what it got.
        Assert.Equal(2, Regex.Matches(code, @"(?<![\w.])AdminAsync\((?!AppRole required)").Count);
        Assert.Single(Regex.Matches(code, @"(?<![\w.])AdminAsync\(AppRole\.Admin\)"));
        Assert.Single(Regex.Matches(code, @"session\.Role\s*[<>]"));
        Assert.Contains("if (session.Role < required) return (null, LocalAccountErrors.NotAuthorized);", code);
    }

    /// <summary>No role alias anywhere in the product: a <c>using static</c> of <c>AppRole</c> (also global), a role
    /// constant, a cast to a role or parsing one would let a page name a role without <c>AppRole.</c>.</summary>
    [Fact]
    public void No_role_alias_in_the_product()
    {
        var product = Path.Combine(SourceRoot(), "src", "Whiskers");
        var findings = ProductFiles(product)
            .SelectMany(f => Alias.Matches(StripComments(File.ReadAllText(f))).Select(m => $"{Path.GetFileName(f)}: {m.Value.Trim()}"))
            .Where(f => !AllowedAliases.Contains(f))
            .ToList();
        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    /// <summary>Role aliases the product needs for data, not for checks.</summary>
    private static readonly string[] AllowedAliases = ["CapabilityCatalog.cs: AppRole RoleOf("];   // the catalog's own RoleOf

    private const string IsAdminField = "    private bool _isAdmin;\n";
    private const string IsAdminSet = "    protected override async Task OnInitializedAsync()\n    {\n        _isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);\n";

    /// <summary>The scan catches each way around the catalog the reviews named, and leaves the catalog forms alone. The
    /// listed lines here are the go-between check and the admin gate's own assignment, so each case is caught for what
    /// it adds.</summary>
    [Theory]
    [InlineData("gate field from a literal", "    private bool _may;\n    protected override async Task OnInitializedAsync()\n    {\n        _may = await CurrentUser.HasRoleAsync(AppRole.Operator);\n    }\n    private async Task Save()\n    {\n        if (!_may) { await Mutations.RefuseAsync(\"config.agent\", new { }, \"no\"); return; }\n        await Mutations.RunAsync(\"config.agent\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("gate field by comparison", "    private bool _may;\n    protected override async Task OnInitializedAsync()\n    {\n        _may = (await CurrentUser.GetRoleAsync()) >= AppRole.Operator;\n    }\n")]
    [InlineData("gate field for another capability", "    private bool _may;\n    protected override async Task OnInitializedAsync()\n    {\n        _may = await CurrentUser.HasRoleAsync(CapabilityCatalog.RoleOf(\"logalert.create\"));\n    }\n    private async Task Remove()\n    {\n        if (!_may) return;\n        await Mutations.RunAsync(\"container.remove\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("gate field from .Role, set through this.", "    private bool _may;\n    protected override async Task OnInitializedAsync()\n    {\n        this._may = (await CurrentUser.CheckSessionAsync()).Role >= CapabilityCatalog.RoleOf(\"logalert.create\");\n    }\n    private async Task Remove()\n    {\n        if (!_may) return;\n        await Mutations.RunAsync(\"container.remove\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("catalog gate copied in its handler", "    private bool _may;\n    private bool _copy;\n    protected override async Task OnInitializedAsync()\n    {\n        _may = await CurrentUser.HasRoleAsync(CapabilityCatalog.RoleOf(\"config.agent\"));\n    }\n    private async Task Save()\n    {\n        _copy = _may;\n        await Mutations.RunAsync(\"config.agent\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("copied gate field", IsAdminField + "    private bool _may;\n" + IsAdminSet + "        _may = _isAdmin;\n    }\n")]
    [InlineData("gate field copied through a ternary", IsAdminField + "    private bool _may;\n" + IsAdminSet + "        _may = _isAdmin ? true : false;\n    }\n")]
    [InlineData("gate field through a non-boolean ternary", IsAdminField + "    private int _mode;\n" + IsAdminSet + "        _mode = _isAdmin ? 1 : 0;\n    }\n    private async Task Go()\n    {\n        if (_mode != 1) return;\n        await Mutations.RunAsync(\"config.agent\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("gate field through a setter method", IsAdminField + "    private bool _mode;\n" + IsAdminSet + "        SetMode(_isAdmin);\n    }\n    private void SetMode(bool b) { _mode = b; }\n    private async Task Go()\n    {\n        if (!_mode) return;\n        await Mutations.RunAsync(\"config.agent\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("gate field behind a property", IsAdminField + IsAdminSet + "    }\n    private bool CanManage => _isAdmin;\n")]
    [InlineData("gate field behind an accessor property", IsAdminField + IsAdminSet + "    }\n    private bool CanEdit { get => _isAdmin; }\n    private async Task Go()\n    {\n        if (!CanEdit) return;\n        await Mutations.RunAsync(\"config.agent\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("gate field behind an accessor block", IsAdminField + IsAdminSet + "    }\n    private bool CanEdit\n    {\n        get\n        {\n            return _isAdmin;\n        }\n    }\n")]
    [InlineData("gate field in a capability handler", IsAdminField + IsAdminSet + "    }\n    private async Task Go() {\n        if (!_isAdmin) { await Mutations.RefuseAsync(\"config.agent\", new { }, \"no\"); return; } }\n")]
    [InlineData("gate field in front of the runner passed on", IsAdminField + IsAdminSet + "    }\n    private async Task Save()\n    {\n        if (!_isAdmin) return;\n        await Whiskers.Services.Vault.VaultBackedSettingsSave.SaveAppSettingsAsync(Mutations, Vault);\n    }\n")]
    [InlineData("gate field in front of the runner copied to a local", IsAdminField + IsAdminSet + "    }\n    private async Task Save()\n    {\n        if (!_isAdmin) return;\n        var m = Mutations;\n        await m.RunAsync(\"config.agent\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("gate field in a modifier-less method", IsAdminField + IsAdminSet + "    }\n    Task Save()\n    {\n        if (!_isAdmin) return;\n        await Mutations.RunAsync(\"config.agent\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("page RoleOf", "    private AppRole RoleOf(string id) => AppRole.Operator;\n")]
    [InlineData("page RoleOf through the catalog", "    private AppRole RoleOf(string id) => CapabilityCatalog.RoleOf(id);\n")]
    [InlineData("helper that takes a role", "    private bool Require(AppRole required)\n    {\n        return _role >= required;\n    }\n")]
    [InlineData("generic helper that takes a role", "    private bool Require<T>(AppRole required)\n    {\n        return _role >= required;\n    }\n")]
    [InlineData("renamed refusing helper", "    private async Task<bool> GuardAsync(AppRole needed, string cap, object args)\n    {\n        if (_role >= needed) return true;\n        await Mutations.RefuseAsync(cap, args, \"no\");\n        return false;\n    }\n")]
    [InlineData("RequireRoleAsync somewhere else", "    private async Task Go()\n    {\n        if (!await RequireRoleAsync(_role, \"scheduler.delete\", new { })) return;\n    }\n")]
    [InlineData("literal to a helper", "    private async Task Go()\n    {\n        if (!await RequireAsync(AppRole.Admin, \"container.remove\", new { })) return;\n    }\n")]
    [InlineData("const alias", "    private const AppRole Needed = AppRole.Admin;\n")]
    [InlineData("cast", "    private async Task Go()\n    {\n        if (_role < (AppRole)2) return;\n    }\n")]
    [InlineData("RoleFor", "    private async Task Go()\n    {\n        if (_role < CapabilityCatalog.RoleFor(McpPermissionLevels.Write)) return;\n        await Mutations.RunAsync(\"container.remove\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("default role", "    private async Task Go()\n    {\n        if (_role < default(AppRole)) return;\n    }\n")]
    [InlineData("enumerated roles", "    private AppRole Pick() => Enum.GetValues<AppRole>()[1];\n")]
    [InlineData("the role compared with itself", "    private async Task Go()\n    {\n        var required = _role;\n        if (_role >= required) await Mutations.RunAsync(\"container.remove\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("RoleOf of a non-catalog id", "    private async Task Go()\n    {\n        if (_role < CapabilityCatalog.RoleOf(\"container.removee\")) return;\n    }\n")]
    [InlineData("RoleOf of a computed id outside a helper", "    private async Task Go(string id)\n    {\n        if (_role < CapabilityCatalog.RoleOf(id)) return;\n    }\n")]
    [InlineData("one-line member", "    private Task<bool> Go() => RequireAsync(AppRole.Admin, \"x.y\", new { });\n")]
    [InlineData("comment instead of the catalog", "    private async Task<bool> RequireAsync(string capabilityId, object arguments)\n    {\n        // CapabilityCatalog.RoleOf(capabilityId)\n        if (_role >= AppRole.Operator) return true;\n        await Mutations.RefuseAsync(capabilityId, arguments, \"no\");\n        return false;\n    }\n")]
    [InlineData("helper without the catalog", "    private async Task<bool> RequireAsync(string capabilityId, object arguments)\n    {\n        if (_ok) return true;\n        await Mutations.RefuseAsync(capabilityId, arguments, \"no\");\n        return false;\n    }\n")]
    [InlineData("HasRoleAsync with a literal", "    private async Task Go()\n    {\n        if (!await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin)) { await Mutations.RefuseAsync(\"x.y\", new { }, \"no\"); return; }\n    }\n")]
    [InlineData("weaker check than what runs", "    private async Task Go()\n    {\n        if (!await RequireAsync(\"container.start\", new { })) return;\n        await Mutations.RunAsync(\"container.remove\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("weaker check, the stricter id only in a message", "    private async Task Go()\n    {\n        if (!await RequireAsync(\"container.start\", new { })) return;\n        var shown = CapabilityCatalog.RoleOf(\"container.remove\");\n        Snackbar.Add(shown.ToString());\n        await Mutations.RunAsync(\"container.remove\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("weaker check than a hand-off helper runs", "    private async Task Go()\n    {\n        if (!await RequireAsync(\"container.start\", new { })) return;\n        await Exec(\"db.query\");\n    }\n    private async Task Exec(string capability)\n    {\n        await Mutations.RunAsync(capability, new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("computed id next to a literal check", "    private async Task Go()\n    {\n        var action = Pick();\n        if (!await RequireAsync(\"container.start\", new { })) return;\n        await Mutations.RunAsync(action, new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("computed id handed to a helper", "    private async Task Go(string name)\n    {\n        if (!await RequireAsync(\"container.start\", new { })) return;\n        await Exec(name);\n    }\n    private async Task Exec(string capability)\n    {\n        await Mutations.RunAsync(capability, new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("listed line in a capability handler", "    private async Task Go()\n    {\n        if (_role < AppRole.Admin) return;\n        await Mutations.RunAsync(\"config.save\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("listed line in front of a hand-off helper", "    private async Task Go()\n    {\n        if (_role < AppRole.Admin) return;\n        await Exec(\"db.query\");\n    }\n    private async Task Exec(string capability)\n    {\n        await Mutations.RunAsync(capability, new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("listed line twice", "    private async Task Go()\n    {\n        if (_role < AppRole.Admin) return;\n        if (_role < AppRole.Admin) return;\n    }\n")]
    [InlineData("a read-check helper in front of a capability", "    private bool MayRead()\n    {\n        var required = Whiskers.Components.PageReads.MigrationStatus;\n        return _role >= required;\n    }\n    private async Task Go()\n    {\n        if (!MayRead()) return;\n        await Exec(\"db.query\");\n    }\n    private async Task Exec(string capability)\n    {\n        await Mutations.RunAsync(capability, new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("a read check two calls deep", "    private bool Ok() => _role >= Whiskers.Components.PageReads.NginxConfigTest;\n    private bool Ok2() => Ok();\n    private async Task Go()\n    {\n        if (!Ok2()) return;\n        await Mutations.RunAsync(\"nginx.reload\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("a read role in front of a capability", "    private async Task Go()\n    {\n        if (_role < Whiskers.Components.PageReads.NginxConfigTest) return;\n        await Mutations.RunAsync(\"nginx.reload\", new { }, _ => Task.CompletedTask);\n    }\n")]
    public void The_scan_catches_a_way_around_the_catalog(string what, string code)
    {
        (string, string, string)[] listed =
        [
            ("P.razor", "Go", "if (_role < AppRole.Admin) return;"),
            ("P.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
        ];
        Assert.True(Findings("P.razor", Page(code), listed, new Stats()).Count > 0, $"not caught: {what}");
    }

    [Theory]
    [InlineData("using static", "@using static Whiskers.Models.AppRole\n<p>x</p>\n@code {\n}\n")]
    [InlineData("handler in markup", "<button @onclick=\"@(async () => await Mutations.RunAsync(\"x.y\", new { }, _ => Task.CompletedTask))\">x</button>\n@code {\n}\n")]
    [InlineData("computed id handed to a helper from markup", "<button @onclick=\"@(() => Exec(_picked))\">x</button>\n@code {\n    private async Task Exec(string capability)\n    {\n        await Mutations.RunAsync(capability, new { }, _ => Task.CompletedTask);\n    }\n}\n")]
    public void The_scan_catches_a_way_around_the_catalog_outside_the_code_block(string what, string page) =>
        Assert.True(Findings("P.razor", page, [], new Stats()).Count > 0, $"not caught: {what}");

    [Fact]
    public void The_scan_leaves_the_catalog_forms_listed_lines_and_markup_alone()
    {
        (string, string, string)[] listed =
        [
            ("P.razor", "OnInitializedAsync", "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"),
            ("P.razor", "OnInitializedAsync", "if (_isAdmin) await Load();"),
            ("P.razor", "Peek", "var shown = _isAdmin ? Load() : null;"),
        ];
        const string code =
            "    private AppRole _role = AppRole.Viewer;\n" +
            "    private bool _isAdmin;\n" +
            "    private bool _may, _mayToo;\n" +
            "    private async Task<bool> RequireAsync(string capabilityId, object arguments)\n    {\n" +
            "        // A role from the table.\n        var required = CapabilityCatalog.RoleOf(capabilityId);\n" +
            "        if (_role >= required) return true;\n        await Mutations.RefuseAsync(capabilityId, arguments, $\"Requires the {required} role.\");\n        return false;\n    }\n" +
            "    protected override async Task OnInitializedAsync()\n    {\n        _isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);\n" +
            "        _may = await CurrentUser.HasRoleAsync(CapabilityCatalog.RoleOf(\"config.agent\"));\n" +
            "        _mayToo = (await CurrentUser.CheckSessionAsync()).Role >= CapabilityCatalog.RoleOf(\"container.remove\");\n" +
            "        if (_isAdmin) await Load();\n    }\n" +
            "    private async Task SaveAgent()\n    {\n        if (!_may) { await Mutations.RefuseAsync(\"config.agent\", new { }, \"no\"); return; }\n" +
            "        await Mutations.RunAsync(\"config.agent\", new { }, _ => Task.CompletedTask);\n    }\n" +
            "    private async Task Remove() {\n        if (!_mayToo) return;\n        await Mutations.RunAsync(\"container.remove\", new { }, _ => Task.CompletedTask); }\n" +
            "    private async Task Wipe() {\n        if (!await RequireAsync(\"container.remove\", new { })) return;\n        await Exec(\"container.exec\"); }\n" +
            "    private async Task Exec(string capability)\n    {\n        await Mutations.RunAsync(capability, new { }, _ => Task.CompletedTask);\n    }\n" +
            "    private async Task Act(string action)\n    {\n        var id = action == \"stop\" ? \"container.stop\" : \"container.start\";\n" +
            "        if (!await RequireAsync(id, new { })) return;\n        await Mutations.RunAsync(id, new { }, _ => Task.CompletedTask);\n    }\n" +
            "    private async Task Flip(bool on)\n    {\n        await Mutations.RunAsync(on ? \"container.start\" : \"container.stop\", new { }, _ => Task.CompletedTask);\n    }\n" +
            "    private async Task Open()\n    {\n        if (await CurrentUser.GetRoleAsync() < CapabilityCatalog.RoleOf(\"terminal.host_open\")) return;\n" +
            "        await Mutations.RunAsync(\"terminal.host_open\", new { }, _ => Task.CompletedTask);\n    }\n" +
            "    private bool MayTestConfig()\n    {\n        var required = Whiskers.Components.PageReads.NginxConfigTest;\n        return _role >= required;\n    }\n" +
            "    private async Task TestConfig()\n    {\n        if (!MayTestConfig()) return;\n        var text = \"{ unbalanced\";\n    }\n" +
            "    private object? Peek()\n    {\n        var shown = _isAdmin ? Load() : null;\n        return shown;\n    }\n" +
            "    private Task Load() => Task.CompletedTask;\n" +
            "    private string Note => \"it's // not a comment\";\n";
        var page = "@if (_role >= AppRole.Admin && _isAdmin) { <b>don't care: shown only</b> }\n" +
                   "<button @onclick=\"@(() => Exec(\"container.exec\"))\">x</button>\n" + Page(code);
        var stats = new Stats();
        var findings = Findings("P.razor", page, listed, stats);
        Assert.True(findings.Count == 0, string.Join("\n", findings));
        Assert.True(stats.Compared >= 4, $"only {stats.Compared} handlers compared");
    }

    /// <summary>The fixed rule has one spelling in the product (<see cref="CapabilityCatalog.LevelFor"/> and its
    /// inverse <see cref="CapabilityCatalog.RoleFor"/>): no other file turns a role literal into a level or back — no
    /// switch, ternary, if or index, with the level constants or the level strings.</summary>
    [Fact]
    public void The_role_level_rule_is_spelled_only_in_the_catalog()
    {
        var product = Path.Combine(SourceRoot(), "src", "Whiskers");
        var findings = ProductFiles(product)
            .Where(f => Path.GetFileName(f) != "CapabilityCatalog.cs")
            .SelectMany(f => RoleLevelSpellings(StripComments(File.ReadAllText(f))).Select(s => $"{Path.GetFileName(f)}: {s}"))
            .ToList();
        Assert.True(findings.Count == 0, string.Join("\n", findings));

        var catalog = StripComments(File.ReadAllText(Path.Combine(product, "Services", "Mutations", "CapabilityCatalog.cs")));
        Assert.Equal(4, RoleLevelSpellings(catalog).Count);   // the two explicit arms of LevelFor and of RoleFor
        var calls = ProductFiles(product).Sum(f => Regex.Matches(StripComments(File.ReadAllText(f)), @"CapabilityCatalog\.LevelFor\(").Count);
        Assert.True(calls >= 10, $"only {calls} surfaces call LevelFor");
    }

    [Theory]
    [InlineData("var level = role switch\n{\n    AppRole.Admin => McpPermissionLevels.Admin,\n    AppRole.Operator => McpPermissionLevels.Write,\n    _ => McpPermissionLevels.Read,\n};")]
    [InlineData("var level = role == AppRole.Admin ? McpPermissionLevels.Admin : McpPermissionLevels.Read;")]
    [InlineData("if (role >= AppRole.Operator) { level = McpPermissionLevels.Write; }")]
    [InlineData("var role = level == McpPermissionLevels.Admin ? AppRole.Admin : AppRole.Viewer;")]
    [InlineData("var role = McpPermissionLevels.Normalize(level) switch { McpPermissionLevels.Write => Whiskers.Models.AppRole.Operator, _ => Whiskers.Models.AppRole.Viewer };")]
    [InlineData("var level = role == AppRole.Admin ? \"admin\" : \"read\";")]
    [InlineData("var level = role switch { AppRole.Operator => \"Write\", _ => \"read\" };")]
    [InlineData("var role = level == \"admin\" ? AppRole.Admin : AppRole.Viewer;")]
    [InlineData("var level = Levels[(int)role];")]
    [InlineData("var level = names[(int)_role];")]
    public void The_rule_scan_catches_a_second_spelling(string code) => Assert.NotEmpty(RoleLevelSpellings(code));

    [Fact]
    public void The_rule_scan_leaves_unrelated_role_and_level_code_alone() =>
        Assert.Empty(RoleLevelSpellings("var level = CapabilityCatalog.LevelFor(role);\nif (_role < AppRole.Admin) return;\nvar l = McpPermissionLevels.Read;\nvar n = (int)count;"));

    private static List<string> RoleLevelSpellings(string code)
    {
        const string role = @"(?:Whiskers\.Models\.)?AppRole\.(?:Admin|Operator|Viewer)\b";
        const string level = @"(?:McpPermissionLevels\.(?:Admin|Write|Read)\b|""(?i:admin|write|read)"")";
        return Regex.Matches(code, $@"{role}[^;]{{0,200}}?{level}|{level}[^;]{{0,200}}?{role}|\[\s*\(int\)\s*\(?\s*\w*[rR]ole\b")
            .Select(m => Regex.Replace(m.Value, @"\s+", " ")).ToList();
    }

    // ── The scan ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Stats
    {
        public int Handlers;
        public int Compared;
        public int CatalogChecks;
        public int GateUses;
    }

    private static string Page(string code) => "<p>page</p>\n@code {\n" + code + "}\n";

    /// <summary>Running a capability in C#: any use of the runner (a call, passing it on, copying it), or a check helper
    /// that refuses on record.</summary>
    private static readonly Regex CodeRun = new(@"(?<![\w.])Mutations\b|\bRefuseAsync\(|\bRequire\w*Async\(");
    /// <summary>Running a capability in markup (where <c>@inject … Mutations</c> itself is not a use).</summary>
    private static readonly Regex MarkupRun = new(@"(?<![\w.])Mutations\.\w+|\bRefuseAsync\(|\bRequire\w*Async\(");
    private static readonly Regex RoleLiteral = new(@"(?:Whiskers\.Models\.)?\bAppRole\.(?:Admin|Operator|Viewer)\b");
    private static readonly Regex HasRole = new(@"\bHasRoleAsync\s*\(\s*(?<arg>[^;]*)");
    private static readonly Regex RoleComparison = new(
        @"(?:\b_?[rR]ole\b|GetRoleAsync\(\)\)?|\.Role\b)\s*(?:<=|>=|<(?![<=])|>(?![>=]))\s*(?<rhs>[^;&|?]*)" +
        @"|(?<lhs>[\w.()""]+)\s*(?:<=|>=|(?<![<=])<|(?<![>=-])>)\s*\(?\s*\b_?[rR]ole\b");
    private static readonly Regex CatalogRole = new(@"CapabilityCatalog\.RoleOf\(\s*(?<arg>""[^""]*""|\w+)\s*\)");
    private static readonly Regex ForbiddenRoleSource = new(
        @"CapabilityCatalog\.RoleFor\(|\bdefault\s*\(\s*(?:Whiskers\.Models\.)?AppRole\s*\)|Enum\.GetValues\s*(?:<\s*(?:Whiskers\.Models\.)?AppRole|\(\s*typeof\s*\(\s*(?:Whiskers\.Models\.)?AppRole)");
    private static readonly Regex Alias = new(
        @"\busing\s+static\s+[\w.]*\bAppRole\b|\bconst\s+(?:Whiskers\.Models\.)?AppRole\b|\bstatic\s+readonly\s+(?:Whiskers\.Models\.)?AppRole\b" +
        @"|\(\s*(?:Whiskers\.Models\.)?AppRole\s*\)\s*[\w(-]|\bAppRole\s+RoleOf\s*\(|\bEnum\.(?:Parse|TryParse)\s*<\s*(?:Whiskers\.Models\.)?AppRole\b");
    private static readonly Regex CatalogIdLiteral = new(@"""([a-z_]+\.[a-z_.]+)""");
    /// <summary>A field assignment at the start of a statement whose value comes from a role check: a gate field.</summary>
    private static readonly Regex GateAssignment = new(
        @"(?<=(?:^|[;{})])[ \t\r\n]*)(?<name>\w+)\s*=(?![=>])(?<rhs>[^;]*?(?:HasRoleAsync|GetRoleAsync|RoleOf\(|\.Role\b|\b_?role\b)[^;]*);",
        RegexOptions.Multiline);
    /// <summary>A field declaration (one or more names) at the top level of a code region.</summary>
    private static readonly Regex FieldDeclaration = new(
        @"(?m)^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(?:(?:private|protected|public|internal|static|readonly|volatile)\s+)*(?!(?:return|await|throw|yield|else|new|case|goto|var)\b)[\w.<>\[\]?]+\s+(?<names>\w+(?:\s*,\s*\w+)*)\s*(?:=\s*(?:true|false|default|null|0)\s*)?;[ \t]*$");
    /// <summary>A literal check in a handler: <c>RequireAsync("id")</c>, <c>RoleOf("id")</c> in a comparison or as the
    /// argument of <c>HasRoleAsync</c>.</summary>
    private static readonly Regex LiteralCheck = new(
        @"\bRequire\w*Async\(\s*""(?<id>[a-z_]+\.[a-z_.]+)""" +
        @"|(?:HasRoleAsync\(\s*|(?:<=|>=|<|(?<![=-])>)\s*\(?\s*)CapabilityCatalog\.RoleOf\(\s*""(?<id>[^""]+)""\s*\)" +
        @"|CapabilityCatalog\.RoleOf\(\s*""(?<id>[^""]+)""\s*\)\s*(?:<=|>=|<(?![<=])|>(?![>=]))");

    private sealed record Member(string Name, string Parameters, int Start, int BodyStart, int End, string Body, bool Expression);
    private sealed record Region(string Code, string Skeleton);
    private sealed record Gate(string? Id, HashSet<int> Assignments);

    /// <summary>Every rule's findings for one component file: "file:member: what".</summary>
    private static List<string> Findings(string file, string source, (string File, string Member, string Line)[] listed, Stats stats)
    {
        var findings = new List<string>();
        var allowed = listed.Where(l => l.File == file).Select(l => (l.Member, l.Line)).ToList();
        var lines = MemberLines(file, source).ToList();
        foreach (var (member, line) in allowed.Concat(RequireRoleCalls.Where(r => r.File == file).Select(r => (r.Member, r.Line))))
            if (lines.Count(l => l.Member == member && l.Line == line) > 1)
                findings.Add($"{file}:{member}: '{line}' is listed once but occurs more often");

        foreach (Match m in Alias.Matches(StripComments(source)))
            findings.Add($"{file}: '{m.Value.Trim()}' — a role alias or page-defined RoleOf");
        var markup = file.EndsWith(".razor", StringComparison.Ordinal) ? MarkupOnly(StripComments(source)) : "";
        foreach (Match m in MarkupRun.Matches(markup))
            findings.Add($"{file}: '{m.Value}' in markup — handlers that run a capability belong in @code");

        foreach (var region in CodeRegions(file, source))
        {
            var members = Members(region, file.EndsWith(".razor", StringComparison.Ordinal));
            Member? Owner(int index) => members.Where(mb => mb.Start <= index && index < mb.End).OrderByDescending(mb => mb.Start).FirstOrDefault();
            string NameAt(int index) => Owner(index)?.Name ?? "(field)";
            string LineAt(int index)
            {
                var from = region.Code.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
                var to = region.Code.IndexOf('\n', index);
                return region.Code[from..(to < 0 ? region.Code.Length : to)].Trim();
            }

            // "Runs a capability", transitively within the file; and the literal capability ids each member runs.
            var names = members.Select(m => m.Name).Distinct().ToList();
            var calls = members.ToDictionary(m => m, m => names.Where(n => n != m.Name && Regex.IsMatch(m.Body, $@"(?<![\w.]){Regex.Escape(n)}\s*(?:<[^<>()]*>)?\s*\(")).ToList());
            HashSet<string> Close(IEnumerable<string> seed)
            {
                var set = seed.ToHashSet();
                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var m in members)
                        if (!set.Contains(m.Name) && calls[m].Any(set.Contains)) { set.Add(m.Name); changed = true; }
                }
                return set;
            }
            var runs = Close(members.Where(m => CodeRun.IsMatch(m.Body)).Select(m => m.Name));
            var ranIds = new Dictionary<string, HashSet<string>>();
            HashSet<string> RanIds(string name, HashSet<string> seen)
            {
                if (ranIds.TryGetValue(name, out var done)) return done;
                var ids = new HashSet<string>();
                if (!seen.Add(name)) return ids;
                foreach (var m in members.Where(mb => mb.Name == name))
                {
                    var body = CatalogRole.Replace(Regex.Replace(m.Body, @"\b(?:Require\w*Async|RefuseAsync)\(\s*""[^""]*""", "("), "");
                    foreach (Match id in CatalogIdLiteral.Matches(body))
                        if (Catalog.Find(id.Groups[1].Value) is not null) ids.Add(id.Groups[1].Value);
                    foreach (var callee in calls[m].Where(runs.Contains)) ids.UnionWith(RanIds(callee, seen));
                }
                return ranIds[name] = ids;
            }
            stats.Handlers += members.Count(m => runs.Contains(m.Name));

            // Gate fields: fields assigned from a role check (the id it was decided for, null for a listed role).
            var fields = FieldDeclaration.Matches(region.Code).Where(f => Owner(f.Index) is null)
                .SelectMany(f => f.Groups["names"].Value.Split(',').Select(n => n.Trim())).ToHashSet();
            var gates = new Dictionary<string, Gate>();
            foreach (Match g in GateAssignment.Matches(region.Code))
            {
                var name = g.Groups["name"].Value;
                if (name is "_role" or "required" || !fields.Contains(name)) continue;
                var id = CatalogRole.Match(g.Groups["rhs"].Value) is { Success: true } c && c.Groups["arg"].Value.StartsWith('"')
                    && !RoleLiteral.IsMatch(g.Groups["rhs"].Value) ? c.Groups["arg"].Value.Trim('"') : null;
                if (!gates.TryGetValue(name, out var gate)) gates[name] = gate = new Gate(id, []);
                else if (gate.Id != id) gates[name] = gate = gate with { Id = null };
                gate.Assignments.Add(g.Groups["name"].Index);
            }

            // Lines that state a role.
            var lineStart = 0;
            foreach (var raw in region.Code.Split('\n'))
            {
                var at = lineStart;
                lineStart += raw.Length + 1;
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var owner = Owner(at);
                var where = $"{file}:{owner?.Name ?? "(field)"}";
                var isListed = allowed.Contains((owner?.Name ?? "(field)", line)) || line == RoleDefault;

                if (ForbiddenRoleSource.IsMatch(line) && !isListed)
                    findings.Add($"{where}: '{line}' — RoleFor, default(AppRole) or an enumeration of roles");
                if (Regex.IsMatch(line, @"(?<![\w.])RequireRoleAsync\s*\(") && !line.Contains("Task<bool> RequireRoleAsync(", StringComparison.Ordinal)
                    && !RequireRoleCalls.Contains((file, owner?.Name ?? "(field)", line)))
                    findings.Add($"{where}: '{line}' — RequireRoleAsync outside its two call sites");
                foreach (Match c in CatalogRole.Matches(line))
                {
                    stats.CatalogChecks++;
                    var arg = c.Groups["arg"].Value;
                    if (arg.StartsWith('"') ? Catalog.Find(arg.Trim('"')) is null
                            : !(arg == "capabilityId" && owner is not null && Regex.IsMatch(owner.Name, @"^Require\w*Async$")))
                        findings.Add($"{where}: '{line}' — RoleOf of {arg}, not of a catalog id (or of capabilityId in a check helper)");
                }

                var literal = RoleLiteral.IsMatch(line);
                var hasRole = HasRole.Match(line);
                var comparison = RoleComparison.Match(line);
                var readRole = line.Contains("PageReads.", StringComparison.Ordinal);
                if (isListed && line != RoleDefault && owner is not null && runs.Contains(owner.Name))
                    findings.Add($"{where}: '{line}' — a listed line in a member that runs a capability");
                if (!literal && !hasRole.Success && !comparison.Success) continue;
                static bool FromCatalog(string side) => side.TrimStart().StartsWith("CapabilityCatalog.RoleOf(", StringComparison.Ordinal)
                    || Regex.IsMatch(side.TrimStart(), @"^required\b");
                var ok = literal ? isListed
                    : hasRole.Success ? FromCatalog(hasRole.Groups["arg"].Value) || isListed
                    : FromCatalog(comparison.Groups["rhs"].Value) || comparison.Groups["lhs"].Value.StartsWith("CapabilityCatalog.RoleOf", StringComparison.Ordinal)
                      || readRole || isListed;
                if (!ok)
                    findings.Add($"{where}: '{line}' — the role does not come from CapabilityCatalog.RoleOf");
                else if (readRole && owner is not null && runs.Contains(owner.Name))
                    findings.Add($"{where}: '{line}' — a read role in a member that runs a capability");
            }

            // Gate fields, denied by default over the whole region: besides their assignment and declaration, only on a
            // listed line in a member that runs nothing, or — decided for a catalog id — as `if (!gate)` in a handler.
            foreach (var (gate, info) in gates)
                foreach (Match u in new Regex($@"(?<![\w.]){Regex.Escape(gate)}\b").Matches(region.Code))
                {
                    if (info.Assignments.Contains(u.Index)) continue;
                    var owner = Owner(u.Index);
                    var line = LineAt(u.Index);
                    if (owner is null && FieldDeclaration.IsMatch(line)) continue;
                    stats.GateUses++;
                    var name = owner?.Name ?? "(field)";
                    if (owner is not null && runs.Contains(owner.Name))
                    {
                        if (info.Id is null)
                            findings.Add($"{file}:{name}: '{line}' — reads {gate} (a listed role) and runs a capability");
                        else if (!Regex.IsMatch(line, $@"\bif\s*\(\s*!?\s*{Regex.Escape(gate)}\s*\)"))
                            findings.Add($"{file}:{name}: '{line}' — uses {gate} other than as `if (!{gate})` where a capability runs");
                    }
                    else if (!allowed.Contains((name, line)))
                        findings.Add($"{file}:{name}: '{line}' — reads the gate field {gate} outside its listed lines");
                }

            // The local `required`: from the catalog, from PageReads, or the RequireRoleAsync parameter.
            foreach (Match r in Regex.Matches(region.Code, @"\brequired\s*=(?!=)\s*(?<rhs>[^;]*);"))
                if (!r.Groups["rhs"].Value.TrimStart().StartsWith("CapabilityCatalog.RoleOf(", StringComparison.Ordinal)
                    && !Regex.IsMatch(r.Groups["rhs"].Value, @"^\s*(?:Whiskers\.Components\.)?PageReads\.\w+\s*$"))
                    findings.Add($"{file}:{NameAt(r.Index)}: 'required = {r.Groups["rhs"].Value.Trim()}' — not from the catalog or PageReads");

            // A computed capability id runs only next to a catalog check of that very expression, through a hand-off
            // parameter every caller fills with a literal id, or listed.
            foreach (Match r in Regex.Matches(region.Code, @"(?<![\w.])Mutations\s*\.\s*Run\w*Async\s*\(\s*(?<x>[^,]+?)\s*,"))
            {
                var x = r.Groups["x"].Value.Trim();
                if (LiteralIds(x)) continue;
                var owner = Owner(r.Index);
                var name = owner?.Name ?? "(field)";
                if (ComputedRuns.Contains((file, name, x))) continue;
                if (owner is not null && Regex.IsMatch(owner.Body, $@"\bRequire\w*Async\(\s*{Regex.Escape(x)}\s*,|CapabilityCatalog\.RoleOf\(\s*{Regex.Escape(x)}\s*\)"))
                    continue;
                var position = owner is null ? -1 : Arguments(owner.Parameters).Select(p => Regex.Match(p, @"(\w+)\s*(?:=.*)?$", RegexOptions.Singleline).Groups[1].Value)
                    .ToList().IndexOf(x);
                if (position < 0)
                {
                    findings.Add($"{file}:{name}: runs the computed id {x} without RequireAsync({x}, …) or RoleOf({x}) in the same member");
                    continue;
                }
                var handOffs = HandOffArguments(StripComments(source), owner!.Name, position).ToList();
                if (handOffs.Count == 0)
                    findings.Add($"{file}:{name}: hands off {x}, but no caller passes it a literal capability id");
                foreach (var arg in handOffs.Where(a => !LiteralIds(a)))
                    findings.Add($"{file}:{name}: a caller hands it the computed id {arg}");
            }

            var readChecks = Close(members.Where(m => m.Body.Contains("PageReads.", StringComparison.Ordinal)).Select(m => m.Name));
            var roleTakers = Close(RoleParameters.Where(r => r.File == file && r.Member != "RequireRoleAsync").Select(r => r.Member));
            foreach (var member in members)
            {
                var running = runs.Contains(member.Name);
                if (Regex.IsMatch(member.Parameters, @"\bAppRole\b") && !RoleParameters.Contains((file, member.Name)))
                    findings.Add($"{file}:{member.Name}: takes a role ({member.Parameters}) — check by capability id");
                // A read role, or a role-taking display member, never reached from where a capability runs.
                if (running && (readChecks.Contains(member.Name) || roleTakers.Contains(member.Name)))
                    findings.Add($"{file}:{member.Name}: reaches a read role or a role-taking display member and runs a capability");
                if (Regex.IsMatch(member.Name, @"^Require\w*Async$") && !Regex.IsMatch(member.Parameters, @"\bAppRole\b")
                    && CodeRun.IsMatch(member.Body)
                    && !member.Body.Contains("CapabilityCatalog.RoleOf(capabilityId)", StringComparison.Ordinal))
                    findings.Add($"{file}:{member.Name}: a role-check helper that does not ask CapabilityCatalog.RoleOf(capabilityId)");

                // The literal check covers what the handler runs (literal ids, through same-file helpers too).
                if (!running) continue;
                var guards = LiteralCheck.Matches(member.Body).Select(g => g.Groups["id"].Value)
                    .Concat(Regex.IsMatch(member.Body, @"(?:<=|>=|<|(?<![=-])>)\s*required\b|\brequired\s*(?:<=|>=|<|>)")
                        ? Regex.Matches(member.Body, @"\brequired\s*=(?!=)\s*CapabilityCatalog\.RoleOf\(\s*""([^""]+)""\s*\)").Select(g => g.Groups[1].Value)
                        : [])
                    .Concat(gates.Where(g => g.Value.Id is not null
                            && Regex.IsMatch(member.Body, $@"\bif\s*\(\s*!?\s*{Regex.Escape(g.Key)}\s*\)"))
                        .Select(g => g.Value.Id!))
                    .Where(id => Catalog.Find(id) is not null)
                    .ToList();
                var ran = RanIds(member.Name, []);
                if (guards.Count == 0 || ran.Count == 0) continue;
                stats.Compared++;
                var guard = guards.Max(CapabilityCatalog.RoleOf);
                var needed = ran.Max(CapabilityCatalog.RoleOf);
                if (guard < needed)
                    findings.Add($"{file}:{member.Name}: checks {string.Join(",", guards)} ({guard}) but runs {string.Join(",", ran)} ({needed})");
            }
        }
        return findings;
    }

    /// <summary>A capability expression made of literal ids only: <c>"a.b"</c> or <c>cond ? "a.b" : "c.d"</c>.</summary>
    private static bool LiteralIds(string x) =>
        Regex.IsMatch(x, @"^""[^""]*""$") || Regex.IsMatch(x, @"^[^?]*\?\s*""[^""]*""\s*:\s*""[^""]*""$");

    /// <summary>The top-level comma-separated parts of an argument or parameter list.</summary>
    private static List<string> Arguments(string list)
    {
        var parts = new List<string>();
        var skeleton = Skeleton(list);
        var depth = 0;
        var from = 0;
        for (var i = 0; i < list.Length; i++)
        {
            if (skeleton[i] is '(' or '[' or '{' or '<') depth++;
            else if (skeleton[i] is ')' or ']' or '}' || (skeleton[i] == '>' && (i == 0 || skeleton[i - 1] != '='))) depth--;
            else if (skeleton[i] == ',' && depth == 0) { parts.Add(list[from..i].Trim()); from = i + 1; }
        }
        if (list.Trim().Length > 0) parts.Add(list[from..].Trim());
        return parts;
    }

    /// <summary>The argument at <paramref name="position"/> of every call of <paramref name="member"/> in the whole file
    /// (markup included), the declaration left out.</summary>
    private static IEnumerable<string> HandOffArguments(string source, string member, int position)
    {
        foreach (Match c in Regex.Matches(source, $@"(?<![\w.]){Regex.Escape(member)}\s*\("))
        {
            var open = c.Index + c.Length - 1;
            var depth = 0;
            var close = open;
            for (; close < source.Length; close++)
            {
                if (source[close] == '(') depth++;
                else if (source[close] == ')' && --depth == 0) break;
            }
            // The declaration is followed by its body ({ or =>), a call never is.
            if (Regex.IsMatch(source[Math.Min(close + 1, source.Length)..], @"^\s*(?:where\b[^{=]*)?(?:\{|=>)")) continue;
            var args = Arguments(source[(open + 1)..Math.Min(close, source.Length)]);
            yield return position < args.Count ? args[position] : "(none)";
        }
    }

    /// <summary>(file, member, trimmed line) of every code line of a component.</summary>
    private static IEnumerable<(string File, string Member, string Line)> MemberLines(string file, string source)
    {
        foreach (var region in CodeRegions(file, source))
        {
            var members = Members(region, file.EndsWith(".razor", StringComparison.Ordinal));
            var start = 0;
            foreach (var raw in region.Code.Split('\n'))
            {
                var at = start;
                start += raw.Length + 1;
                var owner = members.Where(mb => mb.Start <= at && at < mb.End).OrderByDescending(mb => mb.Start).FirstOrDefault();
                yield return (file, owner?.Name ?? "(field)", raw.Trim());
            }
        }
    }

    private static List<string> ComponentFiles()
    {
        var components = Path.Combine(SourceRoot(), "src", "Whiskers", "Components");
        return Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(components, "*.cs", SearchOption.AllDirectories))
            .ToList();
    }

    private static IEnumerable<string> ProductFiles(string product) =>
        Directory.EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(product, "*.razor", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>The C# of a component: each <c>@code { … }</c> / <c>@functions { … }</c> block of a .razor file, or the
    /// whole .cs file — comments removed and <c>this.</c> blanked (<see cref="Region.Code"/>), and also strings blanked
    /// (<see cref="Region.Skeleton"/>, same length) for brace matching.</summary>
    private static List<Region> CodeRegions(string file, string source)
    {
        static Region Of(string block)
        {
            static string NoThis(string s) => Regex.Replace(s, @"\bthis\s*\.", m => new string(' ', m.Length));
            return new Region(NoThis(StripComments(block)), NoThis(Skeleton(block)));
        }
        if (!file.EndsWith(".razor", StringComparison.Ordinal))
            return [Of(source)];
        var regions = new List<Region>();
        foreach (Match m in Regex.Matches(source, @"@(?:code|functions)\s*\{"))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchingBrace(Skeleton(source[open..]), 0);
            regions.Add(Of(source[(open + 1)..(open + close)]));
        }
        return regions;
    }

    /// <summary>A .razor file without its code blocks.</summary>
    private static string MarkupOnly(string source)
    {
        var text = new StringBuilder(source);
        foreach (Match m in Regex.Matches(source, @"@(?:code|functions)\s*\{"))
        {
            var open = m.Index + m.Length - 1;
            var close = open + MatchingBrace(Skeleton(source[open..]), 0);
            for (var i = m.Index; i <= close && i < text.Length; i++) if (text[i] != '\n') text[i] = ' ';
        }
        return text.ToString();
    }

    /// <summary>The members of a code region: methods (generic ones too), local functions and expression-bodied members
    /// (properties included), with their brace on the declaration line or the next; the body runs to the matching brace
    /// or to the end of the expression. A declaration needs a modifier, except at the top level of a .razor code block
    /// (where no statement can stand). A property with accessors is not a member: its lines count as "(field)".</summary>
    private static List<Member> Members(Region region, bool razor)
    {
        var depthAt = new int[region.Skeleton.Length + 1];
        for (var i = 0; i < region.Skeleton.Length; i++)
            depthAt[i + 1] = depthAt[i] + (region.Skeleton[i] == '{' ? 1 : region.Skeleton[i] == '}' ? -1 : 0);
        var members = new List<Member>();
        foreach (Match m in Regex.Matches(region.Skeleton,
                     @"(?m)^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(?<mods>(?:(?:private|protected|public|internal|static|async|override|virtual|sealed|new|partial|unsafe)\s+)*)" +
                     @"(?<type>[\w.<>\[\]?,() ]+?)\s+(?<name>\w+)\s*(?:<[^<>()\n]*>)?\s*(?:\((?<params>(?:[^()]|\([^()]*\))*)\)\s*(?:where[^{=]*)?)?(?<open>\{|=>)"))
        {
            if (!m.Groups["params"].Success && m.Groups["open"].Value == "{") continue;   // a property with accessors, a type
            if (m.Groups["mods"].Length == 0
                && (!razor || depthAt[m.Index] != 0
                    || !Regex.IsMatch(m.Groups["type"].Value.Trim(), @"^[A-Za-z_][\w.]*(?:<[\w.<>, ?\[\]]*>)?(?:\[\])*\??$")
                    || Regex.IsMatch(m.Groups["type"].Value.Trim(), @"^(?:return|await|else|new|throw|yield|case|using|var|if|while|for|foreach|switch|lock)$")))
                continue;
            var start = m.Index;
            var bodyStart = m.Index + m.Length;
            int end;
            if (m.Groups["open"].Value == "{")
                end = MatchingBrace(region.Skeleton, bodyStart - 1) + 1;
            else
            {
                var depth = 0;
                end = bodyStart;
                while (end < region.Skeleton.Length)
                {
                    var c = region.Skeleton[end];
                    if (c is '(' or '{' or '[') depth++;
                    else if (c is ')' or '}' or ']') depth--;
                    else if (c == ';' && depth <= 0) break;
                    end++;
                }
                end = Math.Min(end + 1, region.Skeleton.Length);
            }
            members.Add(new Member(m.Groups["name"].Value,
                m.Groups["params"].Success ? region.Code.Substring(m.Groups["params"].Index, m.Groups["params"].Length) : "",
                start, bodyStart, end, region.Code[bodyStart..Math.Min(end, region.Code.Length)], m.Groups["open"].Value == "=>"));
        }
        return members;
    }

    /// <summary>The offset in <paramref name="skeleton"/> of the brace closing the one at <paramref name="open"/> (the
    /// last offset if none).</summary>
    private static int MatchingBrace(string skeleton, int open)
    {
        var depth = 0;
        for (var i = open; i < skeleton.Length; i++)
        {
            if (skeleton[i] == '{') depth++;
            else if (skeleton[i] == '}' && --depth == 0) return i;
        }
        return skeleton.Length - 1;
    }

    private static string StripComments(string s) => Blank(s, strings: false);
    private static string Skeleton(string s) => Blank(s, strings: true);

    /// <summary>Replaces comments (and, with <paramref name="strings"/>, the contents of string and char literals) by
    /// spaces, keeping every offset and line break.</summary>
    private static string Blank(string s, bool strings)
    {
        var o = new StringBuilder(s);
        void Clear(int from, int to) { for (var k = from; k < to && k < o.Length; k++) if (o[k] != '\n') o[k] = ' '; }
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                var e = s.IndexOf('\n', i); if (e < 0) e = s.Length;
                Clear(i, e); i = e;
            }
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 ? s.Length : e + 2;
                Clear(i, e); i = e;
            }
            else if (s[i] == '"' || (s[i] is '@' or '$' && i + 1 < s.Length && (s[i + 1] == '"' || (s[i + 1] is '@' or '$' && i + 2 < s.Length && s[i + 2] == '"'))))
            {
                var q = s.IndexOf('"', i);
                var verbatim = s[i..q].Contains('@');
                var j = q + 1;
                if (!verbatim && s.AsSpan(q).StartsWith("\"\"\""))
                {
                    var e = s.IndexOf("\"\"\"", q + 3, StringComparison.Ordinal); j = e < 0 ? s.Length : e + 3;
                }
                else
                {
                    var interpolated = s[i..q].Contains('$');
                    var holes = 0;
                    while (j < s.Length)
                    {
                        var c = s[j];
                        if (interpolated && c == '{') { if (j + 1 < s.Length && s[j + 1] == '{' && holes == 0) { j += 2; continue; } holes++; }
                        else if (interpolated && c == '}' && holes > 0) holes--;
                        else if (holes == 0 && !verbatim && c == '\\') { j += 2; continue; }
                        else if (holes == 0 && c == '"')
                        {
                            if (verbatim && j + 1 < s.Length && s[j + 1] == '"') { j += 2; continue; }
                            j++; break;
                        }
                        else if (holes == 0 && !verbatim && c == '\n') break;
                        j++;
                    }
                }
                if (strings) Clear(q + 1, j - 1);
                i = j;
            }
            else if (s[i] == '\'' && i + 2 < s.Length && (s[i + 2] == '\'' || (s[i + 1] == '\\' && i + 3 < s.Length && s[i + 3] == '\'')))
            {
                var e = s[i + 1] == '\\' ? i + 4 : i + 3;
                if (strings) Clear(i + 1, e - 1);
                i = e;
            }
            else i++;
        }
        return o.ToString();
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}
