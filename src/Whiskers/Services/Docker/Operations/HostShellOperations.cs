using System.Globalization;
using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Utils;

namespace Whiskers.Services.Docker.Operations;

/// <summary>
/// SSH-free host shell execution (one-shot privileged nsenter helper containers) for the
/// <see cref="DockerService"/> facade.
/// </summary>
internal sealed class HostShellOperations
{
    private readonly Func<string?, Task<IHostShellDocker>> _docker;
    private readonly MemoryCache _statsCache;
    private readonly ILogger _logger;

    private const string HostShellImage = "alpine:latest";
    // Label stamped on every one-shot host-shell helper container so leaked ones (from a run that
    // was interrupted before its finally-remove) can be identified and swept later. The pre-rebrand
    // (ServerWatch) name is kept on purpose: renaming it would hide helpers leaked by older versions from the sweep.
    private const string HostShellLabel = "serverwatch.hostshell";

    /// <summary>R1-57: the moment (Unix seconds) after which a helper has no business running any more — its run's
    /// deadline plus the clean-up budget. The sweep force-removes a helper still running past it (plus
    /// <see cref="RunningGrace"/>); a helper without the label (from an older version) is left alone while it runs.</summary>
    internal const string DeadlineLabel = "serverwatch.hostshell.deadline";

    /// <summary>How long the clean-up after a run (killing and removing the helper) may hold the caller, on top of the
    /// run's own deadline. It has a budget of its own because the deadline may be what ended the run.</summary>
    internal static readonly TimeSpan DefaultCleanupBudget = TimeSpan.FromSeconds(5);

    /// <summary>How long a clean-up the caller stopped waiting for keeps trying in the background — it never gives up on
    /// a privileged helper after a few seconds.</summary>
    internal static readonly TimeSpan DefaultBackgroundCleanup = TimeSpan.FromMinutes(2);

    /// <summary>How long the background sweep of leaked helpers may take.</summary>
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(30);

    /// <summary>A stopped helper younger than this may belong to a run in flight; the sweep leaves it.</summary>
    private static readonly TimeSpan StoppedGrace = TimeSpan.FromSeconds(30);

    /// <summary>How far past its deadline label a running helper must be before the sweep removes it.</summary>
    internal static readonly TimeSpan RunningGrace = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _cleanupBudget;
    private readonly TimeSpan _backgroundCleanup;
    private readonly TimeProvider _time;
    private readonly IDockerConnectionManager? _connectionManager;

    public HostShellOperations(
        IDockerConnectionManager connectionManager,
        MemoryCache statsCache,
        ILogger? logger = null)
    {
        _connectionManager = connectionManager;
        _docker = async serverId => new DockerClientHostShell(await GetClient(serverId));
        _statsCache = statsCache;
        _logger = logger ?? NullLogger.Instance;
        _cleanupBudget = DefaultCleanupBudget;
        _backgroundCleanup = DefaultBackgroundCleanup;
        _time = TimeProvider.System;
    }

    /// <summary>For tests: the Docker steps come from <paramref name="docker"/>; the budgets and the clock can be set.</summary>
    internal HostShellOperations(Func<string?, Task<IHostShellDocker>> docker, MemoryCache statsCache,
        TimeSpan? cleanupBudget = null, TimeSpan? backgroundCleanup = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _docker = docker;
        _statsCache = statsCache;
        _logger = logger ?? NullLogger.Instance;
        _cleanupBudget = cleanupBudget ?? DefaultCleanupBudget;
        _backgroundCleanup = backgroundCleanup ?? DefaultBackgroundCleanup;
        _time = time ?? TimeProvider.System;
    }

    // The direct client use DockerBudgetCoverageTests counts: the helper has its own deadline, outside the load budget.
    private async Task<global::Docker.DotNet.DockerClient> GetClient(string? serverId)
        => await _connectionManager!.GetClientAsync(serverId);

    /// <summary>Where a run stood when its deadline, its caller or an error stopped it — this decides what the caller
    /// is told.</summary>
    internal enum Stage { Setup, Running, Output }

    /// <summary>
    /// Runs a shell command on the host via a one-shot privileged container that nsenters into the
    /// host namespaces (pid 1). This is the SSH-free shell plane for TCP+mTLS servers: it goes over
    /// the same mTLS Docker connection, so no SSH key is involved. The container is created, started,
    /// waited on, its logs read, and then force-removed.
    ///
    /// <para>R1-57: one deadline (<paramref name="timeout"/>, default 30 s) covers every step from getting the client to
    /// reading the output: the image check or pull, creating, starting and waiting for the helper, and its logs. Every
    /// Docker call is awaited against it, so a call that ignores its token cannot hold the caller either. Killing and
    /// removing the helper hold the caller for a short budget of their own after that (5 s); what they have not finished
    /// by then goes on in the background (up to 2 min), and a helper still running past its deadline label is removed by
    /// the sweep. The caller therefore has its answer at most timeout + 5 s after the call.</para>
    ///
    /// <para>The answer says where the run stood: a stop or a Docker error before the start request means the command did
    /// not run (a Docker error is thrown, as before); from the start request on, its effect is unknown (R1-38); once it
    /// exited, the exit code is known but the output may not be — that result still carries exit code -1, with the real
    /// code in the error text, since a caller cannot act on output it did not get.</para>
    /// </summary>
    public async Task<(string Output, string Error, int ExitCode)> RunHostShellAsync(
        string command, string? serverId = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return ("", StoppedMessage(Stage.Setup, byCaller: true, TimeSpan.Zero, -1), -1);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);

        // R1-38 (INV-40): the caller's cancellation reaches the command itself — the helper is killed, not left to
        // run on after the caller gave up — just as the timeout does. R1-57: the deadline starts here, before the first
        // Docker call.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(effectiveTimeout);
        var token = deadline.Token;

        IHostShellDocker? docker = null;
        string? id = null;
        var stage = Stage.Setup;
        long exitCode = -1;
        try
        {
            docker = await Within(_docker(serverId), token);

            var cacheScope = serverId ?? "local";

            // Opportunistically clear helper containers leaked by a previously interrupted run — but at most
            // once every few minutes per server, not on every command (the leftovers are already dead).
            var sweepKey = $"hostshellsweep:{cacheScope}";
            if (!_statsCache.TryGetValue(sweepKey, out _))
            {
                _statsCache.Set(sweepKey, true, TimeSpan.FromMinutes(5));
                _ = SweepHostShellLeftoversAsync(docker);
            }

            // Ensure the helper image exists on the target (pull once if missing). Cache the "present"
            // result per server for an hour so subsequent commands skip the inspect round-trip. A check or pull cut
            // off by the deadline caches nothing.
            var imageKey = $"hostshellimg:{cacheScope}";
            if (!_statsCache.TryGetValue(imageKey, out _))
            {
                if (!await Within(docker.HasImageAsync(HostShellImage, token), token))
                    await Within(docker.PullImageAsync("alpine", "latest", token), token);
                _statsCache.Set(imageKey, true, TimeSpan.FromHours(1));
            }

            var latestEnd = _time.GetUtcNow() + effectiveTimeout + _cleanupBudget;
            var create = docker.CreateAsync(new CreateContainerParameters
            {
                Image = HostShellImage,
                Cmd = new List<string> { "nsenter", "-t", "1", "-m", "-u", "-i", "-n", "-p", "--", "sh", "-c", command },
                Labels = new Dictionary<string, string>
                {
                    [HostShellLabel] = "1",
                    // Rounded up: the label must never name a moment before the run's real latest end.
                    [DeadlineLabel] = ((latestEnd.ToUnixTimeMilliseconds() + 999) / 1000).ToString(CultureInfo.InvariantCulture),
                },
                HostConfig = new HostConfig { Privileged = true, PidMode = "host", AutoRemove = false }
            }, token);
            try
            {
                id = await Within(create, token);
            }
            catch when (deadline.IsCancellationRequested)
            {
                // A create given up on may still make the helper. It was never started; remove it once it shows up.
                RemoveWhenCreated(docker, create, serverId);
                throw;
            }

            // From the start request on, the command may run: a stop is no longer "it did not start".
            stage = Stage.Running;
            await Within(docker.StartAsync(id, token), token);
            exitCode = await Within(docker.WaitAsync(id, token), token);

            stage = Stage.Output;
            var (output, error) = await Within(docker.ReadLogsAsync(id, token), token);
            return (output, error, (int)exitCode);
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            // Stopped by the deadline or by the caller — whatever exception the Docker call surfaced for it (a
            // cancellation, or an I/O error from the aborted connection).
            return ("", StoppedMessage(stage, ct.IsCancellationRequested, effectiveTimeout, exitCode), -1);
        }
        catch (Exception ex) when (stage != Stage.Setup)
        {
            // A Docker error after the start request (the mTLS link dropped, the daemon answered an error): the command
            // may have run. It is reported as such, never as a plain failure that reads like "nothing happened".
            _logger.LogWarning("Host shell on {ServerId} failed after the start request: {Error}", serverId,
                SecretRedactor.Redact(ex.Message));
            return ("", FailedMessage(stage, exitCode, ex), -1);
        }
        finally
        {
            if (docker is not null && id is not null)
                await CleanUpAsync(docker, id, kill: stage == Stage.Running, serverId);
        }
    }

    /// <summary>What a stopped run tells its caller. Before the start request the command did not run; from it on its
    /// effect is unknown (R1-38); once it exited, its exit code is known but its output is not.</summary>
    internal static string StoppedMessage(Stage stage, bool byCaller, TimeSpan timeout, long exitCode) => stage switch
    {
        Stage.Setup when byCaller => "Command cancelled before it started",
        Stage.Setup => $"Command timed out after {Seconds(timeout)}s while its helper container was prepared; it did not start",
        Stage.Running when byCaller => "Command cancelled — its effect is unknown",
        Stage.Running => $"Command timed out after {Seconds(timeout)}s — its effect is unknown",
        _ when byCaller => $"Command finished with exit code {exitCode}, but reading its output was cancelled",
        _ => $"Command finished with exit code {exitCode}, but its output could not be read within {Seconds(timeout)}s",
    };

    /// <summary>Seconds as the messages show them: invariant ("0.5", never "0,5"), so the text reads the same on every host.</summary>
    private static string Seconds(TimeSpan timeout) => timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a run that a Docker error (not the deadline) stopped after its start request tells its caller.</summary>
    private static string FailedMessage(Stage stage, long exitCode, Exception ex)
    {
        var cause = SecretRedactor.Redact($"{ex.GetType().Name}: {ex.Message}");
        return stage == Stage.Running
            ? $"Command failed after it was started — its effect is unknown ({cause})"
            : $"Command finished with exit code {exitCode}, but its output could not be read ({cause})";
    }

    /// <summary>Kills (when it may still run) and removes the helper. The caller waits for the clean-up budget at most;
    /// whatever has not finished by then goes on in the background, retried up to <see cref="DefaultBackgroundCleanup"/>
    /// — a privileged helper is never simply abandoned. Should that fail too, the helper's deadline label lets the sweep
    /// remove it, running or not.</summary>
    private async Task CleanUpAsync(IHostShellDocker docker, string id, bool kill, string? serverId)
    {
        var cleanup = CleanUpUntilDoneAsync(docker, id, kill, serverId);
        try { await cleanup.WaitAsync(_cleanupBudget); }
        catch (TimeoutException)
        {
            _logger.LogWarning("Removing host-shell helper {Id} on {ServerId} takes longer than {Budget}s; it goes on in the background",
                id, serverId, _cleanupBudget.TotalSeconds);
        }
    }

    private async Task CleanUpUntilDoneAsync(IHostShellDocker docker, string id, bool kill, string? serverId)
    {
        using var limit = new CancellationTokenSource(_backgroundCleanup);
        var token = limit.Token;
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (kill)
                {
                    // Not running any more (it exited, or was never started) is the answer kill gives then; the remove
                    // below decides.
                    try { await Within(docker.KillAsync(id, token), token); }
                    catch when (!token.IsCancellationRequested) { }
                }
                await Within(docker.RemoveAsync(id, token), token);
                return;
            }
            catch when (!token.IsCancellationRequested)
            {
                attempt++;
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10_000, 250 * Math.Pow(2, attempt))), token); }
                catch (OperationCanceledException) { break; }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        _logger.LogWarning("Host-shell helper {Id} on {ServerId} could not be removed within {Limit}; the sweep removes it once it is past its deadline label",
            id, serverId, _backgroundCleanup);
    }

    /// <summary>A create the deadline gave up on may still make the helper on the host: remove it when it does.</summary>
    private void RemoveWhenCreated(IHostShellDocker docker, Task<string> create, string? serverId) =>
        _ = create.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                _ = CleanUpUntilDoneAsync(docker, t.Result, kill: false, serverId);
            else
                _ = t.Exception; // observed
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>Awaits <paramref name="work"/> until <paramref name="token"/> fires, even when the call itself ignores
    /// the token. A call given up on keeps running in the background; its outcome is observed so it cannot surface as an
    /// unobserved exception — also when it finished in the instant the token fired.</summary>
    internal static async Task<T> Within<T>(Task<T> work, CancellationToken token)
    {
        try { return await work.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Observe(work);
            throw;
        }
    }

    /// <inheritdoc cref="Within{T}(Task{T}, CancellationToken)"/>
    internal static async Task Within(Task work, CancellationToken token)
    {
        try { await work.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Observe(work);
            throw;
        }
    }

    /// <summary>Test hook: called with every faulted task a <see cref="Within{T}"/> gave up on, once observed.</summary>
    internal static Action<Task>? Observed;

    private static void Observe(Task work) =>
        _ = work.ContinueWith(static t =>
        {
            _ = t.Exception;
            Observed?.Invoke(t);
        }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// Best-effort cleanup of host-shell helper containers that leaked from a previous run which was
    /// interrupted before its clean-up (e.g. the app container was rebuilt mid-command, or the
    /// mTLS link dropped, or the background clean-up gave up). Touches only our own labelled containers:
    /// a stopped one older than a short grace period, so it never races a command in flight, and — R1-57 — a running
    /// one past its deadline label plus a grace period, which no run is waiting for any more. A running helper without
    /// the label (an older version's) is left alone. Bounded by its own budget; it runs in the background and never
    /// holds a caller.
    /// </summary>
    private async Task SweepHostShellLeftoversAsync(IHostShellDocker docker)
    {
        using var budget = new CancellationTokenSource(SweepBudget);
        var token = budget.Token;
        try
        {
            var helpers = await Within(docker.ListHelpersAsync(HostShellLabel, token), token);
            var now = _time.GetUtcNow();
            foreach (var helper in helpers)
            {
                if (!IsLeftover(helper, now)) continue;
                if (helper.Running)
                    _logger.LogWarning("Removing host-shell helper {Id}: still running past its deadline", helper.Id);
                try { await Within(docker.RemoveAsync(helper.Id, token), token); }
                catch when (!token.IsCancellationRequested) { /* best effort; next helper */ }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Whether the sweep removes <paramref name="helper"/> at <paramref name="now"/>.</summary>
    internal static bool IsLeftover(HostShellHelper helper, DateTimeOffset now)
    {
        if (!helper.Running)
            return new DateTimeOffset(DateTime.SpecifyKind(helper.Created, DateTimeKind.Utc)) < now - StoppedGrace;
        return helper.Labels.TryGetValue(DeadlineLabel, out var raw)
               && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
               && DateTimeOffset.FromUnixTimeSeconds(seconds) + RunningGrace < now;
    }
}
