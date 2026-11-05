using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Docker.Operations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>R1-57 prerequisite (INV-40): the SSH-free host shell over the mTLS Docker connection honours one deadline
/// across setup (client, image check and pull, create), the run, the output and the clean-up. Each test stalls one
/// Docker step forever — a step that also ignores its cancellation token, the worst case a client library can produce —
/// and proves the caller still has its answer within the deadline plus the clean-up budget, told what matches where the
/// run stood; a privileged helper is never simply abandoned. The local/SSH process path is driven with a real process.</summary>
public sealed class HostShellDeadlineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Cleanup = TimeSpan.FromMilliseconds(500);
    /// <summary>Timeout + clean-up budget + scheduling slack. A stalled step that held the caller would cross it; a run
    /// that crosses it fails (it does not hang the suite).</summary>
    private static readonly TimeSpan Bound = Timeout + Cleanup + TimeSpan.FromSeconds(2);

    private readonly FakeDocker _docker = new();

    private HostShellOperations Shell(TimeSpan? backgroundCleanup = null) =>
        new(_ => Task.FromResult<IHostShellDocker>(_docker), new MemoryCache(new MemoryCacheOptions()), Cleanup,
            backgroundCleanup ?? TimeSpan.FromSeconds(10));

    private async Task<((string Output, string Error, int ExitCode) Result, TimeSpan Took)> Run(
        HostShellOperations? shell = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        var run = (shell ?? Shell()).RunHostShellAsync("echo hello", "srv", timeout ?? Timeout, ct);
        // Fails instead of hanging when a step holds the caller.
        var result = await run.WaitAsync(Bound + TimeSpan.FromSeconds(3));
        return (result, watch.Elapsed);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"never happened: {what}");
            await Task.Delay(25);
        }
    }

    // ---- the run ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_that_nothing_stalls_returns_the_output_and_removes_its_helper()
    {
        var (result, _) = await Run();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.Output);
        Assert.Equal(["has-image", "create", "start", "wait", "logs", "remove"], _docker.RunCalls);
        Assert.Empty(_docker.Containers);
    }

    [Fact]
    public async Task The_helper_carries_its_latest_end_as_a_label()
    {
        _docker.Stall("wait");
        var before = DateTimeOffset.UtcNow;
        var run = Shell().RunHostShellAsync("echo hello", "srv", TimeSpan.FromMinutes(2));
        await WaitUntil(() => _docker.Containers.Count == 1, "the helper was created");

        var label = _docker.Containers.Values.Single().Labels[HostShellOperations.DeadlineLabel];
        var end = DateTimeOffset.FromUnixTimeSeconds(long.Parse(label, CultureInfo.InvariantCulture));
        Assert.InRange(end, before + TimeSpan.FromMinutes(2), before + TimeSpan.FromMinutes(2) + Cleanup + TimeSpan.FromSeconds(5));
        _docker.Release("wait");
        await run;
    }

    [Fact]
    public async Task A_stalled_image_pull_ends_within_the_deadline_and_nothing_is_created()
    {
        _docker.ImageMissing = true;
        _docker.Stall("pull");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("did not start", result.Error);
        Assert.Contains("pull", _docker.RunCalls);
        Assert.DoesNotContain("create", _docker.RunCalls);
    }

    [Fact]
    public async Task A_pull_cut_off_by_the_deadline_is_not_remembered_as_present()
    {
        _docker.ImageMissing = true;
        _docker.Stall("pull");
        var shell = Shell();
        await Run(shell);

        _docker.Release("pull");
        var (second, _) = await Run(shell);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(2, _docker.RunCalls.Count(c => c == "pull"));
    }

    [Fact]
    public async Task A_stalled_image_check_ends_within_the_deadline()
    {
        _docker.Stall("has-image");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("did not start", result.Error);
    }

    [Fact]
    public async Task A_stalled_create_ends_within_the_deadline_and_reports_that_the_command_did_not_start()
    {
        _docker.Stall("create");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("did not start", result.Error);
        Assert.DoesNotContain("start", _docker.RunCalls);
    }

    [Fact]
    public async Task A_create_given_up_on_that_still_makes_the_helper_has_it_removed()
    {
        _docker.Stall("create");
        await Run();

        _docker.Release("create");

        await WaitUntil(() => _docker.Created.Count == 1 && _docker.Containers.IsEmpty, "the late helper was removed");
        Assert.DoesNotContain("start", _docker.RunCalls);
    }

    [Fact]
    public async Task A_stalled_start_ends_within_the_deadline_and_its_effect_is_unknown()
    {
        _docker.Stall("start");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal("Command timed out after 0.5s — its effect is unknown", result.Error);
        Assert.Contains("kill", _docker.RunCalls);
        Assert.Contains("remove", _docker.RunCalls);
    }

    [Fact]
    public async Task A_stalled_command_ends_within_the_deadline_and_its_helper_is_killed_and_removed()
    {
        _docker.Stall("wait");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("Command timed out after 0.5s — its effect is unknown", result.Error);
        Assert.Equal(["has-image", "create", "start", "wait", "kill", "remove"], _docker.RunCalls);
        Assert.Empty(_docker.Containers);
    }

    [Fact]
    public async Task A_stalled_log_retrieval_ends_within_the_deadline_with_the_exit_code_but_no_output()
    {
        _docker.ExitCode = 3;
        _docker.Stall("logs");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains("exit code 3", result.Error);
        Assert.Contains("could not be read", result.Error);
        // The command had finished: nothing is killed, the helper is removed.
        Assert.DoesNotContain("kill", _docker.RunCalls);
        Assert.Contains("remove", _docker.RunCalls);
    }

    [Fact]
    public async Task A_docker_error_after_the_start_request_reads_as_effect_unknown_and_the_helper_is_removed()
    {
        _docker.FailOn = "wait";

        var (result, _) = await Run();

        Assert.Equal(-1, result.ExitCode);
        Assert.StartsWith("Command failed after it was started — its effect is unknown", result.Error);
        Assert.Contains("docker said no", result.Error);
        Assert.Contains("kill", _docker.RunCalls);
        Assert.Empty(_docker.Containers);
    }

    [Fact]
    public async Task A_docker_error_before_the_start_request_still_surfaces_as_an_error()
    {
        _docker.FailOn = "create";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Shell().RunHostShellAsync("x", "srv", Timeout));

        Assert.Equal("docker said no", ex.Message);
        Assert.DoesNotContain("start", _docker.RunCalls);
    }

    // ---- the clean-up ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stalled_clean_up_holds_the_caller_no_longer_than_its_budget()
    {
        _docker.Stall("remove");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.Output);
    }

    [Fact]
    public async Task A_clean_up_the_caller_stopped_waiting_for_goes_on_until_the_helper_is_gone()
    {
        _docker.Stall("wait");
        _docker.Stall("kill");
        _docker.Stall("remove");

        var (result, took) = await Run();
        Assert.True(took < Bound, $"took {took}");
        Assert.Equal("Command timed out after 0.5s — its effect is unknown", result.Error);
        Assert.Single(_docker.Containers);

        _docker.Release("kill");
        _docker.Release("remove");

        await WaitUntil(() => _docker.Containers.IsEmpty, "the privileged helper was removed after the caller had its answer");
    }

    [Fact]
    public async Task A_failing_remove_is_retried_in_the_background()
    {
        _docker.FailRemoveTimes = 2;

        var (result, _) = await Run();

        Assert.Equal(0, result.ExitCode);
        await WaitUntil(() => _docker.Containers.IsEmpty, "the helper was removed on a retry");
    }

    [Fact]
    public async Task The_clean_up_has_its_own_budget_after_a_deadline_that_already_fired()
    {
        _docker.Stall("wait");
        _docker.KillTakes = TimeSpan.FromMilliseconds(100);

        await Run();

        // Kill answered after the run's deadline had fired: its own budget let it finish and the remove follow.
        Assert.Contains("kill", _docker.Completed);
        Assert.Contains("remove", _docker.Completed);
    }

    // ---- the caller ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_caller_that_gives_up_during_the_run_is_told_the_effect_is_unknown()
    {
        _docker.Stall("wait");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var (result, took) = await Run(ct: cts.Token, timeout: TimeSpan.FromMinutes(5));

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal("Command cancelled — its effect is unknown", result.Error);
        Assert.Contains("kill", _docker.RunCalls);
    }

    [Fact]
    public async Task A_caller_that_gives_up_during_setup_is_told_the_command_did_not_start()
    {
        _docker.Stall("create");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var (result, took) = await Run(ct: cts.Token, timeout: TimeSpan.FromMinutes(5));

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal("Command cancelled before it started", result.Error);
    }

    [Fact]
    public async Task A_caller_that_had_already_given_up_is_told_the_command_did_not_start()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (result, _) = await Run(ct: cts.Token);

        Assert.Equal("Command cancelled before it started", result.Error);
        Assert.Empty(_docker.RunCalls);
    }

    [Fact]
    public async Task A_client_that_never_arrives_ends_within_the_deadline()
    {
        var shell = new HostShellOperations(_ => new TaskCompletionSource<IHostShellDocker>().Task,
            new MemoryCache(new MemoryCacheOptions()), Cleanup);

        var (result, took) = await Run(shell);

        Assert.True(took < Bound, $"took {took}");
        Assert.Contains("did not start", result.Error);
    }

    // ---- the sweep -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_removes_old_stopped_helpers_and_running_ones_past_their_deadline_only()
    {
        var now = DateTime.UtcNow;
        string Past(TimeSpan ago) => DateTimeOffset.UtcNow.Subtract(ago).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        _docker.Seed("old-stopped", now.AddMinutes(-2), running: false, deadline: null);
        _docker.Seed("young-stopped", now.AddSeconds(-5), running: false, deadline: null);
        _docker.Seed("running-expired", now.AddMinutes(-10), running: true, deadline: Past(TimeSpan.FromMinutes(5)));
        _docker.Seed("running-in-grace", now.AddMinutes(-1), running: true, deadline: Past(TimeSpan.FromSeconds(10)));
        _docker.Seed("running-unlabelled", now.AddHours(-1), running: true, deadline: null);

        await Run();

        await WaitUntil(() => !_docker.Containers.ContainsKey("old-stopped") && !_docker.Containers.ContainsKey("running-expired"),
            "the sweep removed the leftovers");
        Assert.True(_docker.Containers.ContainsKey("young-stopped"));
        Assert.True(_docker.Containers.ContainsKey("running-in-grace"));
        Assert.True(_docker.Containers.ContainsKey("running-unlabelled"));
    }

    [Theory]
    [InlineData(false, -120, null, true)]   // stopped, old
    [InlineData(false, -5, null, false)]    // stopped, may be in flight
    [InlineData(true, -600, -300, true)]    // running, deadline 5 min ago
    [InlineData(true, -600, -30, false)]    // running, within the grace after its deadline
    [InlineData(true, -600, 60, false)]     // running, deadline still ahead
    [InlineData(true, -3600, null, false)]  // running, no label (older version): left alone
    public void What_the_sweep_counts_as_left_over(bool running, int createdSecondsAgo, int? deadlineInSeconds, bool leftover)
    {
        var now = DateTimeOffset.UtcNow;
        var labels = new Dictionary<string, string>();
        if (deadlineInSeconds is { } d)
            labels[HostShellOperations.DeadlineLabel] = now.AddSeconds(d).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var helper = new HostShellHelper("h", now.AddSeconds(createdSecondsAgo).UtcDateTime, running, labels);

        Assert.Equal(leftover, HostShellOperations.IsLeftover(helper, now));
    }

    [Fact]
    public void A_garbled_deadline_label_never_counts_as_expired()
    {
        var now = DateTimeOffset.UtcNow;
        var helper = new HostShellHelper("h", now.AddHours(-1).UtcDateTime, Running: true,
            new Dictionary<string, string> { [HostShellOperations.DeadlineLabel] = "-1" });

        Assert.False(HostShellOperations.IsLeftover(helper, now));
    }

    // ---- abandoned calls -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_call_given_up_on_that_fails_later_is_observed()
    {
        var late = new TaskCompletionSource();
        var observed = new TaskCompletionSource();
        HostShellOperations.Observed = t => { if (t == late.Task) observed.TrySetResult(); };
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostShellOperations.Within(late.Task, cts.Token));
            late.SetException(new InvalidOperationException("late failure"));

            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(late.Task.IsFaulted);
        }
        finally
        {
            HostShellOperations.Observed = null;
        }
    }

    // ---- local / SSH: a real process -------------------------------------------------------------------------------

    private static HostCommandExecutor Executor() => new(null!, null!, NullLogger<HostCommandExecutor>.Instance);

    [SkippableFact]
    public async Task A_local_command_that_runs_past_its_timeout_is_killed_and_reported_as_effect_unknown()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "ping -n 8 127.0.0.1 >nul" })
            : ("sh", new[] { "-c", "sleep 8" });
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

        var watch = Stopwatch.StartNew();
        var result = await Executor().RunProcessAsync(file, args, TimeSpan.FromSeconds(1), default)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(1.5),
            $"took {watch.Elapsed}");
        Assert.Equal("Command timed out after 1s — its effect is unknown", result.Error);
    }

    [SkippableFact]
    public async Task A_command_that_exits_but_leaves_its_output_open_is_reported_with_its_exit_code_at_the_deadline()
    {
        // The command exits at once; a process it started in the background inherits stdout and keeps it open.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "start /b ping -n 8 127.0.0.1 & echo hi" })
            : ("sh", new[] { "-c", "sleep 8 & echo hi" });
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

        var watch = Stopwatch.StartNew();
        var result = await Executor().RunProcessAsync(file, args, TimeSpan.FromSeconds(1), default)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Equal(-1, result.ExitCode);
        Assert.StartsWith("Command exited with code 0, but a process it left running kept its output open", result.Error);
    }

    // ---- double ----------------------------------------------------------------------------------------------------

    /// <summary>The Docker steps of the host shell over an in-memory set of containers. A stalled step never completes
    /// and ignores its token. Calls made by the run are recorded apart from the sweep's listing.</summary>
    private sealed class FakeDocker : IHostShellDocker
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _stalls = new();
        private readonly ConcurrentQueue<string> _calls = new();
        private readonly ConcurrentQueue<string> _completed = new();
        private int _next;

        public sealed record Container(DateTime Created, bool Running, IReadOnlyDictionary<string, string> Labels);

        public ConcurrentDictionary<string, Container> Containers { get; } = new();
        public ConcurrentQueue<string> Created { get; } = new();
        public bool ImageMissing { get; set; }
        public long ExitCode { get; set; }
        public string? FailOn { get; set; }
        public int FailRemoveTimes;
        public TimeSpan KillTakes { get; set; }

        public IReadOnlyList<string> RunCalls => _calls.ToList();
        public IReadOnlyList<string> Completed => _completed.ToList();

        public void Stall(string step) => _stalls[step] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(string step) { if (_stalls.TryRemove(step, out var gate)) gate.TrySetResult(); }

        public void Seed(string id, DateTime created, bool running, string? deadline)
        {
            var labels = new Dictionary<string, string> { ["serverwatch.hostshell"] = "1" };
            if (deadline is not null) labels[HostShellOperations.DeadlineLabel] = deadline;
            Containers[id] = new Container(created, running, labels);
        }

        private async Task Step(string name, bool record = true)
        {
            if (record) _calls.Enqueue(name);
            if (_stalls.TryGetValue(name, out var gate)) await gate.Task; // ignores any token: the worst case
            if (FailOn == name) throw new InvalidOperationException("docker said no");
            if (name == "kill" && KillTakes > TimeSpan.Zero) await Task.Delay(KillTakes);
            _completed.Enqueue(name);
        }

        public async Task<bool> HasImageAsync(string image, CancellationToken ct) { await Step("has-image"); return !ImageMissing; }
        public async Task PullImageAsync(string repository, string tag, CancellationToken ct) { await Step("pull"); ImageMissing = false; }

        public async Task<string> CreateAsync(CreateContainerParameters parameters, CancellationToken ct)
        {
            await Step("create");
            var id = $"helper{Interlocked.Increment(ref _next)}";
            Containers[id] = new Container(DateTime.UtcNow, false, new Dictionary<string, string>(parameters.Labels));
            Created.Enqueue(id);
            return id;
        }

        public async Task StartAsync(string id, CancellationToken ct)
        {
            await Step("start");
            Containers.AddOrUpdate(id, _ => throw new InvalidOperationException("no such container"), (_, c) => c with { Running = true });
        }

        public async Task<long> WaitAsync(string id, CancellationToken ct)
        {
            await Step("wait");
            if (Containers.TryGetValue(id, out var c)) Containers[id] = c with { Running = false };
            return ExitCode;
        }

        public async Task<(string Output, string Error)> ReadLogsAsync(string id, CancellationToken ct) { await Step("logs"); return ("hello\n", ""); }

        public async Task KillAsync(string id, CancellationToken ct)
        {
            await Step("kill");
            if (Containers.TryGetValue(id, out var c)) Containers[id] = c with { Running = false };
        }

        public async Task RemoveAsync(string id, CancellationToken ct)
        {
            await Step("remove", record: id.StartsWith("helper", StringComparison.Ordinal));
            if (Interlocked.Decrement(ref FailRemoveTimes) >= 0) throw new InvalidOperationException("daemon busy");
            Containers.TryRemove(id, out _);
        }

        public Task<IReadOnlyList<HostShellHelper>> ListHelpersAsync(string label, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HostShellHelper>>(Containers
                .Select(kv => new HostShellHelper(kv.Key, kv.Value.Created, kv.Value.Running, kv.Value.Labels))
                .ToList());
    }
}
