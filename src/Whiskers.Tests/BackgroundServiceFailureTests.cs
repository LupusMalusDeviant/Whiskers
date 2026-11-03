using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
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
    public void No_loop_lets_a_dependency_cancellation_end_it()
    {
        // The ratchet: inside a loop body, "ex is not OperationCanceledException" (or a cancellation caught without
        // asking the stopping token) treats a dependency's timeout as the host stopping.
        var offenders = new List<string>();
        foreach (var file in LoopSources())
        {
            var body = RunAsyncBody(File.ReadAllText(file));
            if (body is null) continue;
            if (body.Contains("is not OperationCanceledException", StringComparison.Ordinal) || HasBareCancellationCatch(body))
                offenders.Add(Path.GetFileName(file));
        }
        Assert.True(offenders.Count == 0,
            "These loops end on a dependency's cancellation; catch with `when (!IsHostStopping(ex, stoppingToken))`: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void The_ratchet_finds_the_loops()
    {
        var bodies = LoopSources().Select(f => RunAsyncBody(File.ReadAllText(f))).Where(b => b is not null).ToList();
        Assert.True(bodies.Count >= 10, $"expected the loops' RunAsync bodies, found {bodies.Count}");
        Assert.Contains(bodies, b => b!.Contains("IsHostStopping", StringComparison.Ordinal));
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

        var ex = Assert.Throws<OptionsValidationException>(() =>
        {
            _ = sp.GetRequiredService<IOptions<ImageUpdateSettings>>().Value;
            _ = sp.GetRequiredService<IOptions<CveMonitorSettings>>().Value;
        });
        Assert.Contains(key, ex.Message);
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

    private static IEnumerable<string> LoopSources()
    {
        var src = Path.Combine(SourceRoot(), "src", "Whiskers");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains(": Whiskers.Services.FleetBackgroundService", StringComparison.Ordinal)
                        || Regex.IsMatch(File.ReadAllText(f), @":\s*FleetBackgroundService\b"));
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
