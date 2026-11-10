using System.Globalization;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Deployment;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// Contract 0013, D2–D4, D15 on the Deploy page's own handlers: the form tab deploys under <c>app.deploy</c> and the
/// compose tab under <c>compose.deploy</c>, both as a planned compose project through the compose runner; a directory
/// another path owns, a project running from another directory and a legacy container with a name the deployment would
/// use are refused before anything is written; "Check plan" lists the project, the containers it touches and every
/// exposure with its class.
/// </summary>
public sealed class DeployPageComposeTests : BunitContext, IAsyncLifetime
{
    private static readonly string NginxId = "sha256:" + new string('1', 64);
    private readonly ProtectedResourceResolutionTests.FakeHost _host = new();
    private readonly FakeDocker _docker = new() { AllowMutations = true };
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;

    public DeployPageComposeTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        _host.Images["nginx"] = (NginxId, "{}");
        _host.Images["nginx:1.27"] = (NginxId, "{}");
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(), approvals: new ApprovalGate(new ApprovalStore(), store));

        Services.AddLogging();
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(new UiCreatedResourceAuditTests.Stub<IKeyInterceptorService>().Object);
        Services.AddSingleton<ICurrentUserService>(new AdminUser());
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), new AdminUser()));
        Services.AddSingleton<IDockerService>(_docker);
        Services.AddSingleton<IHostCommandExecutor>(_host);
        Services.AddSingleton<IComposePlanner>(new ComposePlanner(_docker, _host));
        Services.AddSingleton<IComposeRunner>(new ComposeRunner(_host, _docker));
        Services.AddSingleton<IServerConfigService>(new FakeServerConfig(new ServerConfig { Id = "local", Name = "Local", IsDefault = true }));
        Services.AddSingleton(new UiCreatedResourceAuditTests.Stub<Whiskers.Modules.IModuleRegistry>().On("IsEnabled", _ => true).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        CultureInfo.CurrentUICulture = _culture;
        await DisposeAsync();
    }

    // ---------------------------------------------------------------- harness

    private IRenderedComponent<Deploy> Page() => Render<Deploy>();

    private static void SetField(IRenderedComponent<Deploy> page, string field, object? value) =>
        typeof(Deploy).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page.Instance, value);

    private static T Field<T>(IRenderedComponent<Deploy> page, string field) =>
        (T)typeof(Deploy).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page.Instance)!;

    private static Task Invoke(IRenderedComponent<Deploy> page, string handler)
    {
        var method = typeof(Deploy).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!;
        return page.InvokeAsync(() => (Task)method.Invoke(page.Instance, null)!);
    }

    private List<Snackbar> Snackbars() => Services.GetRequiredService<ISnackbar>().ShownSnackbars.ToList();

    private IReadOnlyList<string> Decided => _audit.Events.Where(e => e.StartsWith("begin:", StringComparison.Ordinal))
        .Select(e => e.Split(':')[1]).ToList();

    private bool Started => _host.Executed.Any(c => c.Contains(" up -d --pull never --no-build", StringComparison.Ordinal));

    private const string ShopYaml = "services:\n  api:\n    image: nginx\n";

    private IRenderedComponent<Deploy> ComposeTab(string yaml = ShopYaml, string project = "shop")
    {
        _host.ComposeConfig = """{"services":{"api":{"image":"nginx"}}}""";
        var page = Page();
        SetField(page, "_composeYaml", yaml);
        SetField(page, "_projectName", project);
        return page;
    }

    private IRenderedComponent<Deploy> FormTab(string name = "web")
    {
        _host.ComposeConfig = "{\"services\":{\"" + name + "\":{\"image\":\"nginx:1.27\",\"container_name\":\"" + name + "\"}}}";
        var page = Page();
        Field<DeploymentRequest>(page, "_formRequest").Image = "nginx:1.27";
        Field<DeploymentRequest>(page, "_formRequest").ContainerName = name;
        return page;
    }

    // ---------------------------------------------------------------- both tabs run through the compose runner

    [Fact]
    public async Task The_form_deploys_its_mapped_file_under_app_deploy_through_the_compose_runner()
    {
        var page = FormTab();

        await Invoke(page, "DeployFromForm");

        Assert.Equal("app.deploy", Decided[0]);
        Assert.True(Started);
        var marker = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(DeploymentCompose.Marker))[..20];
        Assert.Contains(_host.Executed, c => c.Contains(marker, StringComparison.Ordinal));
        Assert.Contains(_host.Executed, c => c.Contains("/opt/deployments/web/", StringComparison.Ordinal));
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Success && s.Message.Contains("compose project web", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_form_refuses_an_entry_the_mapper_refuses_before_anything_is_decided()
    {
        var page = FormTab();
        Field<DeploymentRequest>(page, "_formRequest").RestartPolicy = "sometimes";

        await Invoke(page, "DeployFromForm");

        Assert.Empty(Decided);
        Assert.Empty(_host.Executed);
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Warning && s.Message.Contains("restart policy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_compose_tab_deploys_under_compose_deploy_as_the_compose_editor_does()
    {
        var page = ComposeTab();

        await Invoke(page, "DeployFromCompose");

        Assert.Equal("compose.deploy", Decided[0]);
        Assert.True(Started);
        Assert.Contains(_host.Executed, c => c.Contains("/opt/deployments/shop/docker-compose.yml", StringComparison.Ordinal)
                                             && c.Contains(" up -d", StringComparison.Ordinal));
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Success && s.Message.Contains("shop-api-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_compose_tab_pre_fills_the_project_name_from_name_or_the_first_service()
    {
        var page = ComposeTab("name: blog\nservices:\n  api:\n    image: nginx\n", project: "");
        await page.InvokeAsync(() => typeof(Deploy).GetMethod("SuggestProjectName", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page.Instance, null));
        Assert.Equal("blog", Field<string>(page, "_projectName"));

        var second = ComposeTab(ShopYaml, project: "");
        await second.InvokeAsync(() => typeof(Deploy).GetMethod("SuggestProjectName", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(second.Instance, null));
        Assert.Equal("api", Field<string>(second, "_projectName"));
    }

    // ---------------------------------------------------------------- D3 and D15

    [Fact]
    public async Task The_form_refuses_a_directory_another_path_wrote()
    {
        _host.Files["/opt/deployments/web/docker-compose.yml"] = "services:\n  web:\n    image: nginx\n";
        var page = FormTab();

        await Invoke(page, "DeployFromForm");

        Assert.Empty(Decided);
        Assert.False(Started);
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Warning && s.Message.Contains("another path owns it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_compose_tab_refuses_a_project_that_runs_from_another_directory()
    {
        _docker.Add(new ContainerInfo
        {
            Id = "shop-elsewhere", Name = "shop-api-1", ServerId = "local", State = "running",
            Labels = new() { [ComposeModel.ProjectLabel] = "shop", [ComposeModel.ServiceLabel] = "api",
                ["com.docker.compose.project.working_dir"] = "/srv/shop" },
        });
        var page = ComposeTab();

        await Invoke(page, "DeployFromCompose");

        Assert.Empty(Decided);
        Assert.False(Started);
        Assert.Contains(Snackbars(), s => s.Message.Contains("already runs from another directory", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("api")]          // the old compose tab's name: the service name
    [InlineData("shop-api-1")]   // the name the new deployment gets
    public async Task The_compose_tab_refuses_a_legacy_container_with_a_name_it_would_use(string legacy)
    {
        _docker.Add(new ContainerInfo { Id = "legacy-id", Name = legacy, ServerId = "local", State = "running" });
        var page = ComposeTab();

        await Invoke(page, "DeployFromCompose");

        Assert.Empty(Decided);
        Assert.False(Started);
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Warning
                                          && s.Message.Contains($"Container '{legacy}'", StringComparison.Ordinal)
                                          && s.Message.Contains("container page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_form_refuses_a_legacy_container_with_its_name()
    {
        _docker.Add(new ContainerInfo { Id = "legacy-id", Name = "web", ServerId = "local", State = "running" });
        var page = FormTab();

        await Invoke(page, "DeployFromForm");

        Assert.Empty(Decided);
        Assert.Contains(Snackbars(), s => s.Message.Contains("Container 'web'", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Check plan

    [Fact]
    public async Task Check_plan_lists_the_project_the_touched_containers_and_every_exposure_with_its_class()
    {
        _docker.Add(new ContainerInfo
        {
            Id = "shop-api-old", Name = "shop-api-1", ServerId = "local", State = "running",
            Labels = new() { [ComposeModel.ProjectLabel] = "shop", [ComposeModel.ServiceLabel] = "api",
                ["com.docker.compose.project.working_dir"] = "/opt/deployments/shop" },
        });
        var page = ComposeTab("services:\n  api:\n    image: nginx\n    ports:\n      - \"8080:80\"\n    labels:\n      docktail.funnel.enable: \"true\"\n");

        await Invoke(page, "CheckPlan");

        Assert.Null(Field<string?>(page, "_planError"));
        page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Compose", StringComparison.Ordinal)).Click();
        var markup = page.Markup;
        Assert.Contains("Project shop on /opt/deployments/shop", markup);
        Assert.Contains("shop-api-1 (Recreate)", markup);
        Assert.Contains(ExposureClasses.Published, markup);
        Assert.Contains(ExposureClasses.Public, markup);
        Assert.Contains("docktail.funnel.enable", markup);
        // Read only: nothing was decided or written.
        Assert.Empty(Decided);
        Assert.Empty(_host.Executed);
    }

    [Fact]
    public async Task Check_plan_shows_why_a_plan_is_refused()
    {
        _docker.Add(new ContainerInfo { Id = "legacy-id", Name = "api", ServerId = "local", State = "running" });
        var page = ComposeTab();

        await Invoke(page, "CheckPlan");

        Assert.Contains("Container 'api'", Field<string?>(page, "_planError"));
    }

    private sealed class AdminUser : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("ops@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Admin);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(true);
    }
}
