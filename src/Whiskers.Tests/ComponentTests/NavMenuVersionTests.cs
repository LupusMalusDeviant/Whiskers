using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Layout;
using Whiskers.Modules;
using Whiskers.Utils;

namespace Whiskers.Tests.ComponentTests;

/// <summary>Guards the sidebar version tag: it must render the resolved x.x.x app version, NOT the raw Razor
/// expression. A markup expression written as <c>v@Foo.Bar.Baz</c> was swallowed by Razor's email-address
/// literal heuristic (the <c>@</c> directly after a letter) and rendered verbatim as text.</summary>
public class NavMenuVersionTests : BunitContext
{
    public NavMenuVersionTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        // MudBlazor's real KeyInterceptorService is IAsyncDisposable-ONLY, which crashes BunitContext's
        // synchronous teardown — replace it with a sync-disposable no-op (copied from the bUnit pilot).
        Services.AddSingleton<IKeyInterceptorService>(new NoopKeyInterceptor());
        Services.AddSingleton<IModuleRegistry>(
            new ModuleRegistry(Array.Empty<NavItem>(), Array.Empty<Type>(), Array.Empty<string>()));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Sidebar_renders_the_resolved_version_not_the_razor_expression()
    {
        var cut = Render<NavMenu>();

        var text = cut.Find(".sw-sidebar-version").TextContent.Trim();
        Assert.Equal(AppVersion.Display, text);        // the expression evaluated (would be "@_version"/"v@…" if not)
        Assert.Matches(@"^\d+\.\d+\.\d+", text);        // plain x.x.x, no "v" prefix, no leaked identifier
        Assert.DoesNotContain("@", text);
        Assert.DoesNotContain("AppVersion", text);
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
