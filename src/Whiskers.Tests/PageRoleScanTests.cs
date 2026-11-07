using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Xunit;

namespace Whiskers.Tests;

/// <summary>
/// INV-13, held at the source: a page or panel takes every role it requires from the capability catalog
/// (<see cref="CapabilityCatalog.RoleOf"/>) and turns a role into a level only through
/// <see cref="CapabilityCatalog.LevelFor"/>. The scan is deny by default: in the C# of every component, each role
/// literal, <c>HasRoleAsync(</c> and role comparison must take its role from the catalog, or be one of the listed
/// lines below — each in front of a read or deciding only what is shown, never in a handler that runs a capability.
/// </summary>
public class PageRoleScanTests
{
    // ── The listed lines: role checks that guard no capability ────────────────────────────────────────────────

    /// <summary>A role a page states on its own, allowed only where it decides what is shown or loaded, or guards a
    /// read. Keyed by file and the exact (trimmed) line, so a new or changed role check is a finding. A listed line in
    /// a handler that runs a capability is a finding as well.</summary>
    private static readonly Dictionary<string, string[]> ReadAndViewLines = new()
    {
        // Admin-only pages and panels: whether to load and show their content. Their writes go through the gateway
        // (catalog level) or a handler that asks the catalog; _isAdmin itself is refused in any capability handler.
        ["AgentHistory.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"],
        ["AgentView.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"],
        ["AiOperationsView.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"],
        ["AiTriggersView.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"],
        ["AuditLog.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"],
        ["GuardrailsView.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"],
        ["SelfStatus.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);"],
        ["DiskHealthPanel.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"],
        ["IdentityBindingsPanel.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"],
        ["MetricRetentionPanel.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"],
        ["OutboundExceptionsPanel.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"],
        ["QuietHoursPanel.razor"] = ["_isAdmin = await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin);"],
        ["LocalAccountsPanel.razor"] =
        [
            "_isAdmin = await CurrentUser.HasRoleAsync(AppRole.Admin);",
            "private AppRole _newRole = AppRole.Viewer;",                            // the form's default role (data)
        ],
        // Settings holds secrets: an admin-only load gate; the role fields are form defaults (data, not checks).
        ["Settings.razor"] =
        [
            "if (_role < AppRole.Admin) return;",
            "private Whiskers.Models.AppRole _defaultRole = Whiskers.Models.AppRole.Viewer;",
            "private Whiskers.Models.AppRole _newRoleRole = Whiskers.Models.AppRole.Operator;",
        ],
        // Reads: artisan migrate:status, nginx -t, a container's environment (MIT-37), masked check targets.
        ["ContainerDatabasePanel.razor"] =
        [
            "if (!Require(AppRole.Operator)) return;",
            "[Parameter] public AppRole Role { get; set; } = AppRole.Viewer;",       // the parent's role, fail closed
        ],
        ["NginxManager.razor"] = ["if (!Require(AppRole.Admin)) return;"],
        ["ContainerDiff.razor"] = ["if (_role < AppRole.Operator) return null;"],
        ["EndpointChecks.razor"] =
        [
            "if (role < AppRole.Admin && target.IndexOf('?') is >= 0 and var query)",
            "role >= AppRole.Admin ? detail ?? \"\" : EndpointCheckRunner.MaskInternalAddresses(detail);",
        ],
        // What is shown: tabs, the first-server card, the add dialog from a link, the RoleGuard default.
        ["ContainerDetail.razor"] =
        [
            "private int EnvTabIndex => _role >= AppRole.Operator ? 5 : -1;",
            "private int DbTabIndex => _container?.IsDatabase == true && _role >= AppRole.Operator ? 6 : -1;",
        ],
        ["Dashboard.razor"] = ["_showFirstServerCard = _role >= AppRole.Admin &&"],
        ["Servers.razor"] = ["if (add is \"onboard\" or \"classic\" && _role >= AppRole.Admin)"],
        ["RoleGuard.razor"] = ["[Parameter] public Whiskers.Models.AppRole RequiredRole { get; set; } = Whiskers.Models.AppRole.Operator;"],
        // "Run now": the task's own requirement from SchedulerRequirementResolver, held to Admin when it cannot answer.
        ["ScheduledTasksView.razor"] = [": AppRole.Admin;", "return AppRole.Admin;"],
    };

    /// <summary>Lines every page may hold: the role before it is loaded (Viewer, fail closed), and the comparison inside
    /// the snackbar helper <c>Require(AppRole required)</c>, whose callers are scanned themselves.</summary>
    private static readonly string[] AnyPageLines =
    [
        "private AppRole _role = AppRole.Viewer;",
        "if (_role < required)",
    ];

    // ── The tests ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_component_states_its_own_role_in_front_of_a_capability()
    {
        var files = ComponentFiles();
        var findings = files.SelectMany(f => Findings(Path.GetFileName(f), File.ReadAllText(f), ReadAndViewLines)).ToList();
        Assert.True(findings.Count == 0, "INV-13: " + string.Join("\n", findings));

        // The scan sees what it is about: many handlers run a capability, and many role checks ask the catalog.
        var members = files.SelectMany(f => CodeRegions(Path.GetFileName(f), File.ReadAllText(f))
            .SelectMany(r => Members(r))).ToList();
        var handlers = members.Count(m => RunsCapability.IsMatch(m.Body));
        Assert.True(handlers > 120, $"the member scan sees only {handlers} handlers that run a capability");
        var asking = files.Sum(f => Regex.Matches(File.ReadAllText(f), @"CapabilityCatalog\.RoleOf\(").Count);
        Assert.True(asking > 25, $"only {asking} role checks ask the catalog");
    }

    /// <summary>Every listed line still exists where it is listed, so the list cannot hide a stale entry.</summary>
    [Fact]
    public void Every_listed_role_line_still_exists()
    {
        var byName = ComponentFiles().ToDictionary(Path.GetFileName, f => CodeLines(Path.GetFileName(f), File.ReadAllText(f)));
        foreach (var (file, lines) in ReadAndViewLines)
            foreach (var line in lines)
                Assert.True(byName.TryGetValue(file, out var code) && code.Contains(line), $"{file}: '{line}' is listed but gone");
    }

    /// <summary>The scan catches each way around it the review named — a gate field set at initialisation, a
    /// page-defined <c>RoleOf</c>, a renamed helper that takes a role, <c>using static</c>, a constant or a cast, a
    /// one-line member, a brace on the declaration line, a role check hidden behind a comment, a handler in markup and
    /// a check weaker than what the handler runs — and leaves the catalog forms, listed lines and markup alone.</summary>
    [Theory]
    [InlineData("gate field", "    private bool _may;\n    protected override async Task OnInitializedAsync()\n    {\n        _may = await CurrentUser.HasRoleAsync(AppRole.Operator);\n    }\n    private async Task Save()\n    {\n        if (!_may) { await Mutations.RefuseAsync(\"config.agent\", new { }, \"no\"); return; }\n    }\n")]
    [InlineData("gate field by comparison", "    private bool _may;\n    protected override async Task OnInitializedAsync()\n    {\n        _may = (await CurrentUser.GetRoleAsync()) >= AppRole.Operator;\n    }\n")]
    [InlineData("page RoleOf", "    private AppRole RoleOf(string id) => AppRole.Operator;\n")]
    [InlineData("page RoleOf through the catalog", "    private AppRole RoleOf(string id) => CapabilityCatalog.RoleOf(id);\n")]
    [InlineData("renamed helper", "    private async Task<bool> GuardAsync(AppRole needed, string cap, object args)\n    {\n        if (_role >= needed) return true;\n        await Mutations.RefuseAsync(cap, args, \"no\");\n        return false;\n    }\n")]
    [InlineData("literal to a helper", "    private async Task Go()\n    {\n        if (!await RequireAsync(AppRole.Admin, \"container.remove\", new { })) return;\n    }\n")]
    [InlineData("computed id", "    private async Task Go(ContainerInfo c)\n    {\n        var (capability, arguments) = RowAction(c, \"start\");\n        if (!await RequireAsync(AppRole.Operator, capability, arguments)) return;\n    }\n")]
    [InlineData("const alias", "    private const AppRole Needed = AppRole.Admin;\n")]
    [InlineData("cast", "    private async Task Go()\n    {\n        if (_role < (AppRole)2) return;\n        await Mutations.RunAsync(\"x.y\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("one-line member", "    private Task<bool> Go() => RequireAsync(AppRole.Admin, \"x.y\", new { });\n")]
    [InlineData("brace on the declaration line", "    private async Task Go() {\n        if (!_isAdmin) { await Mutations.RefuseAsync(\"config.agent\", new { }, \"no\"); return; } }\n")]
    [InlineData("isAdmin gate", "    private async Task Go()\n    {\n        if (!_isAdmin)\n        {\n            await Mutations.RefuseAsync(\"config.agent\", new { }, \"no\");\n            return;\n        }\n    }\n")]
    [InlineData("comment instead of the catalog", "    private async Task<bool> RequireAsync(string capabilityId, object arguments)\n    {\n        // CapabilityCatalog.RoleOf(capabilityId)\n        if (Require(AppRole.Operator)) return true;\n        await Mutations.RefuseAsync(capabilityId, arguments, \"no\");\n        return false;\n    }\n")]
    [InlineData("helper without the catalog", "    private async Task<bool> RequireAsync(string capabilityId, object arguments)\n    {\n        if (Require(_needed)) return true;\n        await Mutations.RefuseAsync(capabilityId, arguments, \"no\");\n        return false;\n    }\n")]
    [InlineData("plain Require in front of a mutation", "    private async Task Go()\n    {\n        if (!Require(AppRole.Operator)) return;\n        await Mutations.RunAsync(\"container.start\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("HasRoleAsync with a literal", "    private async Task Go()\n    {\n        if (!await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin)) { await Mutations.RefuseAsync(\"x.y\", new { }, \"no\"); return; }\n    }\n")]
    [InlineData("weaker check than what runs", "    private async Task Go()\n    {\n        if (!await RequireAsync(\"container.start\", new { })) return;\n        await Mutations.RunAsync(\"container.remove\", new { }, _ => Task.CompletedTask);\n    }\n")]
    [InlineData("listed line in a capability handler", "    private async Task Go()\n    {\n        if (_role < AppRole.Admin) return;\n        await Mutations.RunAsync(\"config.save\", new { }, _ => Task.CompletedTask);\n    }\n")]
    public void The_scan_catches_a_way_around_the_catalog(string what, string code)
    {
        var listed = new Dictionary<string, string[]> { ["P.razor"] = ["if (_role < AppRole.Admin) return;"] };
        Assert.True(Findings("P.razor", Page(code), listed).Count > 0, $"not caught: {what}");
    }

    [Theory]
    [InlineData("using static", "@using static Whiskers.Models.AppRole\n<p>x</p>\n@code {\n}\n")]
    [InlineData("handler in markup", "<button @onclick=\"@(async () => await Mutations.RunAsync(\"x.y\", new { }, _ => Task.CompletedTask))\">x</button>\n@code {\n}\n")]
    public void The_scan_catches_a_way_around_the_catalog_outside_the_code_block(string what, string page) =>
        Assert.True(Findings("P.razor", page, new()).Count > 0, $"not caught: {what}");

    [Fact]
    public void The_scan_leaves_the_catalog_forms_listed_reads_and_markup_alone()
    {
        var listed = new Dictionary<string, string[]> { ["P.razor"] = ["if (!Require(AppRole.Admin)) return;"] };
        const string code =
            "    private AppRole _role = AppRole.Viewer;\n" +
            "    private bool _may;\n" +
            "    private bool Require(AppRole required)\n    {\n        if (_role < required)\n        {\n            return false;\n        }\n        return true;\n    }\n" +
            "    private async Task<bool> RequireAsync(string capabilityId, object arguments)\n    {\n" +
            "        // A role from the table.\n        var required = CapabilityCatalog.RoleOf(capabilityId);\n" +
            "        if (Require(required)) return true;\n        await Mutations.RefuseAsync(capabilityId, arguments, $\"Requires the {required} role.\");\n        return false;\n    }\n" +
            "    protected override async Task OnInitializedAsync()\n    {\n        _may = await CurrentUser.HasRoleAsync(CapabilityCatalog.RoleOf(\"config.agent\"));\n    }\n" +
            "    private async Task Remove() {\n        if (!await RequireAsync(\"container.remove\", new { })) return;\n        await Mutations.RunAsync(\"container.stop\", new { }, _ => Task.CompletedTask); }\n" +
            "    private async Task Open()\n    {\n        if (await CurrentUser.GetRoleAsync() < CapabilityCatalog.RoleOf(\"terminal.host_open\")) return;\n" +
            "        await Mutations.RunAsync(\"terminal.host_open\", new { }, _ => Task.CompletedTask);\n    }\n" +
            "    private async Task TestConfig()\n    {\n        if (!Require(AppRole.Admin)) return;\n        var text = \"{ unbalanced\";\n    }\n" +
            "    private string Note => \"it's // not a comment\";\n";
        var page = "@if (_role >= AppRole.Admin) { <b>don't care: shown only</b> }\n" + Page(code);
        Assert.Empty(Findings("P.razor", page, listed));
    }

    /// <summary>The fixed rule has one spelling in the product (<see cref="CapabilityCatalog.LevelFor"/> and its
    /// inverse <see cref="CapabilityCatalog.RoleFor"/>): no other file turns a role literal into a level or back — no
    /// switch, ternary or if.</summary>
    [Fact]
    public void The_role_level_rule_is_spelled_only_in_the_catalog()
    {
        var product = Path.Combine(SourceRoot(), "src", "Whiskers");
        var findings = Directory.EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(product, "*.razor", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) != "CapabilityCatalog.cs")
            .SelectMany(f => RoleLevelSpellings(StripComments(File.ReadAllText(f))).Select(s => $"{Path.GetFileName(f)}: {s}"))
            .ToList();
        Assert.True(findings.Count == 0, string.Join("\n", findings));

        var catalog = StripComments(File.ReadAllText(Path.Combine(product, "Services", "Mutations", "CapabilityCatalog.cs")));
        Assert.Equal(4, RoleLevelSpellings(catalog).Count);   // the two explicit arms of LevelFor and of RoleFor
        Assert.True(Regex.Matches(string.Join("\n", Directory.EnumerateFiles(product, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".razor")).Select(File.ReadAllText)), @"CapabilityCatalog\.LevelFor\(").Count >= 10,
            "the surfaces no longer call LevelFor");
    }

    [Theory]
    [InlineData("var level = role switch\n{\n    AppRole.Admin => McpPermissionLevels.Admin,\n    AppRole.Operator => McpPermissionLevels.Write,\n    _ => McpPermissionLevels.Read,\n};")]
    [InlineData("var level = role == AppRole.Admin ? McpPermissionLevels.Admin : McpPermissionLevels.Read;")]
    [InlineData("if (role >= AppRole.Operator) { level = McpPermissionLevels.Write; }")]
    [InlineData("var role = level == McpPermissionLevels.Admin ? AppRole.Admin : AppRole.Viewer;")]
    [InlineData("var role = McpPermissionLevels.Normalize(level) switch { McpPermissionLevels.Write => Whiskers.Models.AppRole.Operator, _ => Whiskers.Models.AppRole.Viewer };")]
    public void The_rule_scan_catches_a_second_spelling(string code) => Assert.NotEmpty(RoleLevelSpellings(code));

    [Fact]
    public void The_rule_scan_leaves_unrelated_role_and_level_code_alone() =>
        Assert.Empty(RoleLevelSpellings("var level = CapabilityCatalog.LevelFor(role);\nif (_role < AppRole.Admin) return;\nvar l = McpPermissionLevels.Read;"));

    private static List<string> RoleLevelSpellings(string code) => Regex.Matches(code,
            @"(?:Whiskers\.Models\.)?AppRole\.(?:Admin|Operator|Viewer)\b[^;]{0,200}?McpPermissionLevels\.(?:Admin|Write|Read)\b" +
            @"|McpPermissionLevels\.(?:Admin|Write|Read)\b[^;]{0,200}?(?:Whiskers\.Models\.)?AppRole\.(?:Admin|Operator|Viewer)\b")
        .Select(m => Regex.Replace(m.Value, @"\s+", " ")).ToList();

    // ── The scan ──────────────────────────────────────────────────────────────────────────────────────────────

    private static string Page(string code) => "<p>page</p>\n@code {\n" + code + "}\n";

    private static readonly Regex RunsCapability = new(@"(?<![\w.])Mutations\.\w+|\bRefuseAsync\(|\bRequire\w*Async\(");
    private static readonly Regex RoleLiteral = new(@"(?:Whiskers\.Models\.)?\bAppRole\.(?:Admin|Operator|Viewer)\b");
    private static readonly Regex HasRole = new(@"\bHasRoleAsync\s*\(");
    private static readonly Regex RoleComparison = new(
        @"(?:\b_?[rR]ole\b|GetRoleAsync\(\)\)?|\.Role\b)\s*(?:<=|>=|<(?![<=])|>(?![>=]))|(?:<=|>=|(?<![<=])<|(?<![>=-])>)\s*\(?\s*(?:\b_?[rR]ole\b)");
    private static readonly Regex IsAdmin = new(@"\b_?isAdmin\b");
    private static readonly Regex Alias = new(
        @"\busing\s+static\s+[\w.]*\bAppRole\b|\bconst\s+(?:Whiskers\.Models\.)?AppRole\b|\bstatic\s+readonly\s+(?:Whiskers\.Models\.)?AppRole\b" +
        @"|\(\s*(?:Whiskers\.Models\.)?AppRole\s*\)\s*[\w(-]|\bAppRole\s+RoleOf\s*\(|\bEnum\.(?:Parse|TryParse)\s*<\s*(?:Whiskers\.Models\.)?AppRole\b");

    private sealed record Member(string Name, string Parameters, int Start, int End, string Body);
    private sealed record Region(string Code, string Skeleton);

    /// <summary>Every rule's findings for one component file: "file:member: line — why".</summary>
    private static List<string> Findings(string file, string source, Dictionary<string, string[]> listed)
    {
        var findings = new List<string>();
        var allowed = listed.GetValueOrDefault(file, []);

        // Directives and markup: a role alias anywhere, and no handler that runs a capability outside the code block.
        foreach (Match m in Alias.Matches(StripComments(source)))
            findings.Add($"{file}: '{m.Value.Trim()}' — a role alias or page-defined RoleOf");
        if (file.EndsWith(".razor", StringComparison.Ordinal))
        {
            var markup = MarkupOnly(source);
            foreach (Match m in RunsCapability.Matches(markup))
                findings.Add($"{file}: '{m.Value}' in markup — handlers that run a capability belong in @code");
        }

        foreach (var region in CodeRegions(file, source))
        {
            var members = Members(region);
            string MemberAt(int index) => members.LastOrDefault(mb => mb.Start <= index && index < mb.End)?.Name ?? "(field)";
            Member? Owner(int index) => members.LastOrDefault(mb => mb.Start <= index && index < mb.End);

            var lines = region.Code.Split('\n');
            var offset = 0;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                var at = offset;
                offset += raw.Length + 1;
                if (line.Length == 0) continue;
                var literal = RoleLiteral.IsMatch(line);
                var hasRole = HasRole.IsMatch(line);
                var comparison = RoleComparison.IsMatch(line);
                if (!literal && !hasRole && !comparison) continue;

                var fromCatalog = line.Contains("CapabilityCatalog.RoleOf(", StringComparison.Ordinal);
                var byParameter = Regex.IsMatch(line, @"(?:<=|>=|<|>)\s*required\b|HasRoleAsync\(\s*required\b");
                var isListed = allowed.Contains(line) || AnyPageLines.Contains(line);
                var ok = literal ? isListed : fromCatalog || byParameter || isListed;
                if (!ok)
                {
                    findings.Add($"{file}:{MemberAt(at)}: '{line}' — the role does not come from CapabilityCatalog.RoleOf");
                    continue;
                }
                if ((literal || isListed) && !fromCatalog && Owner(at) is { } owner && RunsCapability.IsMatch(owner.Body)
                    && !AnyPageLines.Contains(line))
                    findings.Add($"{file}:{owner.Name}: '{line}' — a listed role check in a handler that runs a capability");
            }

            foreach (var member in members)
            {
                var runs = RunsCapability.IsMatch(member.Body);
                if (runs && IsAdmin.IsMatch(member.Body))
                    findings.Add($"{file}:{member.Name}: _isAdmin in a handler that runs a capability");
                if (Regex.IsMatch(member.Body, @"\bRefuseAsync\(") && Regex.IsMatch(member.Parameters, @"\bAppRole\b")
                    && !(member.Name == "RequireRoleAsync" && file == "ScheduledTasksView.razor"))
                    findings.Add($"{file}:{member.Name}: a refusing helper takes a role ({member.Parameters})");
                if (Regex.IsMatch(member.Name, @"^Require\w*Async$") && !Regex.IsMatch(member.Parameters, @"\bAppRole\b")
                    && Regex.IsMatch(member.Body, @"\bRefuseAsync\(|\bRequire\w*Async\(")
                    && !member.Body.Contains("CapabilityCatalog.RoleOf(capabilityId)", StringComparison.Ordinal))
                    findings.Add($"{file}:{member.Name}: a role-check helper that does not ask CapabilityCatalog.RoleOf(capabilityId)");

                // m1: the check covers what the handler runs.
                var guards = Regex.Matches(member.Body, @"\bRequire\w*Async\(\s*""([a-z_]+\.[a-z_.]+)""").Select(g => g.Groups[1].Value).ToList();
                var ran = Regex.Matches(member.Body, @"Mutations\.Run\w*Async(?:<[^>]*>)?\(\s*""([a-z_]+\.[a-z_.]+)""").Select(g => g.Groups[1].Value).ToList();
                if (guards.Count > 0 && ran.Count > 0)
                {
                    var guard = guards.Max(CapabilityCatalog.RoleOf);
                    var needed = ran.Max(CapabilityCatalog.RoleOf);
                    if (guard < needed)
                        findings.Add($"{file}:{member.Name}: checks {string.Join(",", guards)} ({guard}) but runs {string.Join(",", ran)} ({needed})");
                }
            }
        }
        return findings;
    }

    private static List<string> CodeLines(string file, string source) =>
        CodeRegions(file, source).SelectMany(r => r.Code.Split('\n')).Select(l => l.Trim()).ToList();

    private static IEnumerable<string> ComponentFiles()
    {
        var components = Path.Combine(SourceRoot(), "src", "Whiskers", "Components");
        return Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(components, "*.cs", SearchOption.AllDirectories))
            .ToList();
    }

    /// <summary>The C# of a component: each <c>@code { … }</c> / <c>@functions { … }</c> block of a .razor file, or the
    /// whole .cs file — comments removed (<see cref="Region.Code"/>) and also strings blanked
    /// (<see cref="Region.Skeleton"/>, same length) for brace matching.</summary>
    private static List<Region> CodeRegions(string file, string source)
    {
        if (!file.EndsWith(".razor", StringComparison.Ordinal))
            return [new Region(StripComments(source), Skeleton(source))];
        var regions = new List<Region>();
        foreach (Match m in Regex.Matches(source, @"@(?:code|functions)\s*\{"))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchingBrace(Skeleton(source[open..]), 0);
            var block = source[(open + 1)..(open + close)];
            regions.Add(new Region(StripComments(block), Skeleton(block)));
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

    /// <summary>The members of a code region: methods, local functions and expression-bodied members, with their brace
    /// on the declaration line or the next; the body runs to the matching brace or to the end of the expression.</summary>
    private static List<Member> Members(Region region)
    {
        var members = new List<Member>();
        foreach (Match m in Regex.Matches(region.Skeleton,
                     @"(?m)^[ \t]*(?:(?:private|protected|public|internal|static|async|override|virtual|sealed|new|partial|unsafe)\s+)+" +
                     @"[\w.<>\[\]?,() ]+?\s+(\w+)\s*\(((?:[^()]|\([^()]*\))*)\)\s*(?:where[^{=]*)?(\{|=>)"))
        {
            var start = m.Index;
            var bodyStart = m.Index + m.Length;
            int end;
            if (m.Groups[3].Value == "{")
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
            members.Add(new Member(m.Groups[1].Value, region.Code.Substring(m.Groups[2].Index, m.Groups[2].Length), start, end,
                region.Code[bodyStart..Math.Min(end, region.Code.Length)]));
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
