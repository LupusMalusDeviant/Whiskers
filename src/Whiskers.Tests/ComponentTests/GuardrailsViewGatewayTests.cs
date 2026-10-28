using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-11, on the real page handlers: the Guardrails page saves and switches the kill switch off through the
/// real Mutation Gateway against a real <see cref="GuardrailStore"/>. The test host has no resource assembly, so
/// texts render as their keys.</summary>
public class GuardrailsViewGatewayTests : BunitContext, IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-r111-ui-" + Guid.NewGuid().ToString("N"));
    private readonly GuardrailStore _store;
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly MutableUser _user = new(AppRole.Admin);

    public GuardrailsViewGatewayTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new GuardrailStore(filePath: Path.Combine(_dir, "guardrails.json"));
        _store.InitializeAsync().GetAwaiter().GetResult();

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(new NoopKeyInterceptor());
        Services.AddSingleton<IGuardrailStore>(_store);
        Services.AddSingleton<IAgentToolRegistry>(new NoTools());
        Services.AddSingleton<ICurrentUserService>(_user);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store), _audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), _store));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // MudSwitch pulls in a service that only disposes asynchronously; dispose the container that way first.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Save_goes_through_the_gateway_and_is_audited_before_the_store_changes()
    {
        var cut = Render<GuardrailsView>();
        var before = _store.Current.MaxActionsPerSession;

        Save(cut);

        cut.WaitForAssertion(() => Assert.Contains("complete:Executed", Events()));
        Assert.Contains("begin:guardrails.save:", Events());
        Assert.Equal(before, _store.Current.MaxActionsPerSession);
    }

    [Fact]
    public void A_user_demoted_while_the_page_is_open_cannot_save()
    {
        var cut = Render<GuardrailsView>();
        _user.Role = AppRole.Operator;   // the Save button is still rendered
        cut.Find("input[type=checkbox]").Change(true);   // kill switch on in the edit buffer

        Save(cut);

        cut.WaitForAssertion(() => Assert.Contains("denied:Denied", Events()));
        Assert.False(_store.Current.ReadOnlyMode);
    }

    [Fact]
    public void When_the_audit_cannot_be_written_nothing_is_saved()
    {
        var cut = Render<GuardrailsView>();
        cut.Find("input[type=checkbox]").Change(true);
        _audit.FailNextBegin = true;

        Save(cut);

        cut.WaitForAssertion(() => Assert.False(_audit.FailNextBegin));   // the intent write was attempted and failed
        Assert.False(_store.Current.ReadOnlyMode);
        Assert.DoesNotContain(Events(), e => e.StartsWith("begin:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_the_kill_switch_on_saving_is_refused_and_only_switching_it_off_works()
    {
        var config = _store.Config;
        var active = config.Presets.First(p => p.Name == config.ActivePreset);
        active.Policy.ReadOnlyMode = true;
        active.Policy.MaxActionsPerSession = 3;
        await _store.SaveConfigAsync(config, Admin);
        var cut = Render<GuardrailsView>();

        Save(cut);
        cut.WaitForAssertion(() => Assert.Contains("denied:Denied", Events()));
        Assert.True(_store.Current.ReadOnlyMode);

        cut.FindAll("button").First(b => b.TextContent.Contains("GuardrailsView_KillSwitch_Disable_Button")).Click();

        cut.WaitForAssertion(() => Assert.False(_store.Current.ReadOnlyMode));
        Assert.Equal(3, _store.Current.MaxActionsPerSession);
        Assert.Contains("begin:guardrails.kill_switch_off:", Events());
    }

    [Fact]
    public async Task An_operator_cannot_switch_the_kill_switch_off()
    {
        var config = _store.Config;
        config.Presets.First(p => p.Name == config.ActivePreset).Policy.ReadOnlyMode = true;
        await _store.SaveConfigAsync(config, Admin);
        var cut = Render<GuardrailsView>();
        _user.Role = AppRole.Operator;

        cut.FindAll("button").First(b => b.TextContent.Contains("GuardrailsView_KillSwitch_Disable_Button")).Click();

        cut.WaitForAssertion(() => Assert.Contains("denied:Denied", Events()));
        Assert.True(_store.Current.ReadOnlyMode);
    }

    private List<string> Events() { lock (_audit.Events) return _audit.Events.ToList(); }

    private static void Save(IRenderedComponent<GuardrailsView> cut) =>
        cut.FindAll("button").Last(b => b.TextContent.Contains("GuardrailsView_SaveAll_Button")).Click();

    private static readonly AgentPrincipal Admin =
        new(AgentPrincipalKind.WebUser, "admin@x", McpPermissionLevels.Admin, null, UserEmail: "admin@x");

    private sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@x");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }

    private sealed class NoopKeyInterceptor : IKeyInterceptorService, IDisposable
    {
        public Task SubscribeAsync(IKeyInterceptorObserver observer, MudBlazor.Services.KeyInterceptorOptions options) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, IKeyDownObserver? keyDown = null, IKeyUpObserver? keyUp = null) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, Action<KeyboardEventArgs>? keyDown = null, Action<KeyboardEventArgs>? keyUp = null) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, Func<KeyboardEventArgs, Task>? keyDown = null, Func<KeyboardEventArgs, Task>? keyUp = null) => Task.CompletedTask;
        public Task UnsubscribeAsync(IKeyInterceptorObserver observer) => Task.CompletedTask;
        public Task UnsubscribeAsync(string elementId) => Task.CompletedTask;
        public Task UpdateKeyAsync(IKeyInterceptorObserver observer, MudBlazor.Services.KeyOptions option) => Task.CompletedTask;
        public Task UpdateKeyAsync(string elementId, MudBlazor.Services.KeyOptions option) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, Action<MudBlazor.Services.KeyMapBuilder> keyMap) => Task.CompletedTask;
        public Task DispatchAsync(string elementId, MudBlazor.Services.KeyEventKind kind, KeyboardEventArgs args) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoTools : IAgentToolRegistry
    {
        public IReadOnlyDictionary<string, AgentToolEntry> Tools { get; } = new Dictionary<string, AgentToolEntry>();
    }
}
