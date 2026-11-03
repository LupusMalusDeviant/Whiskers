using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Hubs;
using Whiskers.Models;
using Whiskers.Services.HealthMonitor;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>
/// R1-41, review round 2: a background cycle is owned to its end. The host's stop reaches every dependency the cycle
/// waits on — the fleet listing, the Docker and registry reads, the notification delivery and its retries — and the cycle
/// awaits their real end instead of abandoning them with <c>WaitAsync</c>. A dependency that answers only after the stop
/// changes nothing: no metric, no notification, no stored state.
///
/// <para>Every gate here deliberately ignores the stop (the worst case): it proves the token reached the dependency, then
/// that the cycle is still waiting for it, then releases it and checks that nothing happened. No sleeps.</para>
/// </summary>
public sealed class LoopShutdownTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-r141-stop-{Guid.NewGuid():N}");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private static readonly Whiskers.Models.ServerConfig Badwolf = new()
    {
        Id = "badwolf", Name = "Badwolf", IsDefault = true, Enabled = true, ConnectionType = ConnectionType.Local,
    };

    private static ContainerInfo Web() =>
        new() { Id = "c1", Name = "web", Image = "nginx:1", ServerId = "badwolf", ServerName = "Badwolf", State = "running" };

    // ---- image updates --------------------------------------------------------------------------------------------

    public enum ImageRead { DockerDigest, Registry }

    [Theory]
    [InlineData(ImageRead.DockerDigest)]
    [InlineData(ImageRead.Registry)]
    public async Task A_stop_during_an_image_read_is_awaited_and_leaves_no_effect(ImageRead where)
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Gated(CancellationToken ct)
        {
            ct.Register(() => sawStop.TrySetResult());
            reading.TrySetResult();
            return gate.Task;
        }

        var docker = new FakeDocker(Web())
        {
            DigestWithToken = (_, ct) => where == ImageRead.DockerDigest ? Gated(ct) : Task.FromResult<string?>("sha256:local"),
        };
        var registry = new TokenRegistry((_, ct) => where == ImageRead.Registry ? Gated(ct) : Task.FromResult<string?>("sha256:remote"));
        var notifications = new FakeNotifications();
        var (checker, store, hub) = ImageChecker(docker, registry, notifications);

        using var stopping = new CancellationTokenSource();
        var cycle = checker.CheckAllImagesAsync(stopping.Token);
        await reading.Task.WaitAsync(Wait);

        stopping.Cancel();                                 // the host stops
        await sawStop.Task.WaitAsync(Wait);                // the stop reached the read itself
        Assert.False(cycle.IsCompleted);                   // and the cycle waits for the read's real end

        gate.SetResult("sha256:other");                    // the read answers after the stop
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle.WaitAsync(Wait));   // ends with the stop

        Assert.Null(store.Get("c1", "badwolf"));
        Assert.Null(store.LastCheckAt);
        Assert.False(store.IsChecking);
        Assert.Empty(notifications.Events);
        Assert.Empty(hub);
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_stop_during_the_update_notification_is_awaited_and_nothing_follows_it()
    {
        var docker = new FakeDocker(Web(), new ContainerInfo
        {
            Id = "c2", Name = "api", Image = "redis:7", ServerId = "badwolf", ServerName = "Badwolf", State = "running",
        })
        {
            DigestWithToken = (_, _) => Task.FromResult<string?>("sha256:local"),
        };
        var notifications = new FakeNotifications { Gate = new() };
        var (checker, store, hub) = ImageChecker(docker, new TokenRegistry((_, _) => Task.FromResult<string?>("sha256:remote")),
            notifications);

        using var stopping = new CancellationTokenSource();
        var cycle = checker.CheckAllImagesAsync(stopping.Token);
        await notifications.Delivering.Task.WaitAsync(Wait);

        stopping.Cancel();
        await notifications.SawStop.Task.WaitAsync(Wait);  // the stop reached the delivery (and so its retries)
        Assert.False(cycle.IsCompleted);                   // the cycle owns the delivery

        notifications.Gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle.WaitAsync(Wait));

        Assert.Empty(notifications.Events);                // neither the interrupted one nor the second update's
        Assert.Empty(hub);                                 // no broadcast
        Assert.False(store.IsChecking);
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Any()); // not saved
    }

    [Fact]
    public async Task Without_a_stop_the_image_cycle_has_the_effects_the_stop_tests_rule_out()
    {
        // The control: the same doubles produce the state, the notifications, the broadcast and the file — so their
        // absence above is the stop's doing, not a double that never does anything.
        var docker = new FakeDocker(Web()) { DigestWithToken = (_, _) => Task.FromResult<string?>("sha256:local") };
        var notifications = new FakeNotifications();
        var (checker, store, hub) = ImageChecker(docker, new TokenRegistry((_, _) => Task.FromResult<string?>("sha256:remote")),
            notifications);

        await checker.CheckAllImagesAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.True(store.Get("c1", "badwolf")?.UpdateAvailable);
        Assert.NotNull(store.LastCheckAt);
        Assert.Single(notifications.Events);
        Assert.Contains("ImageUpdatesChanged", hub);
        Assert.True(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Any());
    }

    private (ImageUpdateChecker Checker, ImageUpdateStore Store, List<string> Hub) ImageChecker(
        FakeDocker docker, IRegistryClient registry, FakeNotifications notifications)
    {
        var hub = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<Whiskers.Services.Docker.IDockerService>(docker);
        services.AddSingleton<INotificationService>(notifications);
        services.AddSingleton(RecordingHub.Create(hub));
        services.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(Badwolf));
        var sp = services.BuildServiceProvider();
        var store = new ImageUpdateStore(dataPaths: new DataPathOptions(_dir));
        var checker = new ImageUpdateChecker(sp, store, registry,
            Options.Create(new ImageUpdateSettings { NotifyOnUpdate = true }),
            NullLogger<ImageUpdateChecker>.Instance, new Whiskers.Services.Observability.SelfMetrics.SelfMetrics());
        return (checker, store, hub);
    }

    private sealed class TokenRegistry(Func<string, CancellationToken, Task<string?>> read) : IRegistryClient
    {
        public Task<string?> GetRemoteDigestAsync(string imageRef) => read(imageRef, CancellationToken.None);
        public Task<string?> GetRemoteDigestAsync(string imageRef, CancellationToken ct) => read(imageRef, ct);
    }

    // ---- container health -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_health_listing_released_after_the_stop_has_no_effect()
    {
        var docker = new FakeDocker(Web()) { DetailedListingGate = new(), AllowMutations = true };
        var (monitor, health, notifications, metrics, hub) = HealthMonitor(docker);

        using var stopping = new CancellationTokenSource();
        var run = RunAsync(monitor, stopping.Token);
        await docker.DetailedListingStarted.Task.WaitAsync(Wait);

        stopping.Cancel();
        await docker.DetailedListingSawStop.Task.WaitAsync(Wait);   // the stop reached the listing
        Assert.False(run.IsCompleted);                              // the loop still owns its cycle

        docker.DetailedListingGate!.SetResult();                    // the listing answers after the stop
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait));   // the host's stop ends the loop

        Assert.Empty(metrics.Loops());                              // no cycle recorded
        Assert.Empty(notifications.Events);
        Assert.Empty(hub);
        Assert.Empty(health.GetHistory("c1"));
    }

    [Fact]
    public async Task Without_a_stop_the_health_cycle_has_the_effects_the_stop_test_rules_out()
    {
        var docker = new FakeDocker(Web()) { DetailedListingGate = new(), AllowMutations = true };
        var (monitor, health, _, metrics, hub) = HealthMonitor(docker);

        using var stopping = new CancellationTokenSource();
        var run = RunAsync(monitor, stopping.Token);
        docker.DetailedListingGate!.SetResult();
        await hub.Broadcast.Task.WaitAsync(Wait);                   // the cycle ran to its end

        Assert.NotEmpty(metrics.Loops());
        Assert.NotEmpty(health.GetHistory("c1"));
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait));
    }

    private static (ContainerHealthMonitor, InMemoryHealthStore, FakeNotifications,
        Whiskers.Services.Observability.SelfMetrics.SelfMetrics, RecordedHub) HealthMonitor(FakeDocker docker)
    {
        var settings = Options.Create(new HealthMonitorSettings());
        var health = new InMemoryHealthStore(settings);
        var notifications = new FakeNotifications();
        var metrics = new Whiskers.Services.Observability.SelfMetrics.SelfMetrics();
        var hub = new RecordedHub();
        var servers = new FakeServerConfig(Badwolf);
        var suspension = new Whiskers.Services.Observability.LoopSuspensionService(new FakeNotifications(), servers,
            NullLogger<Whiskers.Services.Observability.LoopSuspensionService>.Instance, new NoOutcomes());
        var monitor = new ContainerHealthMonitor(docker, health, notifications, RecordingHub.Create(hub), settings,
            NullLogger<ContainerHealthMonitor>.Instance, metrics, suspension, servers);
        return (monitor, health, notifications, metrics, hub);
    }

    /// <summary>The loop itself (<c>RunAsync</c>), without the host's startup offset.</summary>
    private static Task RunAsync(Whiskers.Services.FleetBackgroundService loop, CancellationToken ct) =>
        (Task)loop.GetType().GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(loop, [ct])!;

    // ---- notification delivery ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stop_ends_a_delivery_and_its_retries_and_keeps_a_critical_event()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new NotificationDeliveryTests.ScriptedHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        var outbox = NotificationOutbox.InMemory(NullLogger<NotificationOutbox>.Instance);
        var composite = NotificationDeliveryTests.Composite([NotificationDeliveryTests.Slack(handler)], outbox,
            TimeSpan.FromMinutes(5));

        using var stopping = new CancellationTokenSource();
        var send = composite.SendAsync(new NotificationEvent
        {
            EventType = "stopped", ContainerName = "web", ContainerId = "web-1", ExitCode = 137,
        }, stopping.Token);
        await started.Task.WaitAsync(Wait);

        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(Wait));

        Assert.Equal(1, handler.Requests);                 // no retry after the stop
        Assert.Equal(1, handler.Cancelled);                // the stop reached the network call
        var kept = Assert.Single(await outbox.DueAsync(DateTime.UtcNow.AddHours(1)));   // a critical event is not lost
        Assert.StartsWith("OperationCanceledException", kept.LastError);
    }

    // ---- doubles --------------------------------------------------------------------------------------------------

    /// <summary>Messages a hub context sent, with a signal on the first.</summary>
    internal sealed class RecordedHub : List<string>
    {
        public TaskCompletionSource Broadcast { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>An <see cref="IHubContext{THub}"/> whose clients record every method sent to them.</summary>
    internal class RecordingHub : DispatchProxy
    {
        private List<string> _sent = null!;

        public static IHubContext<ContainerHub> Create(List<string> sent)
        {
            var hub = DispatchProxy.Create<IHubContext<ContainerHub>, RecordingHub>();
            ((RecordingHub)(object)hub)._sent = sent;
            return hub;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var type = method!.ReturnType;
            if (method.Name == "SendCoreAsync")
            {
                lock (_sent) _sent.Add((string)args![0]!);
                (_sent as RecordedHub)?.Broadcast.TrySetResult();
                return Task.CompletedTask;
            }
            if (type.IsInterface)
            {
                var inner = DispatchProxy.Create(type, typeof(RecordingHub));
                ((RecordingHub)inner)._sent = _sent;
                return inner;
            }
            if (type == typeof(Task)) return Task.CompletedTask;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
