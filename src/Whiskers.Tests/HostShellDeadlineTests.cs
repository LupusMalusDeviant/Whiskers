using System.Collections.Concurrent;
using System.Diagnostics;
using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;
using Whiskers.Services.Docker.Operations;

namespace Whiskers.Tests;

/// <summary>R1-57 prerequisite (INV-40): the SSH-free host shell over the mTLS Docker connection honours one deadline
/// across setup (client, image check and pull, create), the run, the output and the clean-up. Each test stalls one
/// Docker step forever — a step that also ignores its cancellation token, the worst case a client library can produce —
/// and proves the caller still has its answer within the deadline plus the clean-up budget, told what matches where the
/// run stood.</summary>
public sealed class HostShellDeadlineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Cleanup = TimeSpan.FromMilliseconds(500);
    /// <summary>Far below "forever", far above timeout + clean-up: a stalled step that held the caller would cross it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private readonly FakeDocker _docker = new();

    private HostShellOperations Shell(FakeDocker? docker = null) =>
        new(_ => Task.FromResult<IHostShellDocker>(docker ?? _docker), new MemoryCache(new MemoryCacheOptions()), Cleanup);

    private async Task<((string Output, string Error, int ExitCode) Result, TimeSpan Took)> Run(
        HostShellOperations? shell = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        var result = await (shell ?? Shell()).RunHostShellAsync("echo hello", "srv", timeout ?? Timeout, ct);
        return (result, watch.Elapsed);
    }

    [Fact]
    public async Task A_run_that_nothing_stalls_returns_the_output_and_removes_its_helper()
    {
        var (result, _) = await Run();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.Output);
        Assert.Equal(["has-image", "create", "start", "wait", "logs", "remove"], _docker.Calls);
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
        Assert.Contains("pull", _docker.Calls);
        Assert.DoesNotContain("create", _docker.Calls);
    }

    [Fact]
    public async Task A_pull_cut_off_by_the_deadline_is_not_remembered_as_present()
    {
        _docker.ImageMissing = true;
        _docker.Stall("pull");
        var shell = Shell();
        await shell.RunHostShellAsync("echo hello", "srv", Timeout);

        _docker.Release("pull");
        var second = await shell.RunHostShellAsync("echo hello", "srv", Timeout);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(2, _docker.Calls.Count(c => c == "pull"));
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
        Assert.DoesNotContain("start", _docker.Calls);
    }

    [Fact]
    public async Task A_stalled_start_ends_within_the_deadline_and_its_effect_is_unknown()
    {
        _docker.Stall("start");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.StartsWith("Command timed out after", result.Error);
        Assert.DoesNotContain("did not start", result.Error);
        Assert.Contains("kill", _docker.Calls);
        Assert.Contains("remove", _docker.Calls);
    }

    [Fact]
    public async Task A_stalled_command_ends_within_the_deadline_and_its_helper_is_killed_and_removed()
    {
        _docker.Stall("wait");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal(-1, result.ExitCode);
        Assert.StartsWith("Command timed out after", result.Error);
        Assert.Equal(["has-image", "create", "start", "wait", "kill", "remove"], _docker.Calls);
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
        Assert.DoesNotContain("kill", _docker.Calls);
        Assert.Contains("remove", _docker.Calls);
    }

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
    public async Task A_stalled_kill_and_remove_after_a_timeout_still_end_within_the_deadline_plus_the_clean_up_budget()
    {
        _docker.Stall("wait");
        _docker.Stall("kill");
        _docker.Stall("remove");

        var (result, took) = await Run();

        Assert.True(took < Bound, $"took {took}");
        Assert.StartsWith("Command timed out after", result.Error);
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
    public async Task A_caller_that_gives_up_during_the_run_is_told_the_effect_is_unknown()
    {
        _docker.Stall("wait");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var (result, took) = await Run(ct: cts.Token, timeout: TimeSpan.FromMinutes(5));

        Assert.True(took < Bound, $"took {took}");
        Assert.Equal("Command cancelled — its effect is unknown", result.Error);
        Assert.Contains("kill", _docker.Calls);
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
    public async Task A_client_that_never_arrives_ends_within_the_deadline()
    {
        var shell = new HostShellOperations(_ => new TaskCompletionSource<IHostShellDocker>().Task,
            new MemoryCache(new MemoryCacheOptions()), Cleanup);

        var (result, took) = await Run(shell);

        Assert.True(took < Bound, $"took {took}");
        Assert.Contains("did not start", result.Error);
    }

    [Fact]
    public async Task A_docker_error_that_is_not_the_deadline_still_surfaces_and_the_helper_is_removed()
    {
        _docker.FailOn = "wait";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Shell().RunHostShellAsync("x", "srv", Timeout));

        Assert.Equal("docker said no", ex.Message);
        Assert.Contains("kill", _docker.Calls);
        Assert.Contains("remove", _docker.Calls);
    }

    [Fact]
    public async Task A_call_given_up_on_that_fails_later_is_observed()
    {
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception.InnerExceptions.Any(x => x.Message == "late failure")) Interlocked.Increment(ref unobserved);
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await GiveUpOnACallThatFailsLater();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Equal(0, unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    // Its own method, so nothing on the test's stack keeps the abandoned task alive for the collection above.
    private static async Task GiveUpOnACallThatFailsLater()
    {
        var late = new TaskCompletionSource();
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostShellOperations.Within(late.Task, cts.Token));
        late.SetException(new InvalidOperationException("late failure"));
    }

    /// <summary>The Docker steps of the host shell. A stalled step never completes and ignores its token.</summary>
    private sealed class FakeDocker : IHostShellDocker
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _stalls = new();
        private readonly ConcurrentQueue<string> _calls = new();
        private readonly ConcurrentQueue<string> _completed = new();

        public bool ImageMissing { get; set; }
        public long ExitCode { get; set; }
        public string? FailOn { get; set; }
        public TimeSpan KillTakes { get; set; }

        public IReadOnlyList<string> Calls => _calls.ToList();
        public IReadOnlyList<string> Completed => _completed.ToList();

        public void Stall(string step) => _stalls[step] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(string step) { if (_stalls.TryRemove(step, out var gate)) gate.TrySetResult(); }

        private async Task Step(string name)
        {
            _calls.Enqueue(name);
            if (_stalls.TryGetValue(name, out var gate)) await gate.Task; // ignores any token: the worst case
            if (FailOn == name) throw new InvalidOperationException("docker said no");
            if (name == "kill" && KillTakes > TimeSpan.Zero) await Task.Delay(KillTakes);
            _completed.Enqueue(name);
        }

        public async Task<bool> HasImageAsync(string image, CancellationToken ct) { await Step("has-image"); return !ImageMissing; }
        public async Task PullImageAsync(string repository, string tag, CancellationToken ct) { await Step("pull"); ImageMissing = false; }
        public async Task<string> CreateAsync(CreateContainerParameters parameters, CancellationToken ct) { await Step("create"); return "helper1"; }
        public Task StartAsync(string id, CancellationToken ct) => Step("start");
        public async Task<long> WaitAsync(string id, CancellationToken ct) { await Step("wait"); return ExitCode; }
        public async Task<(string Output, string Error)> ReadLogsAsync(string id, CancellationToken ct) { await Step("logs"); return ("hello\n", ""); }
        public Task KillAsync(string id, CancellationToken ct) => Step("kill");
        public Task RemoveAsync(string id, CancellationToken ct) => Step("remove");
        public Task<IReadOnlyList<(string Id, DateTime Created)>> ListStoppedAsync(string label, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(string Id, DateTime Created)>>([]);
    }
}
