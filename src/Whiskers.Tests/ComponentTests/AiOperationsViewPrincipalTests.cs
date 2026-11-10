using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests.ComponentTests;

/// <summary>INV-19 review (m6): the AI Operations page hands the guardrail store the signed-in user's own level, never a
/// synthetic Admin, so the store's own Admin check stays a real second layer. The runner here only runs the action
/// (the gateway's Admin check is pinned elsewhere); the user is demoted to Operator after the page rendered, as a role
/// change while the page is open would do.</summary>
public class AiOperationsViewPrincipalTests : BunitContext, IAsyncLifetime
{
    private readonly RecordingStore _store = new();
    private readonly User _user = new() { Role = AppRole.Admin };

    public AiOperationsViewPrincipalTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(new NoopKeyInterceptor());
        Services.AddSingleton<IGuardrailStore>(_store);
        Services.AddSingleton<IMcpPermissionService>(new AutomationMutationRunnerTests.Keys());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<IUiMutationRunner>(new PassThroughRunner());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Theory]
    [InlineData(AppRole.Admin, McpPermissionLevels.Admin)]
    [InlineData(AppRole.Operator, McpPermissionLevels.Write)]
    public void Activating_a_preset_hands_the_store_the_users_own_level(AppRole roleAtClick, string expected)
    {
        var cut = Render<AiOperationsView>();
        _user.Role = roleAtClick;

        cut.FindAll("button").First(b => b.TextContent.Contains("AiOps_Activate_Button")).Click();

        cut.WaitForAssertion(() => Assert.NotNull(_store.Editor));
        Assert.Equal(expected, _store.Editor!.PermissionLevel);
        Assert.Equal(AgentPrincipalKind.WebUser, _store.Editor.Kind);
        Assert.Equal("admin@x", _store.Editor.UserEmail);
    }

    private sealed class User : ICurrentUserService
    {
        public AppRole Role { get; set; }
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@x");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }

    private sealed class RecordingStore : IGuardrailStore
    {
        public AgentPrincipal? Editor { get; private set; }
        public GuardrailPolicy Current { get; } = GuardrailPolicy.SafeDefault();
        public GuardrailConfig Config => new();
        public event Action? Changed { add { } remove { } }

        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default)
        {
            Editor = editor;
            return Task.CompletedTask;
        }

        public Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class PassThroughRunner : IUiMutationRunner
    {
        public Task<T> RunAsync<TTarget, T>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            RunAsync(capabilityId, arguments, token => action(TestTargets.Of<TTarget>(capabilityId, arguments), token), ct);

        public Task RunAsync<TTarget>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            RunAsync(capabilityId, arguments, token => action(TestTargets.Of<TTarget>(capabilityId, arguments), token), ct);

        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default) => action(ct);

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default) => action(ct);

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
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
}
