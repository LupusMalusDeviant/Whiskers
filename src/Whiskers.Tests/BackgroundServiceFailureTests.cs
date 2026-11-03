using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
        var loop = new FlakyLoop(failures: [new TaskCanceledException("registry timed out"), new OperationCanceledException()])
        {
            StartupOffsetForTests = TimeSpan.Zero,   // the real hosted path, without waiting out the startup spread
        };
        using var host = new HostBuilder().ConfigureServices(s => s.AddHostedService(_ => loop)).Build();
        await host.StartAsync();

        await loop.ThirdCycle.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
            IntervalValidation.MaxMetricsSeconds, NullLogger.Instance);

        Assert.Equal(30, guard.Get(30));
        Assert.Equal(30, guard.Get(0));                          // reloaded invalid: the last valid one, no exception
        Assert.Equal(30, guard.Get(int.MaxValue));
        Assert.Equal(60, guard.Get(60));                         // fixed again: used
        var fresh = new IntervalGuard("x", 5, 86_400, NullLogger.Instance);
        Assert.Equal(5, fresh.Get(0));                           // nothing valid yet: clamped
    }

    [Fact]
    public void The_effective_interval_is_one_value_for_every_consumer()
    {
        // R1-41: the loop, /metrics and the MCP summary resolve the same EffectiveInterval, so a reload that is out of
        // range changes none of them — and a TimeSpan is never built from the raw value.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CveMonitor:CheckIntervalHours"] = "12" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new CveModule().ConfigureServices(services, config);
        using var sp = services.BuildServiceProvider();
        var shared = sp.GetRequiredService<EffectiveInterval<CveMonitorSettings>>();
        Assert.Same(shared, sp.GetRequiredService<EffectiveInterval<CveMonitorSettings>>());
        Assert.Equal(TimeSpan.FromHours(12), shared.Interval);

        config["CveMonitor:CheckIntervalHours"] = "2147483647";
        config.Reload();
        Assert.Equal(int.MaxValue, sp.GetRequiredService<IOptionsMonitor<CveMonitorSettings>>().CurrentValue.CheckIntervalHours);
        Assert.Equal(TimeSpan.FromHours(12), shared.Interval);
        Assert.Equal(TimeSpan.FromHours(12),
            IntervalValidation.CveCheckInterval(sp, sp.GetRequiredService<IOptionsMonitor<CveMonitorSettings>>().CurrentValue));

        // Without the shared one (a module registration missing) the raw value is clamped, never converted as it is.
        Assert.Equal(TimeSpan.FromHours(IntervalValidation.MaxCheckHours),
            IntervalValidation.CveCheckInterval(null, new CveMonitorSettings { CheckIntervalHours = int.MaxValue }));
    }

    [Fact]
    public void Saving_an_interval_out_of_range_is_refused()
    {
        Assert.Throws<IntervalOutOfRangeException>(() => IntervalValidation.ThrowIfInvalid(new MetricsSettings { CollectionIntervalSeconds = 0 }));
        var refused = Assert.Throws<IntervalOutOfRangeException>(() => IntervalValidation.ThrowIfInvalid(new CveMonitorSettings { CheckIntervalHours = 721 }));
        Assert.Equal(("CveMonitor:CheckIntervalHours", 1, 720), (refused.Key, refused.Min, refused.Max));   // what the page says
        Assert.Throws<IntervalOutOfRangeException>(() => IntervalValidation.ThrowIfInvalid(new HealthMonitorSettings { CheckIntervalSeconds = 1 }));
        Assert.Throws<IntervalOutOfRangeException>(() => IntervalValidation.ThrowIfInvalid(new ImageUpdateSettings { CheckIntervalHours = 0 }));
        IntervalValidation.ThrowIfInvalid(new ImageUpdateSettings { CheckIntervalHours = 720 });   // monthly is fine
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
    public async Task A_server_deadline_that_runs_out_in_the_queue_is_saturation_not_the_server_and_not_the_caller()
    {
        var (docker, circuit, budget) = Connection();
        var release = new TaskCompletionSource();
        var busy = Enumerable.Range(0, new ServerBudgetSettings().InteractiveConcurrency)
            .Select(_ => docker.ExecuteAsync("badwolf", async _ => { await release.Task; return 0; })).ToList();

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<BudgetSaturatedException>(() => docker.ExecuteAsync("badwolf", _ => Task.FromResult(1),
            deadline: deadline.Token).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, budget.Snapshot("badwolf").SaturationFailures);
        Assert.Equal(0, circuit.Snapshot("badwolf").ConsecutiveFailures);
        release.SetResult();
        await Task.WhenAll(busy);
    }

    [Fact]
    public async Task A_fleet_listing_does_not_queue_past_the_server_deadline_for_a_slot()
    {
        // Review round 2: the 8 s per-server timeout reached only the Docker request. With every slot of a server taken,
        // the listing waited for one without end when its caller had no deadline of its own.
        var (docker, circuit, budget) = Connection();
        var release = new TaskCompletionSource();
        var busy = Enumerable.Range(0, new ServerBudgetSettings().InteractiveConcurrency)
            .Select(_ => docker.ExecuteAsync("badwolf", async _ => { await release.Task; return 0; })).ToList();
        var ops = Operations(docker, TimeSpan.FromMilliseconds(200));

        var listing = await ops.ListAllContainersDetailedAsync(true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // The server's timeout: reported as a silent server, not held against it, counted as the queue's doing.
        var failure = Assert.Single(listing.FailedServers);
        Assert.Equal("badwolf", failure.ServerId);
        Assert.StartsWith("Timed out", failure.Error);
        Assert.Empty(listing.RespondedServerIds);
        Assert.Equal(0, circuit.Snapshot("badwolf").ConsecutiveFailures);
        Assert.Equal(1, budget.Snapshot("badwolf").SaturationFailures);
        release.SetResult();
        await Task.WhenAll(busy);
    }

    [Fact]
    public async Task A_caller_cancelling_a_fleet_listing_queued_for_a_slot_stays_a_cancellation()
    {
        var (docker, circuit, budget) = Connection();
        var release = new TaskCompletionSource();
        var busy = Enumerable.Range(0, new ServerBudgetSettings().InteractiveConcurrency)
            .Select(_ => docker.ExecuteAsync("badwolf", async _ => { await release.Task; return 0; })).ToList();
        var ops = Operations(docker, TimeSpan.FromMinutes(5));

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ops.ListAllContainersDetailedAsync(true, caller.Token).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(0, budget.Snapshot("badwolf").SaturationFailures);   // not the queue's doing
        Assert.Equal(0, circuit.Snapshot("badwolf").ConsecutiveFailures); // not the server's
        release.SetResult();
        await Task.WhenAll(busy);
    }

    [Fact]
    public async Task The_per_server_listing_and_the_log_read_keep_the_stop_and_the_server_deadline_apart()
    {
        // Review round 3: the log-alert and log-hygiene loops' per-server calls passed no caller token, so their stop did
        // not reach the waits for a client or a slot, and a cancellation there could be counted against the server.
        var (docker, circuit, budget) = Connection();
        var release = new TaskCompletionSource();
        var busy = Enumerable.Range(0, new ServerBudgetSettings().InteractiveConcurrency)
            .Select(_ => docker.ExecuteAsync("badwolf", async _ => { await release.Task; return 0; })).ToList();
        var ops = Operations(docker, TimeSpan.FromMinutes(5));

        // The stop while queued: a cancellation, not the queue's, not the server's.
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ops.ListContainersAsync(true, "badwolf", stop.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        using (var fetchTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ops.GetContainerLogsAsync("c1", 10, "badwolf",
                null, deadline: fetchTimeout.Token, callerToken: stop.Token).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsNotType<BudgetSaturatedException>(ex);
        }
        Assert.Equal(0, budget.Snapshot("badwolf").SaturationFailures);
        Assert.Equal(0, circuit.Snapshot("badwolf").ConsecutiveFailures);

        // The fetch timeout running out in the queue: the queue's doing, still not the server's.
        using (var fetchTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAsync<BudgetSaturatedException>(() => ops.GetContainerLogsAsync("c1", 10, "badwolf", null,
                deadline: fetchTimeout.Token, callerToken: CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, budget.Snapshot("badwolf").SaturationFailures);
        Assert.Equal(0, circuit.Snapshot("badwolf").ConsecutiveFailures);

        release.SetResult();
        await Task.WhenAll(busy);
    }

    [Fact]
    public async Task A_deadline_spent_before_the_queue_is_not_counted_as_saturation()
    {
        var budget = (ServerBudget)TestBudget.Create();
        var release = new TaskCompletionSource();
        var busy = Enumerable.Range(0, 4).Select(_ => budget.RunAsync("s1", async () => { await release.Task; return 0; })).ToList();

        await Assert.ThrowsAsync<BudgetSaturatedException>(() => budget.RunAsync("s1", () => Task.FromResult(1),
            deadline: new CancellationToken(canceled: true)));
        Assert.Equal(0, budget.Snapshot("s1").SaturationFailures);

        release.SetResult();
        await Task.WhenAll(busy);
    }

    private static readonly ServerConfig Badwolf = new()
    {
        Id = "badwolf", Name = "Badwolf", IsDefault = true, Enabled = true, ConnectionType = ConnectionType.Local,
    };

    private static Whiskers.Services.Docker.Operations.ContainerOperations Operations(
        Whiskers.Services.Docker.DockerConnectionManager docker, TimeSpan perServer) =>
        new(docker, new FakeServerConfig(Badwolf), NullLogger<Whiskers.Services.Docker.DockerService>.Instance,
            new MemoryCache(new MemoryCacheOptions()), new Whiskers.Services.Observability.SelfMetrics.SelfMetrics())
        {
            PerServerTimeout = perServer,
        };

    private static (Whiskers.Services.Docker.DockerConnectionManager Docker, ServerCircuitBreaker Circuit, ServerBudget Budget) Connection()
    {
        var servers = new FakeServerConfig(Badwolf);
        var settings = new StaticOptionsMonitor<ServerBudgetSettings>(new ServerBudgetSettings());
        var budget = new ServerBudget(settings, NullLogger<ServerBudget>.Instance);
        var circuit = new ServerCircuitBreaker(settings, new ServiceCollection().BuildServiceProvider(),
            NullLogger<ServerCircuitBreaker>.Instance);
        var suspension = new Whiskers.Services.Observability.LoopSuspensionService(new FakeNotifications(), servers,
            NullLogger<Whiskers.Services.Observability.LoopSuspensionService>.Instance, new NoOutcomes());
        var docker = new Whiskers.Services.Docker.DockerConnectionManager(servers, sshTunnelManager: null!, budget, circuit,
            suspension, NullLogger<Whiskers.Services.Docker.DockerConnectionManager>.Instance)
        {
            ClientSourceForTests = _ => Task.FromResult<Docker.DotNet.DockerClient>(null!),
        };
        return (docker, circuit, budget);
    }

    [Fact]
    public void Every_loop_recovers_from_a_failed_cycle_and_ends_only_on_the_host_stop()
    {
        // The ratchet, over every FleetBackgroundService in the assembly — not over what a text search happens to find.
        // It reads the syntax tree: the catch clauses, their types and filters, where they sit and whether their body
        // keeps the loop going. Comments and strings do not count.
        var classes = LoopSourceFiles()
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f).GetRoot())
            .SelectMany(r => r.DescendantNodes().OfType<ClassDeclarationSyntax>())
            .ToList();
        var offenders = new List<string>();
        foreach (var loop in FleetLoops())
        {
            var declarations = classes.Where(c => c.Identifier.ValueText == loop.Name).ToList();
            if (declarations.Count == 0) { offenders.Add($"{loop.Name}: source not found"); continue; }
            var run = declarations.SelectMany(c => c.Members.OfType<MethodDeclarationSyntax>())
                .SingleOrDefault(m => m.Identifier.ValueText == "RunAsync" && m.Modifiers.Any(SyntaxKind.OverrideKeyword));
            if (Verdict(run) is { } verdict) offenders.Add($"{loop.Name}: {verdict}");
        }
        Assert.True(offenders.Count == 0, "Loops that do not recover from a failed cycle the R1-41 way " +
            "(`catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))` inside the loop with a body that carries on, " +
            "or a cancellation caught only when the stopping token is cancelled followed by a catch of everything else): " +
            string.Join("; ", offenders));
    }

    [Fact]
    public void The_ratchet_covers_every_loop_and_fails_what_it_should()
    {
        Assert.True(FleetLoops().Count >= 15, $"expected the fleet loops, found {FleetLoops().Count}");

        const string loop = "while (!ct.IsCancellationRequested) {{ {0} try {{ await Task.Delay(1, ct); }} catch (OperationCanceledException) {{ break; }} }}";
        string InLoop(string cycle) => string.Format(loop, cycle);

        // What it accepts: the two R1-41 shapes, a pause guarded by its own catch, a break that leaves only an inner loop.
        Assert.Null(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (!IsHostStopping(ex, ct)) { Log(ex); }")));
        Assert.Null(VerdictOf(InLoop("try { await Run(ct); }\ncatch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }\ncatch (Exception ex) { Log(ex); }")));
        Assert.Null(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (!IsHostStopping(ex, ct)) { foreach (var x in Xs) { if (x) break; } continue; }")));
        Assert.Null(VerdictOf("try { await Once(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { } catch (Exception ex) { Log(ex); }"));   // a one-shot service

        // What it refuses.
        Assert.NotNull(Verdict(null));                                                                      // expression body
        Assert.NotNull(VerdictOf(InLoop("await Run(ct);")));                                                // no recovery
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (IsHostStopping(ex, ct)) { Log(ex); }")));   // reversed filter
        Assert.NotNull(VerdictOf(InLoop("// catch (Exception ex) when (!IsHostStopping(ex, ct)) { }\nawait Run(ct);")));               // a comment
        Assert.NotNull(VerdictOf(InLoop("await Run(ct); Log(\"catch (Exception ex) when (!IsHostStopping(ex, ct))\");")));            // a string
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (!IsHostStopping(ex, ct)) { Log(ex); break; }")));
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (!IsHostStopping(ex, ct)) { return; }")));
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (!IsHostStopping(ex, ct)) { Log(ex); throw; }")));
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (!IsHostStopping(ex, ct)) { throw new InvalidOperationException(\"x\", ex); }")));
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); }\ncatch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }\ncatch (Exception ex) { Log(ex); return; }")));
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (!IsHostStopping(ex, other)) { Log(ex); }")));   // another token
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (!IsHostStopping(e2, ct)) { Log(ex); }")));     // another exception
        Assert.NotNull(VerdictOf("try { " + InLoop("await Run(ct);") + " } catch (Exception ex) when (!IsHostStopping(ex, ct)) { Log(ex); }"));   // outside the loop
        Assert.NotNull(VerdictOf(InLoop("foreach (var s in Servers()) { try { await Run(s, ct); } catch (Exception ex) when (!IsHostStopping(ex, ct)) { Log(ex); } }")));   // only in a nested loop
        Assert.Null(VerdictOf(InLoop("try { foreach (var s in Servers()) { try { await Run(s, ct); } catch (Exception ex) when (!IsHostStopping(ex, ct)) { Log(ex); } } } catch (Exception ex) when (!IsHostStopping(ex, ct)) { Log(ex); }")));   // nested one too, outer one present
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { }")));
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); }\ncatch (OperationCanceledException) { break; }\ncatch (Exception ex) { }")));
        Assert.NotNull(VerdictOf(InLoop("try { await Run(ct); }\ncatch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }\ncatch (Exception ex) { }")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task An_interval_that_would_break_the_loop_stops_the_start_with_a_clear_error(int seconds)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddOptions<HealthMonitorSettings>().Configure(s => s.CheckIntervalSeconds = seconds)
            .ValidateInterval(s => s.CheckIntervalSeconds, "HealthMonitor:CheckIntervalSeconds",
                IntervalValidation.MinHealthSeconds, IntervalValidation.MaxHealthSeconds, TimeSpan.FromSeconds(1));
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("HealthMonitor:CheckIntervalSeconds", ex.Message);
        Assert.Contains("app-settings.json", ex.Message);                 // where to fix it
    }

    [Theory]
    [InlineData(1, IntervalValidation.MinHealthSeconds)]
    [InlineData(IntervalValidation.MaxHealthSeconds + 1, IntervalValidation.MaxHealthSeconds)]
    [InlineData(int.MaxValue, IntervalValidation.MaxHealthSeconds)]
    public async Task A_positive_interval_out_of_range_starts_with_a_warning_and_is_clamped(int seconds, int used)
    {
        // Review round 3: earlier versions accepted such values; refusing them would make an upgrade a restart loop.
        var logs = new NotificationDeliveryTests.ListLoggerProvider();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
        builder.Services.AddOptions<HealthMonitorSettings>().Configure(s => s.CheckIntervalSeconds = seconds)
            .ValidateInterval(s => s.CheckIntervalSeconds, "HealthMonitor:CheckIntervalSeconds",
                IntervalValidation.MinHealthSeconds, IntervalValidation.MaxHealthSeconds, TimeSpan.FromSeconds(1));
        using var host = builder.Build();

        await host.StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(used), host.Services.GetRequiredService<EffectiveInterval<HealthMonitorSettings>>().Interval);
        Assert.Contains(logs.Lines, l => l.Contains("HealthMonitor:CheckIntervalSeconds"));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("ImageUpdate:CheckIntervalHours", "0")]
    [InlineData("ImageUpdate:CheckIntervalHours", "-3")]
    [InlineData("CveMonitor:CheckIntervalHours", "0")]
    [InlineData("CveMonitor:CheckIntervalHours", "-100000")]
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

    /// <summary>The verdict on a <c>RunAsync</c> body given as text (the mutation cases), with <c>ct</c> as its token.</summary>
    internal static string? VerdictOf(string body)
    {
        var root = CSharpSyntaxTree.ParseText(
            "class Loop : FleetBackgroundService { protected override async Task RunAsync(CancellationToken ct) {\n" + body + "\n} }").GetRoot();
        return Verdict(root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single());
    }

    /// <summary>Null when <c>RunAsync</c> recovers from a failed cycle and ends only on the host's stop; otherwise why not.</summary>
    internal static string? Verdict(MethodDeclarationSyntax? run)
    {
        if (run?.Body is null || run.ParameterList.Parameters.Count != 1)
            return "RunAsync is not a block body with one token parameter the ratchet can read";
        var token = run.ParameterList.Parameters[0].Identifier.ValueText;
        var body = run.Body;

        foreach (var c in Outside<CatchClauseSyntax>(body))
            if (Problem(c, token) is { } problem) return problem;

        // The recovering catch must sit inside the loop: one outside it lets the first failure end the loop. And inside the
        // OUTERMOST loop's body: a catch in a nested loop (a foreach over the servers) leaves everything between the loops —
        // the enumeration itself included — free to end the outer one.
        var loops = Outside<StatementSyntax>(body)
            .Where(IsLoop)
            .Where(l => !l.Ancestors().TakeWhile(a => a != body).OfType<StatementSyntax>().Any(IsLoop))
            .ToList();
        var candidates = loops.Count > 0
            ? loops.SelectMany(l => Outside<TryStatementSyntax>(LoopBody(l))
                .Where(t => !t.Ancestors().TakeWhile(a => a != l).OfType<StatementSyntax>().Any(IsLoop)))
            : body.Statements.OfType<TryStatementSyntax>();
        return candidates.Any(t => Recovers(t, token))
            ? null
            : "no catch inside the loop that recovers from a failed cycle and lets the loop go on";
    }

    /// <summary>A catch that ends the loop on something other than the host's stop.</summary>
    private static string? Problem(CatchClauseSyntax c, string token)
    {
        var filter = c.Filter?.FilterExpression;
        if (filter is not null && filter.DescendantNodesAndSelf().OfType<IsPatternExpressionSyntax>().Any(p =>
                p.Pattern is UnaryPatternSyntax { Pattern: TypePatternSyntax or ConstantPatternSyntax } u
                && u.OperatorToken.IsKind(SyntaxKind.NotKeyword) && Code(u.Pattern).EndsWith("CanceledException", StringComparison.Ordinal)))
            return "`is not OperationCanceledException` lets a dependency's cancellation end the loop";
        if (!IsCancellationType(c)) return null;
        if (filter is not null && AsksTheStoppingToken(filter, token, c)) return null;
        if (IsThePause(c, token)) return null;
        return "a cancellation is caught without asking the stopping token";
    }

    /// <summary>Recovers: <c>catch (Exception ex) when (!IsHostStopping(ex, token))</c>, or a cancellation caught only while
    /// the token is cancelled followed by an unfiltered <c>catch (Exception)</c> — and in either case the recovering body
    /// carries on (no <c>break</c> out of the loop, <c>return</c>, <c>throw</c> or <c>goto</c>).</summary>
    private static bool Recovers(TryStatementSyntax t, string token)
    {
        for (var i = 0; i < t.Catches.Count; i++)
        {
            var c = t.Catches[i];
            if (TypeName(c) != "Exception" || Terminates(c.Block)) continue;
            var name = c.Declaration?.Identifier.ValueText;
            if (c.Filter?.FilterExpression is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not
                && Unparenthesized(not.Operand) is InvocationExpressionSyntax call && IsHostStoppingCall(call, name, token))
                return true;
            if (c.Filter is null && t.Catches.Take(i).Any(o => IsCancellationType(o) && o.Filter is not null
                                                           && AsksTheStoppingToken(o.Filter.FilterExpression, token, o)))
                return true;
        }
        return false;
    }

    private static bool IsHostStoppingCall(InvocationExpressionSyntax call, string? exceptionName, string token) =>
        call.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText == "IsHostStopping",
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText == "IsHostStopping",
            _ => false,
        }
        && call.ArgumentList.Arguments.Count == 2
        && exceptionName is not null
        && call.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax e && e.Identifier.ValueText == exceptionName
        && call.ArgumentList.Arguments[1].Expression is IdentifierNameSyntax k && k.Identifier.ValueText == token;

    /// <summary>The filter is <c>token.IsCancellationRequested</c> (or <c>IsHostStopping(ex, token)</c>) itself — not negated.</summary>
    private static bool AsksTheStoppingToken(ExpressionSyntax filter, string token, CatchClauseSyntax c) =>
        Unparenthesized(filter) switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText == "IsCancellationRequested"
                                              && m.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == token,
            InvocationExpressionSyntax call => IsHostStoppingCall(call, c.Declaration?.Identifier.ValueText, token),
            _ => false,
        };

    /// <summary><c>try { await Task.Delay(…, token); } catch (OperationCanceledException) { … }</c>: the loop's own pause,
    /// which only the stopping token can cancel.</summary>
    private static bool IsThePause(CatchClauseSyntax c, string token) =>
        c.Parent is TryStatementSyntax { Block.Statements: [ExpressionStatementSyntax { Expression: AwaitExpressionSyntax
        {
            Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Delay" } d } call
        } }] } && Code(d.Expression) == "Task"
        && call.ArgumentList.Arguments.LastOrDefault()?.Expression is IdentifierNameSyntax last && last.Identifier.ValueText == token;

    /// <summary>Whether the block can leave the loop: <c>return</c>, <c>throw</c>, <c>goto</c>, <c>yield break</c>, or a
    /// <c>break</c> not inside a loop or switch of its own.</summary>
    private static bool Terminates(BlockSyntax block) =>
        Outside<SyntaxNode>(block).Any(n => n switch
        {
            ReturnStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax or GotoStatementSyntax => true,
            YieldStatementSyntax y => y.IsKind(SyntaxKind.YieldBreakStatement),
            BreakStatementSyntax b => !b.Ancestors().TakeWhile(a => a != block)
                .Any(a => a is SwitchStatementSyntax || (a is StatementSyntax s && IsLoop(s))),
            _ => false,
        });

    private static bool IsLoop(StatementSyntax s) =>
        s is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForEachStatementSyntax;

    private static StatementSyntax LoopBody(StatementSyntax loop) => loop switch
    {
        WhileStatementSyntax w => w.Statement,
        DoStatementSyntax d => d.Statement,
        ForStatementSyntax f => f.Statement,
        ForEachStatementSyntax e => e.Statement,
        _ => loop,
    };

    private static bool IsCancellationType(CatchClauseSyntax c) =>
        TypeName(c) is "OperationCanceledException" or "TaskCanceledException";

    private static string? TypeName(CatchClauseSyntax c) => c.Declaration?.Type switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => c.Declaration is null ? "Exception" : null,   // a bare `catch` catches everything
    };

    private static ExpressionSyntax Unparenthesized(ExpressionSyntax e) =>
        e is ParenthesizedExpressionSyntax p ? Unparenthesized(p.Expression) : e;

    /// <summary>The code of a node without trivia — no comments, no whitespace.</summary>
    private static string Code(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));

    /// <summary>The descendants of <paramref name="root"/> that run as part of it — not inside a lambda or a local
    /// function, whose catches belong to other code paths.</summary>
    private static IEnumerable<T> Outside<T>(SyntaxNode root) where T : SyntaxNode =>
        root.DescendantNodesAndSelf(n => n == root || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<T>()
            .Where(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}
