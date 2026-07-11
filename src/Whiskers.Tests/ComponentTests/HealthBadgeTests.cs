using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Shared;

namespace Whiskers.Tests.ComponentTests;

/// <summary>bUnit pilot (changeme C17.4): first component-level tests in the repo. HealthBadge is
/// the smallest shared widget — the pilot proves the bUnit + MudBlazor setup so page-tab components
/// (C4 follow-up) can adopt the pattern.</summary>
public class HealthBadgeTests : BunitContext
{
    public HealthBadgeTests()
    {
        Services.AddMudServices();
        // MudBlazor's real KeyInterceptorService is IAsyncDisposable-ONLY, which crashes
        // BunitContext's synchronous teardown — replace it with a sync-disposable no-op.
        // (Same workaround any future component test class should copy.)
        Services.AddSingleton<IKeyInterceptorService>(new NoopKeyInterceptor());
        JSInterop.Mode = JSRuntimeMode.Loose;
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

    [Theory]
    [InlineData("healthy", "mud-chip-color-success")]
    [InlineData("unhealthy", "mud-chip-color-error")]
    [InlineData("starting", "mud-chip-color-warning")]
    [InlineData("none", "mud-chip-color-default")]
    [InlineData("HEALTHY", "mud-chip-color-success")] // case-insensitive mapping
    public void Status_maps_to_the_right_chip_color(string status, string expectedClass)
    {
        var cut = Render<HealthBadge>(p => p.Add(x => x.Status, status));
        var chip = cut.Find(".mud-chip");
        Assert.Contains(expectedClass, chip.ClassList);
        Assert.Contains(status, chip.TextContent);
    }

    [Fact]
    public void Default_status_renders_without_parameters()
    {
        var cut = Render<HealthBadge>();
        Assert.Contains("none", cut.Find(".mud-chip").TextContent);
    }
}
