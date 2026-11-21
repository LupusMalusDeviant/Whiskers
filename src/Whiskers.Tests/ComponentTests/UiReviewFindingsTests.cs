using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// Review of 1.0.0, work package T3 (the remaining UI findings): each test failed on the reviewed code and pins what the
/// page does now — a setting no panel shows survives its save, a refused or failed save says what happened, a filter
/// offers what it searches, a button is offered only where the gateway allows it, a tab switch neither empties the
/// terminal nor loads the .env twice.
/// </summary>
public sealed class UiReviewFindingsTests : BunitContext, IAsyncLifetime
{
    private readonly ServersPauseResumeRoleTests.MutableUser _user = new(AppRole.Admin);

    public UiReviewFindingsTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddOptions();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(Null<IKeyInterceptorService>());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<IUiMutationRunner>(new PassThrough());
        Services.AddSingleton(Stub<Whiskers.Modules.IModuleRegistry>().On("IsEnabled", _ => true).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    // ------------------------------------------------------------------ harness

    private static T Null<T>() where T : class => new UiCreatedResourceAuditTests.Stub<T>().Object;
    private static UiCreatedResourceAuditTests.Stub<T> Stub<T>() where T : class => new();

    private static FieldInfo FieldOf(Type type, string field) =>
        type.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.Name, field);

    private static void Set(object instance, string field, object? value) => FieldOf(instance.GetType(), field).SetValue(instance, value);

    private static T Get<T>(object instance, string field) => (T)FieldOf(instance.GetType(), field).GetValue(instance)!;

    private static Task Invoke<TComponent>(IRenderedComponent<TComponent> page, string handler, params object?[] args)
        where TComponent : IComponent
    {
        var method = typeof(TComponent).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == handler && m.GetParameters().Length == args.Length);
        return page.InvokeAsync(() => method.Invoke(page.Instance, args) as Task ?? Task.CompletedTask);
    }

    private List<Snackbar> Snackbars() => Services.GetRequiredService<ISnackbar>().ShownSnackbars.ToList();

    private void TryAdd<T>(T instance) where T : class
    {
        if (Services.All(d => d.ServiceType != typeof(T))) Services.AddSingleton(instance);
    }

    private static string Source(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, "src", "Whiskers", .. relative]));
    }

    private static IEnumerable<(string Name, string Text)> RazorSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx"))) dir = dir.Parent;
        var root = Path.Combine(dir!.FullName, "src", "Whiskers", "Components");
        return Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(root, f), File.ReadAllText(f)));
    }

    // ------------------------------------------------------------------ settings

    private IRenderedComponent<Settings> SettingsPage(Action<IServiceCollection>? more = null)
    {
        Services.AddSingleton(Null<Whiskers.Services.Cve.ICveFindingsStore>());
        Services.AddSingleton(Null<IWhitelistService>());
        Services.AddSingleton(Null<Whiskers.Services.Vault.IVaultService>());
        Services.AddSingleton(Null<Whiskers.Services.Mcp.IMcpPermissionService>());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        more?.Invoke(Services);
        TryAdd(Null<Whiskers.Services.Cve.ICveMonitorService>());
        TryAdd(Null<INotificationService>());
        TryAdd(Null<IMatrixNotificationService>());
        TryAdd(Null<IRoleService>());
        TryAdd(Null<Whiskers.Services.Persistence.IAppSettingsStore>());
        // Rendered for a Viewer (the access-denied panel), as SettingsHandlerRoleTests does; the handlers decide by the
        // role held at the click.
        _user.Role = AppRole.Viewer;
        var page = Render<Settings>();
        _user.Role = AppRole.Admin;
        return page;
    }

    [Fact]
    public async Task Saving_health_monitoring_keeps_the_unreachable_cycle_counts_the_panel_does_not_show()
    {
        object? saved = null;
        var page = SettingsPage(s => s
            .Configure<HealthMonitorSettings>(o => { o.ServerUnreachableCycles = 7; o.ServerUnreachableColdStartCycles = 33; })
            .AddSingleton(Stub<Whiskers.Services.Persistence.IAppSettingsStore>()
                .On("SaveSectionAsync", a => { saved = a[1]; return Task.CompletedTask; }).Object));

        await Invoke(page, "SaveHealth");

        var health = Assert.IsType<HealthMonitorSettings>(saved);
        Assert.Equal(7, health.ServerUnreachableCycles);
        Assert.Equal(33, health.ServerUnreachableColdStartCycles);
    }

    [Fact]
    public async Task Adding_an_address_that_already_has_a_role_is_refused_and_saves_nothing()
    {
        var saves = 0;
        var page = SettingsPage(s => s.AddSingleton(Stub<IRoleService>()
            .On("SaveRoleDataAsync", _ => { saves++; return Task.CompletedTask; }).Object));
        Set(page.Instance, "_roleEntries", new List<UserRoleEntry> { new() { Email = "ops@example.com", Role = AppRole.Viewer } });
        Set(page.Instance, "_newRoleEmail", " OPS@example.com ");

        await Invoke(page, "AddRole");

        Assert.Single(Get<List<UserRoleEntry>>(page.Instance, "_roleEntries"));
        Assert.Equal(0, saves);
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Warning && s.Message.ToString()!.Contains("Settings_Snackbar_RoleExists"));
    }

    [Theory]
    [InlineData(true, "Settings_Snackbar_LastAdmin")]
    [InlineData(false, "Settings_Snackbar_SaveError")]
    public async Task A_role_save_reports_the_last_admin_refusal_only_for_that_refusal(bool lastAdmin, string expected)
    {
        var page = SettingsPage(s => s.AddSingleton(Stub<IRoleService>()
            .On("GetRoleData", _ => new UserRoleData())
            .On("SaveRoleDataAsync", _ => Task.FromException(lastAdmin
                ? new LastAdminException()
                : new InvalidOperationException("The role store is locked by another writer."))).Object));

        await Invoke(page, "SaveRoles");

        var error = Assert.Single(Snackbars(), s => s.Severity == Severity.Error);
        Assert.Contains(expected, error.Message.ToString());
    }

    [Fact]
    public async Task The_mattermost_test_sends_to_mattermost_only()
    {
        var everyChannel = 0;
        var mattermost = 0;
        var page = SettingsPage(s => s
            .AddSingleton(Stub<INotificationService>().On("SendTestAsync", _ => { everyChannel++; return Task.CompletedTask; }).Object)
            .AddSingleton(Stub<IMattermostNotificationService>().On("SendTestAsync", _ => { mattermost++; return Task.CompletedTask; }).Object));

        await Invoke(page, "TestMattermost");

        Assert.Equal(1, mattermost);
        Assert.Equal(0, everyChannel);
    }

    private const string LeakySecret = "Hunter2Secret";

    [Fact]
    public async Task A_failed_manual_cve_scan_is_shown_without_its_secret()
    {
        var page = SettingsPage(s => s
            .Configure<Microsoft.Extensions.Localization.LocalizationOptions>(o => o.ResourcesPath = "Resources")
            .AddSingleton(Stub<Whiskers.Services.Cve.ICveMonitorService>().On("RunOneCycleAsync",
                _ => Task.FromException<bool>(new IOException($"trivy failed for admin@example.com: password={LeakySecret}"))).Object));

        await Invoke(page, "TriggerCveScan");

        page.WaitForAssertion(() => Assert.Contains(Snackbars(), s => s.Severity == Severity.Error), TimeSpan.FromSeconds(5));
        var text = Snackbars().Single(s => s.Severity == Severity.Error).Message.ToString() ?? "";
        Assert.False(text.Contains(LeakySecret, StringComparison.Ordinal), "The manual CVE scan shows its error unredacted: " + text);
        Assert.Contains("password=***", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ server terminal

    [Fact]
    public void The_back_arrow_of_the_server_terminal_leads_back_to_the_dashboard()
    {
        Services.AddSingleton(Null<Whiskers.Services.Terminal.ITerminalSessionManager>());
        Services.AddSingleton(Null<IServerConfigService>());
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("server-terminal/local");
        _user.Role = AppRole.Viewer;   // no shell is opened for this test
        var page = Render<ServerTerminalView>(p => p.Add(x => x.ServerId, "local"));

        page.Find(".mud-icon-button").Click();

        Assert.Equal(nav.BaseUri, nav.Uri);
    }

    // ------------------------------------------------------------------ health reports

    [Fact]
    public void The_health_report_filter_offers_the_containers_of_every_server()
    {
        Services.AddSingleton(Null<Whiskers.Services.HealthMonitor.IHealthStore>());
        Services.AddSingleton(Stub<IDockerService>()
            .On(nameof(IDockerService.ListContainersAsync), _ => Task.FromResult<IList<ContainerInfo>>(
                [new ContainerInfo { Id = "c1", Name = "web", ServerId = "local", ServerName = "Local" }]))
            .On(nameof(IDockerService.ListAllContainersAsync), _ => Task.FromResult<IList<ContainerInfo>>(
                [
                    new ContainerInfo { Id = "c1", Name = "web", ServerId = "local", ServerName = "Local" },
                    new ContainerInfo { Id = "c2", Name = "db", ServerId = "edge", ServerName = "Edge" },
                ])).Object);

        var page = Render<HealthReports>();

        Assert.Equal(["c1", "c2"], Get<List<ContainerInfo>>(page.Instance, "_containers").Select(c => c.Id));
    }

    // ------------------------------------------------------------------ container detail: terminal and .env tabs

    private sealed class ShellCounter
    {
        public int Opens;
        public int EnvReads;
        public TaskCompletionSource<List<KeyValuePair<string, string>>> Env { get; } = new();
    }

    private IRenderedComponent<ContainerDetail> ContainerDetailPage(ShellCounter counter)
    {
        var container = new ContainerInfo
        {
            Id = "abc123", Name = "web", State = "running", ServerId = "local", ServerName = "Local",
            Labels = new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = "/opt/deployments/web" },
        };
        Services.AddSingleton(Stub<IDockerService>()
            .On(nameof(IDockerService.GetContainerAsync), _ => Task.FromResult<ContainerInfo?>(container))
            .On(nameof(IDockerService.GetContainerEnvAsync), _ => { counter.EnvReads++; return counter.Env.Task; }).Object);
        Services.AddSingleton(Null<Whiskers.Services.HealthMonitor.IHealthStore>());
        Services.AddSingleton(Stub<Whiskers.Services.Terminal.ITerminalSessionManager>().On("OpenContainerShellAsync", _ =>
        {
            counter.Opens++;
            return Task.FromException<Whiskers.Services.Terminal.TerminalSession>(new InvalidOperationException("no shell in this test"));
        }).Object);
        Services.AddSingleton(Null<Whiskers.Services.Metrics.IMetricsQueryService>());
        Services.AddSingleton(Stub<IHostCommandExecutor>().On("ExecuteAsync",
            _ => Task.FromResult(new CommandResult { ExitCode = 0, Output = "present\nA=1\nB=2\n" })).Object);
        Services.AddSingleton(Stub<IServerConfigService>().On("SupportsTerminal", _ => true).Object);
        Services.AddSingleton(Null<Whiskers.Services.Cve.ICveFindingsStore>());
        Services.AddSingleton(Null<IComposePlanner>());
        Services.AddSingleton(Null<IComposeRunner>());
        return Render<ContainerDetail>(p => p.Add(x => x.ServerId, "local").Add(x => x.ContainerId, "abc123"));
    }

    private static void OpenTab(IRenderedComponent<ContainerDetail> page, int index) =>
        page.FindAll(".mud-tab")[index].Click();

    [Fact]
    public void Coming_back_to_the_terminal_tab_opens_a_new_shell_in_the_new_terminal()
    {
        var counter = new ShellCounter();
        var page = ContainerDetailPage(counter);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".mud-tab")));

        OpenTab(page, 4);
        page.WaitForAssertion(() => Assert.Equal(1, counter.Opens));
        OpenTab(page, 0);
        OpenTab(page, 4);

        page.WaitForAssertion(() => Assert.Equal(2, counter.Opens), TimeSpan.FromSeconds(3));
    }

    private sealed class IdleProcess : Whiskers.Services.Terminal.ITerminalProcess
    {
        public StreamReader Output { get; } = new(new MemoryStream());
        public StreamReader Errors { get; } = new(new MemoryStream());
        public bool HasExited => false;
        public Task WriteAsync(string data) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Leaving_the_terminal_tab_while_its_shell_opens_closes_that_shell_and_keeps_only_the_new_one()
    {
        var opens = new List<(string SessionId, TaskCompletionSource<Whiskers.Services.Terminal.TerminalSession> Done)>();
        var closed = new List<string>();
        var container = new ContainerInfo { Id = "abc123", Name = "web", State = "running", ServerId = "local", ServerName = "Local" };
        Services.AddSingleton(Stub<IDockerService>()
            .On(nameof(IDockerService.GetContainerAsync), _ => Task.FromResult<ContainerInfo?>(container)).Object);
        Services.AddSingleton(Null<Whiskers.Services.HealthMonitor.IHealthStore>());
        Services.AddSingleton(Stub<Whiskers.Services.Terminal.ITerminalSessionManager>()
            .On("OpenContainerShellAsync", a =>
            {
                var request = (Whiskers.Services.Terminal.TerminalOpenRequest)a[0]!;
                var done = new TaskCompletionSource<Whiskers.Services.Terminal.TerminalSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (opens) opens.Add((request.SessionId, done));
                return done.Task;
            })
            .On("CloseAsync", a => { lock (closed) closed.Add((string)a[0]!); return Task.CompletedTask; }).Object);
        Services.AddSingleton(Null<Whiskers.Services.Metrics.IMetricsQueryService>());
        Services.AddSingleton(Null<IHostCommandExecutor>());
        Services.AddSingleton(Stub<IServerConfigService>().On("SupportsTerminal", _ => true).Object);
        Services.AddSingleton(Null<Whiskers.Services.Cve.ICveFindingsStore>());
        Services.AddSingleton(Null<IComposePlanner>());
        Services.AddSingleton(Null<IComposeRunner>());
        var page = Render<ContainerDetail>(p => p.Add(x => x.ServerId, "local").Add(x => x.ContainerId, "abc123"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".mud-tab")));
        Whiskers.Services.Terminal.TerminalSession Session(string id) => new(id, new("user:a@x", "a@x"),
            new("local", "abc123", "web"), new IdleProcess(), DateTime.UtcNow);

        OpenTab(page, 4);                                           // the first shell starts opening
        page.WaitForAssertion(() => Assert.Single(opens));
        OpenTab(page, 0);                                           // left before it is open
        OpenTab(page, 4);                                           // back: a second shell starts opening
        page.WaitForAssertion(() => Assert.Equal(2, opens.Count));
        var (first, second) = (opens[0], opens[1]);

        await page.InvokeAsync(() => first.Done.SetResult(Session(first.SessionId)));    // the stale open finishes late
        page.WaitForAssertion(() => Assert.Contains(first.SessionId, closed));
        await page.InvokeAsync(() => second.Done.SetResult(Session(second.SessionId)));

        page.WaitForAssertion(() => Assert.Equal(second.SessionId,
            Get<Whiskers.Services.Terminal.TerminalSession?>(page.Instance, "_termSession")?.SessionId));
        Assert.DoesNotContain(second.SessionId, closed);            // the current shell stays open, the stale one alone closed
        Assert.Equal([first.SessionId], closed);
    }

    [Fact]
    public void The_env_tab_loads_once_while_the_page_renders_during_the_load()
    {
        var counter = new ShellCounter();
        var page = ContainerDetailPage(counter);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".mud-tab")));

        OpenTab(page, 5);
        page.WaitForAssertion(() => Assert.Equal(1, counter.EnvReads));
        page.Render();                 // what the 5 s refresh or any other change does while the env is loading
        page.Render();
        counter.Env.SetResult([new("A", "1")]);

        page.WaitForAssertion(() => Assert.True(Get<bool>(page.Instance, "_envLoaded")));
        Assert.Equal(1, counter.EnvReads);
        var entries = (System.Collections.IList)FieldOf(typeof(ContainerDetail), "_dotEnvEntries").GetValue(page.Instance)!;
        Assert.Equal(2, entries.Count);   // A and B from the .env, each once
    }

    // ------------------------------------------------------------------ app store, compose editor, deploy

    [Fact]
    public void Choosing_a_marketplace_ends_the_search_progress_when_the_search_ends()
    {
        Services.AddSingleton(Null<Whiskers.Services.Templates.ITemplateService>());
        Services.AddSingleton(Stub<Whiskers.Services.ImageSearch.IImageSearchService>()
            .On("GetRegistries", _ => (IReadOnlyList<Whiskers.Services.ImageSearch.ImageRegistryInfo>)[new("hub", "Docker Hub", true)])
            .On("SearchAsync", _ => SlowSearch()).Object);
        Services.AddSingleton(Null<IHostCommandExecutor>());
        Services.AddSingleton(Null<IComposePlanner>());
        Services.AddSingleton(Null<IComposeRunner>());
        Services.AddSingleton(Null<IServerConfigService>());
        Services.AddSingleton(Null<IDockerService>());
        var page = Render<AppStore>();
        var mode = FieldOf(typeof(AppStore), "_mode");
        page.InvokeAsync(() =>
        {
            mode.SetValue(page.Instance, Enum.Parse(mode.FieldType, "Registry"));
            Set(page.Instance, "_search", "nginx");
            Set(page.Instance, "_searchPerformed", true);
        });
        page.Render();

        page.FindAll(".mud-chip")[1].Click();   // "Docker Hub": searches again in that marketplace

        page.WaitForAssertion(() => Assert.False(Get<bool>(page.Instance, "_searching")), TimeSpan.FromSeconds(3));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".mud-progress-linear")), TimeSpan.FromSeconds(3));

        static async Task<IReadOnlyList<Whiskers.Services.ImageSearch.ImageSearchResult>> SlowSearch()
        {
            await Task.Delay(100);
            return [];
        }
    }

    [Fact]
    public async Task A_new_compose_service_never_takes_the_name_of_an_existing_one()
    {
        Services.AddSingleton(Null<IDockerService>());
        Services.AddSingleton(Null<IHostCommandExecutor>());
        Services.AddSingleton(Null<IComposePlanner>());
        Services.AddSingleton(Null<IComposeRunner>());
        Services.AddSingleton(Null<IServerConfigService>());
        var page = Render<ComposeEditor>();

        await Invoke(page, "AddService");
        await Invoke(page, "AddService");
        var services = (System.Collections.IList)FieldOf(typeof(ComposeEditor), "_services").GetValue(page.Instance)!;
        await page.InvokeAsync(() => services.RemoveAt(0));   // service1 goes; service2 stays
        await Invoke(page, "AddService");

        var names = services.Cast<object>().Select(s => (string)s.GetType().GetProperty("Name")!.GetValue(s)!).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Theory]
    [InlineData(AppRole.Operator, false)]
    [InlineData(AppRole.Admin, true)]
    public async Task The_deploy_buttons_are_offered_only_at_the_gateway_level(AppRole role, bool offered)
    {
        Services.AddSingleton(Null<IDockerService>());
        Services.AddSingleton(Null<IHostCommandExecutor>());
        Services.AddSingleton(Null<IComposePlanner>());
        Services.AddSingleton(Null<IComposeRunner>());
        Services.AddSingleton(Null<IServerConfigService>());
        _user.Role = role;

        var deploy = Render<Deploy>();
        var compose = Render<ComposeEditor>();
        await Invoke(compose, "AddService");   // the editor shows its Deploy button under the generated YAML
        compose.Render();

        Assert.Equal(offered, deploy.Markup.Contains("Deploy_Button_Deploy"));
        Assert.Equal(offered, compose.Markup.Contains("ComposeEditor_Button_Deploy"));
    }

    [Fact]
    public void No_deploy_or_query_button_is_gated_by_a_role_of_its_own()
    {
        // The pages ask the capability catalog (INV-13) instead of comparing with a fixed role the gateway does not use.
        foreach (var page in new[] { "Deploy.razor", "ComposeEditor.razor", "AppStore.razor" })
            Assert.DoesNotContain("_role >= AppRole.Operator", Source("Components", "Pages", page));
        var panel = Source("Components", "Pages", "ContainerDatabasePanel.razor").ReplaceLineEndings("\n");
        // Migration (container.exec), SQL file and raw query (db.query) as well: no button states Admin on its own.
        Assert.DoesNotContain("_role < AppRole.", panel);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(panel,
            "Disabled=\"@\\(State\\.DbMigrating \\|\\| _role < CapabilityCatalog\\.RoleOf\\(\"container\\.exec\"\\)\\)\"").Count);
        Assert.Contains("OnClick=\"ExecuteMigrationFile\" Disabled=\"@(State.DbMigrating || _role < CapabilityCatalog.RoleOf(\"db.query\"))\"", panel);
        Assert.Contains("State.DbExecuting || _role < CapabilityCatalog.RoleOf(\"db.query\"))\"", panel);
        Assert.Contains("OnClick=\"ExecuteBuilderQuery\"\n                       Disabled=\"@(_role < CapabilityCatalog.RoleOf(\"db.query\")", panel);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(panel,
            "Disabled=\"@\\(_role < CapabilityCatalog\\.RoleOf\\(\"db\\.query\"\\)\\)\"\n\\s*OnClick=\"@\\(\\(\\) => QuickQuery").Count);
    }

    // ------------------------------------------------------------------ ai operations, triggers

    [Fact]
    public void The_active_guardrail_preset_is_highlighted()
    {
        Services.AddSingleton(Null<Whiskers.Services.Mcp.IMcpPermissionService>());
        Services.AddSingleton(Stub<Whiskers.Services.Guardrails.IGuardrailStore>()
            .On("get_Config", _ => new GuardrailConfig { ActivePreset = AiOperationsPresets.SafeOperations }).Object);

        var page = Render<Whiskers.Components.Pages.AiOperationsView>();

        Assert.Single(page.FindAll(".sw-preset-active"));
        Assert.DoesNotContain("class:sw-active", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_trigger_event_has_its_text_in_both_languages_and_the_english_label_is_english()
    {
        var en = Source("Resources", "SharedResource.resx");
        var de = Source("Resources", "SharedResource.de.resx");
        foreach (var (type, label) in AiTriggerEvents.All)
        {
            Assert.Contains($"<data name=\"AiTriggersView_Event_{type}\"", en);
            Assert.Contains($"<data name=\"AiTriggersView_Event_{type}\"", de);
            Assert.DoesNotMatch("[äöüß]|gestoppt|Neue|nicht|wieder|fehlgeschlagen|Hohe|Zertifikat", label);
        }
        Assert.Contains("EventLabel(e.Type, e.Label)", Source("Components", "Pages", "AiTriggersView.razor"));
    }

    // ------------------------------------------------------------------ markup rules across the pages

    [Fact]
    public void No_link_or_navigation_leaves_the_base_path()
    {
        // Under a PathBase (/whiskers) an absolute "/x" points past the app; "x" resolves against <base href>.
        var offenders = RazorSources()
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Text, "(Href|href)=\"/|NavigateTo\\(\"/"))
            .Select(f => f.Name).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_text_is_coloured_a_fixed_white_that_light_mode_cannot_read()
    {
        var offenders = RazorSources()
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Text, "(?<![-\\w])color:\\s*rgba\\(255,\\s*255,\\s*255"))
            .Select(f => f.Name).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_error_page_does_not_advise_the_development_environment()
    {
        var page = Source("Components", "Pages", "Error.razor");
        Assert.DoesNotContain("DevMode", page);
        Assert.DoesNotContain("Error_DevMode", Source("Resources", "SharedResource.resx"));
    }

    [Fact]
    public void No_page_title_is_german_text()
    {
        foreach (var page in new[] { "LogSearch.razor", "ScheduledTasks.razor" })
            Assert.DoesNotMatch("<PageTitle>(Log-Suche|Geplante Tasks)", Source("Components", "Pages", page));
    }

    [Theory]
    [InlineData(364, 0, 11)]
    [InlineData(365, 1, 0)]
    [InlineData(729, 1, 11)]
    [InlineData(400, 1, 1)]
    public void A_cve_age_never_counts_twelve_months(int days, int years, int months)
    {
        var parts = typeof(Cves).GetMethod("AgeParts", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(parts);   // the reviewed code computed the parts inline: 729 days read "1 yr 12 mo."
        Assert.Equal((years, months), ((int, int))parts.Invoke(null, [days])!);
    }

    // ------------------------------------------------------------------ runner

    private sealed class PassThrough : IUiMutationRunner
    {
        public Task<T> RunAsync<TTarget, T>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            action(TestTargets.Of<TTarget>(capabilityId, arguments), ct);

        public Task RunAsync<TTarget>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            action(TestTargets.Of<TTarget>(capabilityId, arguments), ct);

        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default) => action(ct);

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default) => action(ct);

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
