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

    /// <summary>The latest deadline label a sweep accepts (9999-12-31T23:59:59Z); a larger one counts as garbled.</summary>
    private const long MaxLabelSeconds = 253_402_300_799;

    private readonly TimeSpan _cleanupBudget;
    private readonly TimeSpan _backgroundCleanup;
    private readonly TimeSpan _runningGrace;
    private readonly TimeSpan _sweepRetry;

    /// <summary>The first wait before a scheduled sweep that could not reach its server tries again (doubling, up to 5 min).</summary>
    private static readonly TimeSpan DefaultSweepRetry = TimeSpan.FromSeconds(30);
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
        _runningGrace = RunningGrace;
        _sweepRetry = DefaultSweepRetry;
        _time = TimeProvider.System;
    }

    /// <summary>For tests: the Docker steps come from <paramref name="docker"/>; the budgets, the sweep's grace past a
    /// deadline label and the clock can be set.</summary>
    internal HostShellOperations(Func<string?, Task<IHostShellDocker>> docker, MemoryCache statsCache,
        TimeSpan? cleanupBudget = null, TimeSpan? backgroundCleanup = null, TimeProvider? time = null, ILogger? logger = null,
        TimeSpan? runningGrace = null, TimeSpan? sweepRetry = null)
    {
        _docker = docker;
        _statsCache = statsCache;
        _logger = logger ?? NullLogger.Instance;
        _cleanupBudget = cleanupBudget ?? DefaultCleanupBudget;
        _backgroundCleanup = backgroundCleanup ?? DefaultBackgroundCleanup;
        _runningGrace = runningGrace ?? RunningGrace;
        _sweepRetry = sweepRetry ?? DefaultSweepRetry;
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
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        // Fail closed: a run without a finite, positive deadline would hold its caller and its privileged helper
        // without bound. Refused before anything is created.
        if (!ExecutionTimeout.IsUsable(effectiveTimeout))
            throw new ArgumentOutOfRangeException(nameof(timeout), effectiveTimeout,
                "A host-shell run needs a finite, positive timeout.");
        if (ct.IsCancellationRequested)
            return ("", StoppedMessage(Stage.Setup, byCaller: true, TimeSpan.Zero, -1), -1);

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
        var labelEnd = DateTimeOffset.MinValue;
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
                var sweepWith = docker;
                Observe(InBackground(() => SweepHostShellLeftoversAsync(sweepWith)));
                // (a sweep that cannot reach the server here is simply tried again with a later call)
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
            labelEnd = latestEnd;
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
            catch
            {
                // A create given up on — or one that failed, say the link dropped after the daemon had handled it — may
                // still make the helper. It was never started; remove it once its answer arrives — and, should the answer
                // never arrive, a sweep past its deadline label finds it by the label.
                RemoveWhenCreated(docker, create, serverId);
                ScheduleSweep(serverId, latestEnd, docker);
                throw;
            }

            // A deadline that fired in the instant the create answered: the command was not started and is not.
            token.ThrowIfCancellationRequested();

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
                await CleanUpAsync(docker, id, kill: stage == Stage.Running, serverId, labelEnd);
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
    /// — a privileged helper is never simply abandoned. Should that fail too, a sweep is scheduled for when the helper is
    /// past its deadline label, which removes it running or not.</summary>
    private async Task CleanUpAsync(IHostShellDocker docker, string id, bool kill, string? serverId, DateTimeOffset labelEnd)
    {
        var cleanup = InBackground(() => CleanUpUntilDoneAsync(docker, id, kill, serverId, labelEnd));
        try { await cleanup.WaitAsync(_cleanupBudget); }
        catch (TimeoutException)
        {
            Observe(cleanup);
            _logger.LogWarning("Removing host-shell helper {Id} on {ServerId} takes longer than {Budget}s; it goes on in the background",
                id, serverId, _cleanupBudget.TotalSeconds);
        }
    }

    /// <summary>Kills (when asked) and removes the helper, retrying until <see cref="DefaultBackgroundCleanup"/> runs
    /// out. The first attempt uses the client of the run; every retry asks the connection manager for the server's
    /// client again, which is the same cached client unless a reconnect replaced (and disposed) the run's meanwhile.
    /// Where none is to be had (the connection cannot be rebuilt right now) the run's client is tried again. Removing our
    /// own privileged helper is exempt from the emergency stop: the work runs without the caller's context, so it is not
    /// a background call the suspension would refuse — stopping a command is what a pause wants anyway.</summary>
    private async Task CleanUpUntilDoneAsync(IHostShellDocker docker, string id, bool kill, string? serverId, DateTimeOffset labelEnd)
    {
        using var limit = new CancellationTokenSource(_backgroundCleanup);
        var token = limit.Token;
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Each attempt has the clean-up budget at most: one that hangs (a request stuck on a half-open
                // connection) is dropped, and the next one is a new request — through a replaced client, if there is one.
                using var attemptLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                attemptLimit.CancelAfter(_cleanupBudget);
                var step = attemptLimit.Token;
                var client = attempt == 0 ? docker : await FreshOrAsync(docker, serverId, step);
                if (kill)
                {
                    // Not running any more (it exited, or was never started) is the answer kill gives then; the remove
                    // below decides.
                    try { await Within(client.KillAsync(id, step), step); }
                    catch when (!token.IsCancellationRequested) { }
                }
                await Within(client.RemoveAsync(id, step), step);
                return;
            }
            catch when (!token.IsCancellationRequested)
            {
                attempt++;
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10_000, 250 * Math.Pow(2, attempt))), token); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
        }
        _logger.LogWarning("Host-shell helper {Id} on {ServerId} could not be removed within {Limit}; a sweep past its deadline label is scheduled",
            id, serverId, _backgroundCleanup);
        ScheduleSweep(serverId, labelEnd, docker);
    }

    /// <summary>The server's client, got afresh; the given one when none is to be had.</summary>
    private async Task<IHostShellDocker> FreshOrAsync(IHostShellDocker fallback, string? serverId, CancellationToken token)
    {
        try { return await Within(_docker(serverId), token); }
        catch when (!token.IsCancellationRequested) { return fallback; }
    }

    /// <summary>A create the deadline gave up on may still make the helper on the host: remove it when the answer
    /// arrives. (Should it never arrive, the sweep scheduled alongside finds the helper by its label.)</summary>
    private void RemoveWhenCreated(IHostShellDocker docker, Task<string> create, string? serverId) =>
        Observe(InBackground(async () =>
        {
            string id;
            try { id = await create; }
            catch { return; } // no helper to remove; the failure is observed here
            await CleanUpUntilDoneAsync(docker, id, kill: false, serverId, DateTimeOffset.MinValue);
        }));

    /// <summary>Starts background work without the caller's execution context: a clean-up or sweep that outlives the
    /// run must not carry the gateway decision (or any other scope) the run was part of.</summary>
    private static Task InBackground(Func<Task> work)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(work);
        using (ExecutionContext.SuppressFlow())
            return Task.Run(work);
    }

    /// <summary>How long a scheduled sweep keeps trying to reach its server (a link outage) before it leaves the helper to
    /// the sweep of a later host-shell call.</summary>
    private static readonly TimeSpan ScheduledSweepPersistence = TimeSpan.FromHours(1);

    /// <summary>How much later a pending sweep may be due and still cover a new one: the interval of the sweep that runs
    /// with host-shell calls.</summary>
    private static readonly TimeSpan SweepCoverWindow = TimeSpan.FromMinutes(5);

    /// <summary>Per server: when the latest scheduled sweep is due. It covers a new one due no later and at most
    /// <see cref="SweepCoverWindow"/> earlier; any other new one runs at its own time.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _pendingSweeps = new();

    /// <summary>Sweeps the server once its helper with deadline label <paramref name="labelEnd"/> is past that label and
    /// the grace — so a helper whose removal failed, or whose create never answered, is removed without waiting for a
    /// later host-shell call. A label of <see cref="DateTimeOffset.MinValue"/> (no helper of this run to wait for) sweeps
    /// at once. As a rule each failure gets its own sweep at its own due, so a short helper never waits for a long one's
    /// sweep; only a new sweep due no later than the latest pending one and at most <see cref="SweepCoverWindow"/>
    /// before it is left to that one. A sweep that cannot reach the server retries with backoff for up to an hour. In the
    /// background, each try bounded by the sweep budget, never holding a caller.</summary>
    private void ScheduleSweep(string? serverId, DateTimeOffset labelEnd, IHostShellDocker fallback)
    {
        var now = _time.GetUtcNow();
        var at = labelEnd == DateTimeOffset.MinValue ? now : labelEnd + _runningGrace + TimeSpan.FromSeconds(1);
        var key = serverId ?? "local";
        var claimed = false;
        _pendingSweeps.AddOrUpdate(key,
            _ => { claimed = true; return at; },
            (_, pending) =>
            {
                claimed = !(pending >= at && pending <= at + SweepCoverWindow);
                return claimed ? at : pending;
            });
        if (!claimed) return;

        // Task.Delay takes at most uint.MaxValue - 1 ms. A due beyond it (a timeout of about 49 days) waits the maximum;
        // the sweep then finds the helper still within its label and leaves it — accepted: such a helper is removed by
        // the sweep of a later host-shell call on the server once past its label.
        var due = at - now;
        var maxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 2.0);
        due = due < TimeSpan.Zero ? TimeSpan.Zero : due > maxDelay ? maxDelay : due;
        Observe(InBackground(() => SweepLaterAsync(key, serverId, at, due, fallback)));
    }

    private async Task SweepLaterAsync(string key, string? serverId, DateTimeOffset at, TimeSpan due, IHostShellDocker fallback)
    {
        try
        {
            if (due > TimeSpan.Zero) await Task.Delay(due, _time);
            var giveUpAt = _time.GetUtcNow() + ScheduledSweepPersistence;
            var backoff = _sweepRetry;
            while (true)
            {
                using (var budget = new CancellationTokenSource(SweepBudget))
                {
                    try
                    {
                        var docker = await FreshOrAsync(fallback, serverId, budget.Token);
                        if (await SweepHostShellLeftoversAsync(docker, budget.Token)) return;
                    }
                    catch { /* not reached this time */ }
                }
                if (_time.GetUtcNow() + backoff > giveUpAt)
                {
                    _logger.LogWarning("Scheduled sweep of host-shell helpers on {ServerId} could not reach the server for {Hours} h; a later host-shell call there sweeps again",
                        serverId, ScheduledSweepPersistence.TotalHours);
                    return;
                }
                await Task.Delay(backoff, _time);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
            }
        }
        catch { /* best effort; the next host-shell call on the server sweeps again */ }
        finally
        {
            _pendingSweeps.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, at));
        }
    }

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

    /// <summary>Test hook: called with every faulted task a <see cref="Within{T}"/> gave up on, once observed.
    /// Async-local, so a test's hook sees only the calls that test made.</summary>
    internal static Action<Task>? Observed
    {
        get => ObservedHook.Value;
        set => ObservedHook.Value = value;
    }

    private static readonly AsyncLocal<Action<Task>?> ObservedHook = new();

    private static void Observe(Task work)
    {
        var hook = Observed;
        _ = work.ContinueWith(t =>
        {
            _ = t.Exception;
            hook?.Invoke(t);
        }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Best-effort cleanup of host-shell helper containers that leaked from a previous run which was
    /// interrupted before its clean-up (e.g. the app container was rebuilt mid-command, or the
    /// mTLS link dropped, or the background clean-up gave up). Touches only our own labelled containers, judged by
    /// <see cref="IsLeftover"/>. Runs with a host-shell call (at most every 5 min per server) and when scheduled past a
    /// helper's deadline label (<see cref="ScheduleSweep"/>). Bounded by its own budget; it runs in the background and
    /// never holds a caller; a helper it cannot judge or remove does not stop it.
    /// </summary>
    /// <returns>Whether the listing reached the server (removals of single helpers are best effort either way).</returns>
    private async Task<bool> SweepHostShellLeftoversAsync(IHostShellDocker docker, CancellationToken? within = null)
    {
        using var budget = within is null ? new CancellationTokenSource(SweepBudget) : null;
        var token = within ?? budget!.Token;
        try
        {
            var helpers = await Within(docker.ListHelpersAsync(HostShellLabel, token), token);
            var now = _time.GetUtcNow();
            foreach (var helper in helpers)
            {
                try
                {
                    if (!IsLeftover(helper, now, _runningGrace)) continue;
                    if (helper.Running)
                        _logger.LogWarning("Removing host-shell helper {Id}: still running past its deadline", helper.Id);
                    await Within(docker.RemoveAsync(helper.Id, token), token);
                }
                catch when (!token.IsCancellationRequested) { /* best effort; next helper */ }
            }
            return true;
        }
        catch { return false; /* best effort */ }
    }

    /// <summary>Whether the sweep removes <paramref name="helper"/> at <paramref name="now"/>. A helper with a readable
    /// deadline label is judged by it alone, running or stopped: past the label plus <paramref name="grace"/> no run
    /// waits for it any more, and before that one may (a stopped helper whose output is still being read). Without a
    /// readable label (an older version's helper, or a garbled one) a stopped helper goes once older than a short grace,
    /// and a running one is left alone.</summary>
    internal static bool IsLeftover(HostShellHelper helper, DateTimeOffset now, TimeSpan? grace = null)
    {
        if (helper.Labels.TryGetValue(DeadlineLabel, out var raw)
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds <= MaxLabelSeconds)
            return DateTimeOffset.FromUnixTimeSeconds(seconds) < now - (grace ?? RunningGrace);
        return !helper.Running
               && new DateTimeOffset(DateTime.SpecifyKind(helper.Created, DateTimeKind.Utc)) < now - StoppedGrace;
    }
}
