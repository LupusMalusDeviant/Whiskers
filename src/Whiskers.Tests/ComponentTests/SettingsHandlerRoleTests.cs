using System.Reflection;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Cve;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>INV-17/INV-18 on the Settings page's own handlers. The page renders its content for Admins only, but its
/// handlers decide with the role the user holds when they run: the manual CVE scan is the gateway's <c>cve.scan</c>
/// (Operator; a refusal is one Denied event and the monitor never runs), and the test-notification buttons check for
/// Admin inside the handler before anything is sent. Rendered as a Viewer, the page shows its no-access panel and the
/// handlers are called directly.</summary>
public class SettingsHandlerRoleTests : BunitContext, IAsyncLifetime
{
    private readonly CountingMonitor _monitor = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly ServersPauseResumeRoleTests.MutableUser _user = new(AppRole.Viewer);
    private readonly List<string> _sent = [];

    public SettingsHandlerRoleTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddOptions();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<ICveMonitorService>(_monitor);
        Services.AddSingleton<ICveFindingsStore>(new NoopCveFindingsStore());
        Services.AddSingleton<INotificationService>(Recording<INotificationService>.Create(_sent, "channels"));
        Services.AddSingleton<IMatrixNotificationService>(Recording<IMatrixNotificationService>.Create(_sent, "matrix"));
        Services.AddSingleton(NullProxy<Whiskers.Modules.IModuleRegistry>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Auth.IWhitelistService>.Create());
        Services.AddSingleton(NullProxy<IRoleService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Vault.IVaultService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Mcp.IMcpPermissionService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Persistence.IAppSettingsStore>.Create());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var policy = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(policy), _audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), policy));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // MudBlazor services dispose only asynchronously; dispose the container that way.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Fact]
    public async Task A_viewer_cannot_start_a_scan_from_settings_and_the_refusal_is_recorded()
    {
        var page = Render<Settings>();
        Assert.Contains("Settings_Alert_NoAccess", page.Markup);

        await Invoke(page, "TriggerCveScan");

        page.WaitForAssertion(() => Assert.Equal(["denied:Denied"], Events()), TimeSpan.FromSeconds(10));
        page.WaitForAssertion(() => Assert.Contains(Snackbars(), s => s.Severity == Severity.Warning), TimeSpan.FromSeconds(10));
        Assert.Equal(0, _monitor.Runs);
    }

    [Fact]
    public async Task The_role_held_at_the_click_decides_the_scan_not_the_rendered_page()
    {
        var page = Render<Settings>();   // rendered for a Viewer
        _user.Role = AppRole.Operator;

        await Invoke(page, "TriggerCveScan");

        page.WaitForAssertion(() => Assert.Equal(["begin:cve.scan:", "complete:Executed"], Events()), TimeSpan.FromSeconds(10));
        Assert.Equal(1, _monitor.Runs);
    }

    [Fact]
    public async Task A_scan_that_found_another_one_running_is_a_reported_failure_not_a_finished_scan()
    {
        _user.Role = AppRole.Operator;
        _monitor.AlreadyRunning = true;
        var page = Render<Settings>();

        await Invoke(page, "TriggerCveScan");

        page.WaitForAssertion(() => Assert.Equal(["begin:cve.scan:", "complete:Failed"], Events()), TimeSpan.FromSeconds(10));
        page.WaitForAssertion(() => Assert.Contains(Snackbars(), s => s.Message == "Cves_Snackbar_ScanAlreadyRunning"),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(Snackbars(), s => s.Message == "Cves_Snackbar_ScanFinished");
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task Below_admin_no_test_notification_is_sent(AppRole role)
    {
        var page = Render<Settings>();
        _user.Role = role;

        await Invoke(page, "TestChannels");
        await Invoke(page, "TestMattermost");
        await Invoke(page, "TestMatrix");

        lock (_sent) Assert.Empty(_sent);
        Assert.Contains(Snackbars(), s => s.Message == "Settings_Snackbar_NoPermission");   // identical toasts are shown once
    }

    [Fact]
    public async Task An_admin_sends_the_test_notifications()
    {
        var page = Render<Settings>();
        _user.Role = AppRole.Admin;   // promoted since the page rendered: the handler reads the role now

        await Invoke(page, "TestChannels");
        await Invoke(page, "TestMatrix");

        lock (_sent) Assert.Equal(["channels.SendTestAsync", "matrix.SendTestAsync"], _sent);
    }

    private List<string> Events() { lock (_audit.Events) return _audit.Events.ToList(); }

    private List<Snackbar> Snackbars() => Services.GetRequiredService<ISnackbar>().ShownSnackbars.ToList();

    private static Task Invoke(IRenderedComponent<Settings> page, string handler) => page.InvokeAsync(() =>
        (Task)typeof(Settings).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, null)!);

    private sealed class CountingMonitor : ICveMonitorService
    {
        private int _runs;
        public int Runs => Volatile.Read(ref _runs);
        public bool AlreadyRunning { get; set; }

        public Task<bool> RunOneCycleAsync(CancellationToken ct = default)
        {
            if (AlreadyRunning) return Task.FromResult(false);
            Interlocked.Increment(ref _runs);
            return Task.FromResult(true);
        }
    }

    /// <summary>Records every call as "prefix.Method"; completes every task.</summary>
    public class Recording<T> : DispatchProxy where T : class
    {
        private List<string> _calls = null!;
        private string _prefix = "";

        public static T Create(List<string> calls, string prefix)
        {
            var proxy = DispatchProxy.Create<T, Recording<T>>();
            var self = (Recording<T>)(object)proxy;
            self._calls = calls;
            self._prefix = prefix;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            lock (_calls) _calls.Add($"{_prefix}.{method!.Name}");
            var type = method.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type == typeof(void)) return null;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
