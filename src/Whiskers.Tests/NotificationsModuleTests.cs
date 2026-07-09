using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Modules.Notifications;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>RoadToSAP Phase 1 — the Notifications module move. Two things are proven: (1) the composite fans
/// an event out over every registered channel + the in-app feed + AI triggers (changeme C9), and (2) the
/// enable/disable gate — with the module on, DI resolves <see cref="CompositeNotificationService"/> plus the
/// 8 channels; with it off, the Core <see cref="NoopNotificationService"/> remains (so every
/// INotificationService consumer still resolves) and no channels are wired. The gate tests mirror the real
/// Program.cs + <see cref="NotificationsModule"/> registrations.</summary>
public class NotificationsModuleTests
{
    // --- Fakes -------------------------------------------------------------------------------------------

    private sealed class FakeChannel : INotificationChannel
    {
        public FakeChannel(string name) => Name = name;
        public string Name { get; }
        public int SendCount;
        public int TestCount;
        public Task SendAsync(NotificationEvent evt) { SendCount++; return Task.CompletedTask; }
        public Task SendTestAsync() { TestCount++; return Task.CompletedTask; }
    }

    private sealed class CountingInApp : IInAppNotificationStore
    {
        public int AddCount;
        public IReadOnlyList<InAppNotification> Recent => Array.Empty<InAppNotification>();
        public int UnreadCount => 0;
        public void Add(NotificationEvent evt) => AddCount++;
        public void MarkAllRead() { }
        public void Clear() { }
        public event Action? Changed { add { } remove { } }
        public event Action<InAppNotification>? Added { add { } remove { } }
        public Task<List<InAppNotification>> QueryAsync(string? severity, string? eventType, int skip, int take, CancellationToken ct = default)
            => Task.FromResult(new List<InAppNotification>());
        public Task<int> CountAsync(string? severity, string? eventType, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class RecordingDispatcher : IAiTriggerDispatcher
    {
        public int Count;
        public Task OnEventAsync(NotificationEvent evt) { Count++; return Task.CompletedTask; }
    }

    private static IServiceProvider SpWith(IAiTriggerDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dispatcher);
        return services.BuildServiceProvider();
    }

    // --- Composite fan-out (changeme C9) -----------------------------------------------------------------

    [Fact]
    public async Task SendAsync_fans_out_to_every_channel_the_in_app_feed_and_ai_triggers()
    {
        var channels = new[] { new FakeChannel("A"), new FakeChannel("B"), new FakeChannel("C") };
        var inApp = new CountingInApp();
        var dispatcher = new RecordingDispatcher();
        var composite = new CompositeNotificationService(
            channels, inApp, SpWith(dispatcher), NullLogger<CompositeNotificationService>.Instance);

        await composite.SendAsync(new NotificationEvent { EventType = "container_down" });

        Assert.All(channels, c => Assert.Equal(1, c.SendCount));
        Assert.Equal(1, inApp.AddCount);
        Assert.Equal(1, dispatcher.Count);
    }

    [Fact]
    public async Task SendTestAsync_tests_every_channel()
    {
        var channels = new[] { new FakeChannel("A"), new FakeChannel("B") };
        var composite = new CompositeNotificationService(
            channels, new CountingInApp(), SpWith(new RecordingDispatcher()), NullLogger<CompositeNotificationService>.Instance);

        await composite.SendTestAsync();

        Assert.All(channels, c => Assert.Equal(1, c.TestCount));
    }

    // --- Enable/disable gate (boot-gate) -----------------------------------------------------------------

    // Mirrors the Core notification registrations from Program.cs: the Noop default plus the two services the
    // module deliberately leaves in Core (in-app feed store + AI-trigger dispatcher), here as light fakes.
    private static ServiceCollection CoreNotificationDeps()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IInAppNotificationStore, CountingInApp>();
        services.AddSingleton<IAiTriggerDispatcher, RecordingDispatcher>();
        // Registered BEFORE the module (like Program.cs registers it before the module loop).
        services.AddSingleton<INotificationService, NoopNotificationService>();
        return services;
    }

    [Fact]
    public void Enabled_module_registers_the_composite_and_all_eight_channels()
    {
        var services = CoreNotificationDeps();
        new NotificationsModule().ConfigureServices(services, new ConfigurationBuilder().Build());

        using var sp = services.BuildServiceProvider(validateScopes: true);

        // The module's composite wins by last-registration; all 8 channels resolve.
        Assert.IsType<CompositeNotificationService>(sp.GetRequiredService<INotificationService>());
        Assert.Equal(8, sp.GetServices<INotificationChannel>().Count());
    }

    [Fact]
    public void Disabled_module_leaves_the_noop_default_and_no_channels()
    {
        // Module NOT configured → mirrors Features:notifications:Enabled=false (its ConfigureServices skipped).
        var services = CoreNotificationDeps();

        using var sp = services.BuildServiceProvider(validateScopes: true);

        Assert.IsType<NoopNotificationService>(sp.GetRequiredService<INotificationService>());
        Assert.Empty(sp.GetServices<INotificationChannel>());
    }
}
