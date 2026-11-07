using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Cve;
using Whiskers.Services.Mutations;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>INV-17/INV-18 on the manual CVE scan (U-40, user decision 2026-11-07: Operator). The CVEs page starts a
/// cycle only through the real Mutation Gateway (<c>cve.scan</c>): a Viewer — also an Operator demoted while the page
/// is open — is refused at the click, the refusal is one Denied audit event and <see cref="ICveMonitorService"/> never
/// runs; an Operator's click runs the cycle inside the decision. Hiding the button is presentation only.</summary>
public class CveScanRoleTests : BunitContext, IAsyncLifetime
{
    private readonly CountingMonitor _monitor = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly ServersPauseResumeRoleTests.MutableUser _user = new(AppRole.Viewer);

    public CveScanRoleTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<ICveMonitorService>(_monitor);
        Services.AddSingleton<ICveFindingsStore>(new NoopCveFindingsStore());
        Services.AddSingleton<ICveAgeStore>(new NoopCveAgeStore());
        Services.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig());
        Services.AddSingleton<IOptionsMonitor<CveMonitorSettings>>(new FixedOptions(new CveMonitorSettings { Enabled = true }));
        Services.AddSingleton<Whiskers.Modules.IModuleRegistry>(new CveEnabled());
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
    public async Task A_viewer_cannot_start_a_scan_and_the_refusal_is_recorded()
    {
        var page = Render<Cves>();   // as a Viewer: no scan button is rendered, the handler runs anyway
        Assert.DoesNotContain("Cves_Button_ScanNow", page.Markup);

        await page.InvokeAsync(() => (Task)typeof(Cves)
            .GetMethod("TriggerScan", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, null)!);

        page.WaitForAssertion(() => Assert.Equal(["denied:Denied"], Events()), TimeSpan.FromSeconds(10));
        Assert.Equal(0, _monitor.Runs);
    }

    [Fact]
    public void An_operator_demoted_while_the_page_is_open_cannot_start_a_scan()
    {
        _user.Role = AppRole.Operator;
        var page = Render<Cves>();
        _user.Role = AppRole.Viewer;   // the button is still on screen

        ScanButton(page).Click();

        page.WaitForAssertion(() => Assert.Equal(["denied:Denied"], Events()), TimeSpan.FromSeconds(10));
        Assert.Equal(0, _monitor.Runs);
    }

    [Fact]
    public void An_operator_starts_a_scan_that_runs_inside_the_gateway()
    {
        _user.Role = AppRole.Operator;
        var page = Render<Cves>();

        ScanButton(page).Click();

        page.WaitForAssertion(() => Assert.Equal(["begin:cve.scan:", "complete:Executed"], Events()), TimeSpan.FromSeconds(10));
        Assert.Equal(1, _monitor.Runs);
        Assert.True(_monitor.RanInsideTheDecision);
    }

    [Fact]
    public void A_scan_that_found_another_one_running_is_a_reported_failure_and_says_so()
    {
        _user.Role = AppRole.Operator;
        _monitor.AlreadyRunning = true;
        var page = Render<Cves>();

        ScanButton(page).Click();

        page.WaitForAssertion(() => Assert.Equal(["begin:cve.scan:", "complete:Failed"], Events()), TimeSpan.FromSeconds(10));
        page.WaitForAssertion(() => Assert.Contains(Snackbars(), s => s.Message == "Cves_Snackbar_ScanAlreadyRunning"),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(Snackbars(), s => s.Message == "Cves_Snackbar_ScanFinished");
    }

    private List<Snackbar> Snackbars() => Services.GetRequiredService<ISnackbar>().ShownSnackbars.ToList();

    private List<string> Events() { lock (_audit.Events) return _audit.Events.ToList(); }

    private static AngleSharp.Dom.IElement ScanButton(IRenderedComponent<Cves> page) =>
        page.FindAll("button").First(b => b.TextContent.Contains("Cves_Button_ScanNow"));

    /// <summary>Counts the cycles that reached the monitor and whether the audit intent was written before.</summary>
    private sealed class CountingMonitor : ICveMonitorService
    {
        private int _runs;
        public int Runs => Volatile.Read(ref _runs);
        public bool RanInsideTheDecision { get; private set; }

        public bool AlreadyRunning { get; set; }

        public Task<bool> RunOneCycleAsync(CancellationToken ct = default)
        {
            if (AlreadyRunning) return Task.FromResult(false);
            Interlocked.Increment(ref _runs);
            RanInsideTheDecision = MutationScope.Current is { } scope && scope.CapabilityId == "cve.scan";
            return Task.FromResult(true);
        }
    }

    private sealed class FixedOptions(CveMonitorSettings value) : IOptionsMonitor<CveMonitorSettings>
    {
        public CveMonitorSettings CurrentValue => value;
        public CveMonitorSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<CveMonitorSettings, string?> listener) => null;
    }

    private sealed class CveEnabled : Whiskers.Modules.IModuleRegistry
    {
        public IReadOnlyList<Whiskers.Modules.NavItem> NavItems => [];
        public IReadOnlyList<Type> McpToolTypes => [];
        public bool IsEnabled(string moduleId) => moduleId == "cve";
    }
}
