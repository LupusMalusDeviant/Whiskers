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
using Whiskers.Services.Mutations;
using Whiskers.Services.Observability;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Workloads.Kubernetes;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>INV-17 on the Servers page (U-22): pausing and resuming background checks has no page role check of its
/// own, and the buttons are only hidden below Operator. These tests call the page's real handlers as a role below the
/// catalog level of <c>checks.pause</c> / <c>checks.resume</c> (write = Operator) and prove that the real Mutation
/// Gateway refuses at the click, records a Denied event and never reaches <see cref="ILoopSuspensionService"/>;
/// an Operator's click runs inside the gateway after its audit intent.</summary>
public class ServersPauseResumeRoleTests : BunitContext, IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-inv17-srv-" + Guid.NewGuid().ToString("N"));
    private readonly ServerConfigService _servers;
    private readonly RecordingSuspension _suspension = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly MutableUser _user = new(AppRole.Viewer);
    private readonly ServerConfig _edge = new() { Name = "edge", ConnectionType = ConnectionType.TCP, TcpHost = "10.0.0.9" };

    public ServersPauseResumeRoleTests()
    {
        Directory.CreateDirectory(_dir);
        _servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, storePath: Path.Combine(_dir, "servers.json"));
        _servers.InitializeAsync().GetAwaiter().GetResult();
        _servers.AddServerAsync(_edge).GetAwaiter().GetResult();
        _servers.AddServerAsync(new ServerConfig { Name = "core", ConnectionType = ConnectionType.TCP, TcpHost = "10.0.0.10" })
            .GetAwaiter().GetResult();

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IServerConfigService>(_servers);
        Services.AddSingleton<ICurrentUserService>(_user);
        var store = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(), approvals: new ApprovalGate(new ApprovalStore(), store));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        Services.AddSingleton<ILoopSuspensionService>(_suspension);
        Services.AddSingleton(NullProxy<Whiskers.Services.Vault.IVaultService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Docker.IDockerConnectionManager>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Onboarding.IOnboardingService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Workloads.IWorkloadProviderFactory>.Create());
        Services.AddSingleton(NullProxy<IKubernetesClientCache>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Docker.Budget.IServerCircuitBreaker>.Create());
        Services.AddSingleton(EmptyClientCredentialStore());
        Services.AddSingleton(NullProxy<Whiskers.Services.Certificates.IMtlsCertificateService>.Create());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_viewer_cannot_pause_a_server_and_the_refusal_is_recorded()
    {
        var page = Render<Servers>();   // as a Viewer: no pause button is rendered, the handler is called anyway

        await Invoke(page, "PauseServer", _edge, ServerMonitoring.PauseOptions[0]);

        Assert.Empty(_suspension.Calls);
        Assert.False(_suspension.IsSuspended(_edge.Id));
        Assert.Equal(["denied:Denied"], Events());
    }

    [Fact]
    public async Task A_viewer_cannot_resume_a_paused_server_and_the_refusal_is_recorded()
    {
        _suspension.Paused.Add(_edge.Id);
        var page = Render<Servers>();

        await Invoke(page, "ResumeServer", _edge);

        Assert.Empty(_suspension.Calls);
        Assert.True(_suspension.IsSuspended(_edge.Id));
        Assert.Equal(["denied:Denied"], Events());
    }

    [Fact]
    public async Task A_viewer_cannot_pause_the_fleet_and_every_refusal_is_recorded()
    {
        var page = Render<Servers>();
        var enabled = _servers.GetEnabledServers().Count;
        Assert.True(enabled >= 2);

        await Invoke(page, "PauseAll", ServerMonitoring.PauseOptions[0]);

        Assert.Empty(_suspension.Calls);
        Assert.Equal(Enumerable.Repeat("denied:Denied", enabled), Events());
    }

    [Fact]
    public async Task An_operator_demoted_while_the_page_is_open_cannot_pause_or_resume()
    {
        _user.Role = AppRole.Operator;
        _suspension.Paused.Add(_edge.Id);
        var page = Render<Servers>();   // rendered for an Operator: the buttons are on screen
        _user.Role = AppRole.Viewer;

        await Invoke(page, "ResumeServer", _edge);
        await Invoke(page, "PauseServer", _edge, ServerMonitoring.PauseOptions[0]);

        Assert.Empty(_suspension.Calls);
        Assert.Equal(["denied:Denied", "denied:Denied"], Events());
    }

    [Fact]
    public async Task An_operator_pauses_and_resumes_a_server_inside_the_gateway()
    {
        _user.Role = AppRole.Operator;
        var page = Render<Servers>();

        await Invoke(page, "PauseServer", _edge, ServerMonitoring.PauseOptions[0]);
        await Invoke(page, "ResumeServer", _edge);

        Assert.Equal([$"suspend:{_edge.Id}", $"resume:{_edge.Id}"], _suspension.Calls);
        Assert.Equal(["begin:checks.pause:", "complete:Executed", "begin:checks.resume:", "complete:Executed"], Events());
    }

    [Fact]
    public async Task An_operator_may_pause_the_fleet_at_the_contracted_level()
    {
        // PRODUCT_CONTRACT U-22: the fleet-wide pause is checks.pause per server, catalog level write (Operator).
        _user.Role = AppRole.Operator;
        var page = Render<Servers>();
        var enabled = _servers.GetEnabledServers().Select(s => $"suspend:{s.Id}").ToList();

        await Invoke(page, "PauseAll", ServerMonitoring.PauseOptions[0]);

        Assert.Equal(enabled, _suspension.Calls);
        Assert.Equal(enabled.Count, Events().Count(e => e == "complete:Executed"));
        Assert.DoesNotContain(Events(), e => e.StartsWith("denied:", StringComparison.Ordinal));
    }

    private List<string> Events() { lock (_audit.Events) return _audit.Events.ToList(); }

    private static Task Invoke(IRenderedComponent<Servers> page, string handler, params object[] args)
    {
        var method = typeof(Servers).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return page.InvokeAsync(() => (Task)method.Invoke(page.Instance, args)!);
    }

    internal sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("someone@x");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }

    /// <summary>Remembers every pause and resume that reached the service.</summary>
    private sealed class RecordingSuspension : ILoopSuspensionService
    {
        public HashSet<string> Paused { get; } = [];
        public List<string> Calls { get; } = [];
        public bool IsSuspended(string serverId) => Paused.Contains(serverId);
        public void Suspend(string serverId, DateTime? until, string reason, bool automatic = false)
        {
            Calls.Add($"suspend:{serverId}");
            Paused.Add(serverId);
        }
        public void Resume(string serverId)
        {
            Calls.Add($"resume:{serverId}");
            Paused.Remove(serverId);
        }
        public IReadOnlyList<LoopSuspension> Current() => [];
    }
}
