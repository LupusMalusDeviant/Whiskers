using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker.Budget;
using Whiskers.Modules.Cve;
using Whiskers.Modules.ImageUpdate;
using Whiskers.Services;

namespace Whiskers.Tests;

/// <summary>
/// R1-41 (TA-52, F-15): a background loop ends only when the host stops it. A dependency's own timeout or cancellation
/// (an <c>HttpClient</c> timeout is a <see cref="TaskCanceledException"/>) is a failed cycle — logged, and the next cycle
/// runs. Before, eight loops caught with <c>when (ex is not OperationCanceledException)</c>: such a timeout escaped the
/// loop, ended it for good, and with the host's default behaviour stopped Whiskers. A loop interval is checked at start.
/// </summary>
public class BackgroundServiceFailureTests
{
    [Fact]
    public void Only_a_cancellation_while_the_host_stops_counts_as_stopping()
    {
        using var stopping = new CancellationTokenSource();

        Assert.False(FleetBackgroundService.IsHostStopping(new TaskCanceledException("HttpClient timeout"), stopping.Token));
        Assert.False(FleetBackgroundService.IsHostStopping(new OperationCanceledException(), stopping.Token));
        Assert.False(FleetBackgroundService.IsHostStopping(new InvalidOperationException(), stopping.Token));

        stopping.Cancel();
        Assert.True(FleetBackgroundService.IsHostStopping(new TaskCanceledException(), stopping.Token));
        Assert.True(FleetBackgroundService.IsHostStopping(new OperationCanceledException(stopping.Token), stopping.Token));
        Assert.False(FleetBackgroundService.IsHostStopping(new InvalidOperationException(), stopping.Token));
    }

    [Fact]
    public async Task A_dependency_timeout_fails_one_cycle_and_the_next_cycle_runs()
    {
        var loop = new FlakyLoop(failures: [new TaskCanceledException("registry timed out"), new OperationCanceledException()]);
        using var host = new HostBuilder().ConfigureServices(s => s.AddHostedService(_ => loop)).Build();
        await host.StartAsync();

        await loop.ThirdCycle.Task.WaitAsync(TimeSpan.FromSeconds(60));   // the startup offset is up to 30 s
        Assert.Equal(2, loop.Failed);
        Assert.False(loop.Ended);

        await host.StopAsync();
        Assert.True(loop.Ended);                                            // the host's stop does end it
    }

    [Fact]
    public async Task A_cancelled_caller_stops_waiting_for_a_fleet_listing()
    {
        // R1-38 carry-over: the AutoPatch planning and the advisor chat pass their token to the Docker reads.
        Whiskers.Services.Docker.IDockerService docker = new FakeDocker { ListingGate = new() };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => docker.ListAllContainersAsync(true, cts.Token));
    }

    [Fact]
    public void A_reloaded_interval_out_of_range_keeps_the_last_valid_one()
    {
        var guard = new IntervalGuard("Metrics:CollectionIntervalSeconds", IntervalValidation.MinMetricsSeconds,
            IntervalValidation.MaxMetricsSeconds, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(30, guard.Get(30));
        Assert.Equal(30, guard.Get(0));                          // reloaded invalid: the last valid one, no exception
        Assert.Equal(30, guard.Get(int.MaxValue));
        Assert.Equal(60, guard.Get(60));                         // fixed again: used
        var fresh = new IntervalGuard("x", 5, 86_400, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(5, fresh.Get(0));                           // nothing valid yet: clamped
    }

    [Fact]
    public void Saving_an_interval_out_of_range_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IntervalValidation.ThrowIfInvalid(new MetricsSettings { CollectionIntervalSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntervalValidation.ThrowIfInvalid(new CveMonitorSettings { CheckIntervalHours = 169 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntervalValidation.ThrowIfInvalid(new HealthMonitorSettings { CheckIntervalSeconds = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntervalValidation.ThrowIfInvalid(new ImageUpdateSettings { CheckIntervalHours = 0 }));
        IntervalValidation.ThrowIfInvalid(new MetricsSettings());
        IntervalValidation.ThrowIfInvalid(new { Other = 0 });
    }

    [Fact]
    public async Task A_caller_cancellation_is_not_held_against_the_server()
    {
        var (docker, circuit, _) = Connection();

        // The caller gives up while its call runs: nothing about the server.
        using (var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => docker.ExecuteAsync<int>("badwolf",
                async _ => { await Task.Delay(Timeout.Infinite, caller.Token); return 0; }, callerToken: caller.Token));
        Assert.Equal(0, circuit.Snapshot("badwolf").ConsecutiveFailures);

        // A deadline at the host does count.
        await Assert.ThrowsAsync<TimeoutException>(() => docker.ExecuteAsync<int>("badwolf",
            _ => throw new TimeoutException("the daemon did not answer")));
        Assert.Equal(1, circuit.Snapshot("badwolf").ConsecutiveFailures);
    }

    [Fact]
    public async Task A_caller_waiting_for_a_budget_slot_leaves_the_queue_when_it_cancels()
    {
        var (docker, circuit, _) = Connection();
        var release = new TaskCompletionSource();
        var busy = Enumerable.Range(0, new ServerBudgetSettings().InteractiveConcurrency)
            .Select(_ => docker.ExecuteAsync("badwolf", async _ => { await release.Task; return 0; })).ToList();

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var queued = docker.ExecuteAsync("badwolf", _ => Task.FromResult(1), callerToken: caller.Token);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotType<BudgetSaturatedException>(ex);
        release.SetResult();
        await Task.WhenAll(busy);
        Assert.Equal(0, circuit.Snapshot("badwolf").ConsecutiveFailures);
    }

    [Fact]
    public async Task A_stop_during_an_image_check_leaves_no_effect_behind()
    {
        var digest = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docker = new FakeDocker(new ContainerInfo { Id = "c1", Name = "web", Image = "nginx:1", ServerId = "badwolf", State = "running" })
        {
            Digest = _ => { reading.TrySetResult(); return digest.Task; },
        };
        var notifications = new FakeNotifications();
        var services = new ServiceCollection();
        services.AddSingleton<Whiskers.Services.Docker.IDockerService>(docker);
        services.AddSingleton<Whiskers.Services.Notifications.INotificationService>(notifications);
        services.AddSingleton(ComponentTests.ServersKubeconfigSaveTests.NullProxy<
            Microsoft.AspNetCore.SignalR.IHubContext<Whiskers.Hubs.ContainerHub>>.Create());
        services.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(Badwolf));
        using var sp = services.BuildServiceProvider();
        var dir = Path.Combine(Path.GetTempPath(), $"sw-r141-{Guid.NewGuid():N}");
        var store = new Whiskers.Services.ImageUpdate.ImageUpdateStore(dataPaths: new DataPathOptions(dir));
        var registry = new StaticRegistry("sha256:remote");
        var checker = new Whiskers.Services.ImageUpdate.ImageUpdateChecker(sp, store, registry,
            Options.Create(new ImageUpdateSettings { NotifyOnUpdate = true }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Whiskers.Services.ImageUpdate.ImageUpdateChecker>.Instance,
            new Whiskers.Services.Observability.SelfMetrics.SelfMetrics());
        try
        {
            using var stopping = new CancellationTokenSource();
            var cycle = checker.CheckAllImagesAsync(stopping.Token);
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the cycle waits for the image read

            stopping.Cancel();                                       // the host stops
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle.WaitAsync(TimeSpan.FromSeconds(10)));
            digest.SetResult("sha256:local");                        // the read answers after the stop
            await Task.Delay(100);

            Assert.Empty(store.GetAllPendingUpdates());
            Assert.Null(store.LastCheckAt);
            Assert.Empty(notifications.Events);
            Assert.False(store.IsChecking);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ } }
    }

    private static readonly ServerConfig Badwolf = new()
    {
        Id = "badwolf", Name = "Badwolf", IsDefault = true, Enabled = true, ConnectionType = ConnectionType.Local,
    };

    private static (Whiskers.Services.Docker.DockerConnectionManager Docker, ServerCircuitBreaker Circuit, ServerBudget Budget) Connection()
    {
        var servers = new FakeServerConfig(Badwolf);
        var settings = new StaticOptionsMonitor<ServerBudgetSettings>(new ServerBudgetSettings());
        var budget = new ServerBudget(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerBudget>.Instance);
        var circuit = new ServerCircuitBreaker(settings, new ServiceCollection().BuildServiceProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerCircuitBreaker>.Instance);
        var suspension = new Whiskers.Services.Observability.LoopSuspensionService(new FakeNotifications(), servers,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Whiskers.Services.Observability.LoopSuspensionService>.Instance,
            new NoOutcomes());
        var docker = new Whiskers.Services.Docker.DockerConnectionManager(servers, sshTunnelManager: null!, budget, circuit,
            suspension, Microsoft.Extensions.Logging.Abstractions.NullLogger<Whiskers.Services.Docker.DockerConnectionManager>.Instance)
        {
            ClientSourceForTests = _ => Task.FromResult<Docker.DotNet.DockerClient>(null!),
        };
        return (docker, circuit, budget);
    }

    private sealed class StaticRegistry(string digest) : Whiskers.Services.ImageUpdate.IRegistryClient
    {
        public Task<string?> GetRemoteDigestAsync(string imageRef) => Task.FromResult<string?>(digest);
    }

    [Fact]
    public void Every_loop_recovers_from_a_failed_cycle_and_ends_only_on_the_host_stop()
    {
        // The ratchet, over every FleetBackgroundService in the assembly — not over what a text search happens to find:
        // a loop whose RunAsync cannot be read (an expression body, a renamed parameter) fails, as does one without a
        // recovering catch or with one that lets a dependency's cancellation end it.
        var sources = LoopSourceFiles().Select(File.ReadAllText).ToList();
        var offenders = new List<string>();
        foreach (var loop in FleetLoops())
        {
            var source = sources.FirstOrDefault(s => Regex.IsMatch(s, $@"\bclass {loop.Name}\b[^{{]*FleetBackgroundService"));
            var verdict = source is null ? "source not found" : Verdict(RunAsyncBody(source));
            if (verdict is not null) offenders.Add($"{loop.Name}: {verdict}");
        }
        Assert.True(offenders.Count == 0, "Loops that do not recover from a failed cycle the R1-41 way " +
            "(`catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))`, or a cancellation caught only when the stopping " +
            "token is cancelled followed by a catch of everything else): " + string.Join("; ", offenders));
    }

    [Fact]
    public void The_ratchet_covers_every_loop_and_fails_what_it_should()
    {
        Assert.True(FleetLoops().Count >= 15, $"expected the fleet loops, found {FleetLoops().Count}");

        // Mutation cases: what the ratchet must refuse.
        Assert.Null(Verdict("while (!ct.IsCancellationRequested) { try { await Run(ct); } catch (Exception ex) when (!IsHostStopping(ex, ct)) { Log(ex); } }"));
        Assert.Null(Verdict("while (!ct.IsCancellationRequested) { try { await Run(ct); }\ncatch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }\ncatch (Exception ex) { Log(ex); } }"));
        Assert.NotNull(Verdict(null));                                                                // expression body
        Assert.NotNull(Verdict("while (!ct.IsCancellationRequested) { await Run(ct); await Task.Delay(1, ct); }"));   // no recovery
        Assert.NotNull(Verdict("try { await Run(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }"));
        Assert.NotNull(Verdict("try { await Run(ct); }\ncatch (OperationCanceledException) { break; }\ncatch (Exception ex) { }"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IntervalValidation.MaxHealthSeconds + 1)]
    public async Task An_interval_out_of_range_stops_the_start_with_a_clear_error(int seconds)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddOptions<HealthMonitorSettings>().Configure(s => s.CheckIntervalSeconds = seconds)
            .ValidateInterval(s => s.CheckIntervalSeconds, "HealthMonitor:CheckIntervalSeconds",
                IntervalValidation.MinHealthSeconds, IntervalValidation.MaxHealthSeconds);
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("HealthMonitor:CheckIntervalSeconds", ex.Message);
    }

    [Theory]
    [InlineData("ImageUpdate:CheckIntervalHours", "0")]
    [InlineData("ImageUpdate:CheckIntervalHours", "169")]
    [InlineData("CveMonitor:CheckIntervalHours", "0")]
    [InlineData("CveMonitor:CheckIntervalHours", "100000")]
    public void The_module_check_intervals_are_validated(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new ImageUpdateModule().ConfigureServices(services, config);
        new CveModule().ConfigureServices(services, config);
        using var sp = services.BuildServiceProvider();

        // Checked when the host starts, before any loop: the registered startup check refuses it.
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(IntervalValidation.IntervalStartupCheck));
        var check = new IntervalValidation.IntervalStartupCheck(sp.GetServices<IntervalValidation.IIntervalCheck>());
        var ex = Assert.Throws<OptionsValidationException>(() => check.StartingAsync(CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains(key, ex.Message);
        // The options themselves do not throw: a later reload must not end the loop that reads them.
        _ = sp.GetRequiredService<IOptions<ImageUpdateSettings>>().Value;
        _ = sp.GetRequiredService<IOptions<CveMonitorSettings>>().Value;
    }

    [Fact]
    public void The_defaults_are_in_range()
    {
        Assert.InRange(new HealthMonitorSettings().CheckIntervalSeconds, IntervalValidation.MinHealthSeconds, IntervalValidation.MaxHealthSeconds);
        Assert.InRange(new MetricsSettings().CollectionIntervalSeconds, IntervalValidation.MinMetricsSeconds, IntervalValidation.MaxMetricsSeconds);
        Assert.InRange(new ImageUpdateSettings().CheckIntervalHours, IntervalValidation.MinCheckHours, IntervalValidation.MaxCheckHours);
        Assert.InRange(new CveMonitorSettings().CheckIntervalHours, IntervalValidation.MinCheckHours, IntervalValidation.MaxCheckHours);
    }

    /// <summary>A loop written the way the loops are: one cycle, catch, pause.</summary>
    private sealed class FlakyLoop(Exception[] failures) : FleetBackgroundService
    {
        private int _cycle;
        public int Failed;
        public bool Ended;
        public TaskCompletionSource ThirdCycle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task RunAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var n = _cycle++;
                        if (n < failures.Length) throw failures[n];
                        if (n == failures.Length) ThirdCycle.TrySetResult();
                    }
                    catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))
                    {
                        Failed++;
                    }
                    try { await Task.Delay(10, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally { Ended = true; }
        }
    }

    private static IReadOnlyList<Type> FleetLoops() =>
        typeof(FleetBackgroundService).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(FleetBackgroundService).IsAssignableFrom(t))
            .ToList();

    private static IEnumerable<string> LoopSourceFiles()
    {
        var src = Path.Combine(SourceRoot(), "src", "Whiskers");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("FleetBackgroundService", StringComparison.Ordinal));
    }

    /// <summary>Null when the body recovers from a failed cycle and ends only on the host's stop; otherwise why not.</summary>
    internal static string? Verdict(string? body)
    {
        if (body is null) return "RunAsync is not a block body the ratchet can read";
        if (body.Contains("is not OperationCanceledException", StringComparison.Ordinal))
            return "`is not OperationCanceledException` lets a dependency's cancellation end the loop";
        if (HasBareCancellationCatch(body)) return "a cancellation is caught without asking the stopping token";
        var recovers = body.Contains("IsHostStopping(", StringComparison.Ordinal)
                       || (Regex.IsMatch(body, @"catch\s*\(\s*OperationCanceledException\s*\)\s*when\s*\(\s*\w+\.IsCancellationRequested\s*\)")
                           && Regex.IsMatch(body, @"catch\s*\(\s*Exception(\s+\w+)?\s*\)\s*(\{|$)", RegexOptions.Multiline));
        return recovers ? null : "no catch that recovers from a failed cycle";
    }

    /// <summary>A cancellation caught without asking the stopping token — except right after the loop's own pause,
    /// <c>try { await Task.Delay(…, token); }</c>, where only the stopping token can cancel.</summary>
    private static bool HasBareCancellationCatch(string body)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^catch\s*\(\s*(Task|Operation)CanceledException(\s+\w+)?\s*\)\s*(\{|$)")) continue;
            if (i > 0 && Regex.IsMatch(lines[i - 1], @"^try\s*\{\s*await Task\.Delay\([^;]*\);\s*\}$")) continue;
            return true;
        }
        return false;
    }

    /// <summary>The body of <c>RunAsync</c>, by brace matching from its signature.</summary>
    private static string? RunAsyncBody(string source)
    {
        var m = Regex.Match(source, @"protected override async Task RunAsync\(CancellationToken \w+\)\s*\{");
        if (!m.Success) return null;
        var depth = 1;
        var i = m.Index + m.Length;
        for (; i < source.Length && depth > 0; i++)
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
        return source[(m.Index + m.Length)..i];
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}
