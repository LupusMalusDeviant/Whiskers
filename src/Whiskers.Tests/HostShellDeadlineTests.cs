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

    [Fact]
    public async Task A_clean_up_whose_client_was_disposed_gets_the_server_client_afresh()
    {
        // The run's client is disposed after the run (a reconnect replaced it): its kill and remove throw. The next
        // client the server hands out works.
        var disposed = new DisposedAfterRun(_docker);
        var handedOut = 0;
        var shell = new HostShellOperations(
            _ => Task.FromResult<IHostShellDocker>(Interlocked.Increment(ref handedOut) == 1 ? disposed : _docker),
            new MemoryCache(new MemoryCacheOptions()), Cleanup, TimeSpan.FromSeconds(10));
        _docker.Stall("wait");

        var (result, _) = await Run(shell);

        Assert.Equal("Command timed out after 0.5s — its effect is unknown", result.Error);
        await WaitUntil(() => _docker.Containers.IsEmpty, "the helper was removed through a fresh client");
        Assert.True(handedOut >= 2, "a fresh client was asked for");
    }

    [Fact]
    public async Task A_clean_up_that_gives_up_schedules_a_sweep_past_the_deadline_label()
    {
        // The only remove of the clean-up fails and its background time runs out before the retry; nothing else
        // would remove the helper before a later host-shell call on the server.
        _docker.FailRemoveTimes = 1;
        var shell = new HostShellOperations(_ => Task.FromResult<IHostShellDocker>(_docker),
            new MemoryCache(new MemoryCacheOptions()), Cleanup, backgroundCleanup: TimeSpan.FromMilliseconds(100),
            runningGrace: TimeSpan.Zero);

        var (result, _) = await Run(shell);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(_docker.Containers);
        await WaitUntil(() => _docker.Containers.IsEmpty, "the scheduled sweep removed the helper past its label");
    }

    [Fact]
    public async Task A_create_that_never_answers_but_made_the_helper_is_swept_past_its_deadline_label()
    {
        _docker.CreateHangsAfterMaking = true;
        var shell = new HostShellOperations(_ => Task.FromResult<IHostShellDocker>(_docker),
            new MemoryCache(new MemoryCacheOptions()), Cleanup, runningGrace: TimeSpan.Zero);

        var (result, took) = await Run(shell);

        Assert.True(took < Bound, $"took {took}");
        Assert.Contains("did not start", result.Error);
        Assert.Single(_docker.Containers);
        await WaitUntil(() => _docker.Containers.IsEmpty, "the scheduled sweep found the helper by its label");
    }

    [Fact]
    public async Task A_clean_up_attempt_that_hangs_is_dropped_and_retried_through_a_fresh_client()
    {
        // A half-open connection: the run's client never answers the kill. The next client the server hands out works.
        var hanging = new KillHangs(_docker);
        var handedOut = 0;
        var shell = new HostShellOperations(
            _ => Task.FromResult<IHostShellDocker>(Interlocked.Increment(ref handedOut) == 1 ? hanging : _docker),
            new MemoryCache(new MemoryCacheOptions()), Cleanup, TimeSpan.FromSeconds(60));
        _docker.Stall("wait");

        await Run(shell);

        // Well before the 60 s background window: the hanging attempt was dropped after its budget.
        await WaitUntil(() => _docker.Containers.IsEmpty, "the helper was removed by a retry through a fresh client");
    }

    [Fact]
    public async Task A_docker_error_while_reading_the_output_reports_the_exit_code_and_removes_the_helper()
    {
        _docker.ExitCode = 3;
        _docker.FailOn = "logs";

        var (result, _) = await Run();

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("Command finished with exit code 3, but its output could not be read (InvalidOperationException: docker said no)",
            result.Error);
        await WaitUntil(() => _docker.Containers.IsEmpty, "the helper was removed");
    }

    [Fact]
    public async Task A_docker_error_from_the_start_request_reads_as_effect_unknown_and_the_helper_is_killed_and_removed()
    {
        _docker.FailOn = "start";

        var (result, _) = await Run();

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("Command failed after it was started — its effect is unknown (InvalidOperationException: docker said no)",
            result.Error);
        Assert.Contains("kill", _docker.RunCalls);
        await WaitUntil(() => _docker.Containers.IsEmpty, "the helper was removed");
    }

    [Fact]
    public async Task A_deadline_that_fires_as_the_create_answers_does_not_start_the_command()
    {
        using var cts = new CancellationTokenSource();
        _docker.CancelOnCreate = cts;

        var (result, _) = await Run(ct: cts.Token, timeout: TimeSpan.FromMinutes(5));

        Assert.Equal("Command cancelled before it started", result.Error);
        Assert.DoesNotContain("start", _docker.RunCalls);
        Assert.DoesNotContain("kill", _docker.RunCalls);
        await WaitUntil(() => _docker.Containers.IsEmpty, "the never-started helper was removed");
    }

    [Fact]
    public async Task A_create_that_fails_but_made_the_helper_is_swept_past_its_deadline_label()
    {
        _docker.CreateFailsAfterMaking = true;
        var shell = new HostShellOperations(_ => Task.FromResult<IHostShellDocker>(_docker),
            new MemoryCache(new MemoryCacheOptions()), Cleanup, runningGrace: TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(shell));

        Assert.Single(_docker.Containers);
        await WaitUntil(() => _docker.Containers.IsEmpty, "the scheduled sweep found the helper by its label");
    }

    [Fact]
    public async Task The_executor_hands_the_host_shell_result_and_its_errors_through_unchanged()
    {
        var server = new Whiskers.Models.ServerConfig { Id = "edge", Name = "edge", ConnectionType = Whiskers.Models.ConnectionType.TCP };
        var docker = new global::Whiskers.Tests.FakeDocker();
        var executor = new HostCommandExecutor(new FakeServerConfig(server), docker, NullLogger<HostCommandExecutor>.Instance);

        docker.HostShell = (_, _, _, _) => Task.FromResult(("out", "err", 7));
        var ran = await executor.ExecuteAsync("edge", "true");
        Assert.Equal((7, "out", "err"), (ran.ExitCode, ran.Output, ran.Error));

        docker.HostShell = (_, _, _, _) => Task.FromResult(("", "Command timed out after 1s — its effect is unknown", -1));
        var stopped = await executor.ExecuteAsync("edge", "true");
        Assert.Equal((-1, "Command timed out after 1s — its effect is unknown"), (stopped.ExitCode, stopped.Error));

        // A Docker error before the start request: the command did not run, the error says why.
        docker.HostShell = (_, _, _, _) => throw new InvalidOperationException("daemon unreachable");
        var failed = await executor.ExecuteAsync("edge", "true");
        Assert.Equal((-1, "daemon unreachable"), (failed.ExitCode, failed.Error));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]     // Timeout.InfiniteTimeSpan: no deadline at all
    [InlineData("-5000")]
    [InlineData("max")]    // beyond what CancelAfter accepts
    public async Task A_local_or_ssh_command_without_a_finite_positive_timeout_is_refused_before_a_process_starts(string milliseconds)
    {
        var timeout = milliseconds == "max" ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(int.Parse(milliseconds, CultureInfo.InvariantCulture));
        var (file, args) = OperatingSystem.IsWindows() ? ("cmd", new[] { "/c", "echo hi" }) : ("sh", new[] { "-c", "echo hi" });

        // Refused by a throw before Process.Start — a started process would have produced a result instead.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Executor().RunProcessAsync(file, args, timeout, default));

        var local = new Whiskers.Models.ServerConfig { Id = "here", Name = "here", ConnectionType = Whiskers.Models.ConnectionType.Local };
        var executor = new HostCommandExecutor(new FakeServerConfig(local), new global::Whiskers.Tests.FakeDocker(),
            NullLogger<HostCommandExecutor>.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.ExecuteAsync("here", "echo hi", timeout));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]   // Timeout.InfiniteTimeSpan
    [InlineData(-5000)]
    public async Task A_run_without_a_finite_positive_timeout_is_refused_before_anything_is_created(int milliseconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Shell().RunHostShellAsync("echo hello", "srv", TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Empty(_docker.RunCalls);
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
    [InlineData(false, -120, 60, false)]    // stopped, label ahead: its run may still read the output
    [InlineData(false, -120, -30, false)]   // stopped, within the grace after its label
    [InlineData(false, -120, -300, true)]   // stopped, well past its label
    [InlineData(false, -5, -300, true)]     // stopped and young, but past its label: the label decides
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

    [Theory]
    [InlineData("253402300800", true, false)]          // one second past year 9999: garbled, running → left alone
    [InlineData("9223372036854775807", true, false)]  // long.MaxValue: no overflow, left alone
    [InlineData("253402300800", false, true)]         // garbled on an old stopped helper: the 30 s rule applies
    public void A_deadline_label_out_of_range_counts_as_garbled(string label, bool running, bool leftover)
    {
        var now = DateTimeOffset.UtcNow;
        var helper = new HostShellHelper("h", now.AddHours(-1).UtcDateTime, running,
            new Dictionary<string, string> { [HostShellOperations.DeadlineLabel] = label });

        Assert.Equal(leftover, HostShellOperations.IsLeftover(helper, now));
    }

    [Fact]
    public async Task A_helper_with_a_bad_label_does_not_stop_the_sweep()
    {
        var now = DateTime.UtcNow;
        _docker.Seed("bad-label", now.AddHours(-1), running: true, deadline: "99999999999999999999");
        _docker.Seed("old-stopped", now.AddMinutes(-2), running: false, deadline: null);

        await Run();

        await WaitUntil(() => !_docker.Containers.ContainsKey("old-stopped"), "the sweep went past the bad label");
        Assert.True(_docker.Containers.ContainsKey("bad-label"));
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

    /// <summary>A client on a half-open connection: its kill never answers (and ignores its token).</summary>
    private sealed class KillHangs(FakeDocker inner) : IHostShellDocker
    {
        public Task<bool> HasImageAsync(string image, CancellationToken ct) => inner.HasImageAsync(image, ct);
        public Task PullImageAsync(string repository, string tag, CancellationToken ct) => inner.PullImageAsync(repository, tag, ct);
        public Task<string> CreateAsync(CreateContainerParameters parameters, CancellationToken ct) => inner.CreateAsync(parameters, ct);
        public Task StartAsync(string id, CancellationToken ct) => inner.StartAsync(id, ct);
        public Task<long> WaitAsync(string id, CancellationToken ct) => inner.WaitAsync(id, ct);
        public Task<(string Output, string Error)> ReadLogsAsync(string id, CancellationToken ct) => inner.ReadLogsAsync(id, ct);
        public Task KillAsync(string id, CancellationToken ct) => new TaskCompletionSource().Task;
        public Task RemoveAsync(string id, CancellationToken ct) => new TaskCompletionSource().Task;
        public Task<IReadOnlyList<HostShellHelper>> ListHelpersAsync(string label, CancellationToken ct) => inner.ListHelpersAsync(label, ct);
    }

    /// <summary>A client the connection manager disposed after the run: the run's own steps go through, its kill and
    /// remove throw <see cref="ObjectDisposedException"/>.</summary>
    private sealed class DisposedAfterRun(FakeDocker inner) : IHostShellDocker
    {
        public Task<bool> HasImageAsync(string image, CancellationToken ct) => inner.HasImageAsync(image, ct);
        public Task PullImageAsync(string repository, string tag, CancellationToken ct) => inner.PullImageAsync(repository, tag, ct);
        public Task<string> CreateAsync(CreateContainerParameters parameters, CancellationToken ct) => inner.CreateAsync(parameters, ct);
        public Task StartAsync(string id, CancellationToken ct) => inner.StartAsync(id, ct);
        public Task<long> WaitAsync(string id, CancellationToken ct) => inner.WaitAsync(id, ct);
        public Task<(string Output, string Error)> ReadLogsAsync(string id, CancellationToken ct) => inner.ReadLogsAsync(id, ct);
        public Task KillAsync(string id, CancellationToken ct) => throw new ObjectDisposedException("DockerClient");
        public Task RemoveAsync(string id, CancellationToken ct) => throw new ObjectDisposedException("DockerClient");
        public Task<IReadOnlyList<HostShellHelper>> ListHelpersAsync(string label, CancellationToken ct) => inner.ListHelpersAsync(label, ct);
    }

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

        /// <summary>Create makes the helper on the "host", then its answer never arrives.</summary>
        public bool CreateHangsAfterMaking { get; set; }

        /// <summary>Create makes the helper on the "host", then fails (the link dropped after the daemon handled it).</summary>
        public bool CreateFailsAfterMaking { get; set; }

        /// <summary>Create cancels this source as it answers, already completed: the deadline fires the instant the
        /// create's answer is there.</summary>
        public CancellationTokenSource? CancelOnCreate { get; set; }

        public Task<string> CreateAsync(CreateContainerParameters parameters, CancellationToken ct)
        {
            if (CancelOnCreate is { } cancel)
            {
                _calls.Enqueue("create");
                var made = $"helper{Interlocked.Increment(ref _next)}";
                Containers[made] = new Container(DateTime.UtcNow, false, new Dictionary<string, string>(parameters.Labels));
                cancel.Cancel();
                return Task.FromResult(made);
            }
            return CreateSlowlyAsync(parameters);
        }

        private async Task<string> CreateSlowlyAsync(CreateContainerParameters parameters)
        {
            await Step("create");
            var id = $"helper{Interlocked.Increment(ref _next)}";
            Containers[id] = new Container(DateTime.UtcNow, false, new Dictionary<string, string>(parameters.Labels));
            Created.Enqueue(id);
            if (CreateHangsAfterMaking) await new TaskCompletionSource().Task;
            if (CreateFailsAfterMaking) throw new InvalidOperationException("connection reset");
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
