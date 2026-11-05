using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Whiskers.Services.Docker.Operations;

/// <summary>
/// SSH-free host shell execution (one-shot privileged nsenter helper containers) for the
/// <see cref="DockerService"/> facade.
/// </summary>
internal sealed class HostShellOperations
{
    private readonly Func<string?, Task<IHostShellDocker>> _docker;
    private readonly MemoryCache _statsCache;

    private const string HostShellImage = "alpine:latest";
    // Label stamped on every one-shot host-shell helper container so leaked ones (from a run that
    // was interrupted before its finally-remove) can be identified and swept later. The pre-rebrand
    // (ServerWatch) name is kept on purpose: renaming it would hide helpers leaked by older versions from the sweep.
    private const string HostShellLabel = "serverwatch.hostshell";

    /// <summary>How long the clean-up after a run (killing and removing the helper) may take, on top of the run's own
    /// deadline. It has a budget of its own because the deadline may be what ended the run: a caller has its answer at
    /// most this long after its timeout, whatever Docker does.</summary>
    internal static readonly TimeSpan DefaultCleanupBudget = TimeSpan.FromSeconds(5);

    /// <summary>How long the background sweep of leaked helpers may take.</summary>
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _cleanupBudget;

    private readonly IDockerConnectionManager? _connectionManager;

    public HostShellOperations(
        IDockerConnectionManager connectionManager,
        MemoryCache statsCache)
    {
        _connectionManager = connectionManager;
        _docker = async serverId => new DockerClientHostShell(await GetClient(serverId));
        _statsCache = statsCache;
        _cleanupBudget = DefaultCleanupBudget;
    }

    // The direct client use DockerBudgetCoverageTests counts: the helper has its own deadline, outside the load budget.
    private async Task<global::Docker.DotNet.DockerClient> GetClient(string? serverId)
        => await _connectionManager!.GetClientAsync(serverId);

    /// <summary>For tests: the Docker steps come from <paramref name="docker"/>, the clean-up has
    /// <paramref name="cleanupBudget"/>.</summary>
    internal HostShellOperations(Func<string?, Task<IHostShellDocker>> docker, MemoryCache statsCache,
        TimeSpan? cleanupBudget = null)
    {
        _docker = docker;
        _statsCache = statsCache;
        _cleanupBudget = cleanupBudget ?? DefaultCleanupBudget;
    }

    /// <summary>Where a run stood when its deadline or its caller stopped it — this decides what the caller is told.</summary>
    internal enum Stage { Setup, Running, Output }

    /// <summary>
    /// Runs a shell command on the host via a one-shot privileged container that nsenters into the
    /// host namespaces (pid 1). This is the SSH-free shell plane for TCP+mTLS servers: it goes over
    /// the same mTLS Docker connection, so no SSH key is involved. The container is created, started,
    /// waited on, its logs read, and then force-removed.
    ///
    /// <para>R1-57: one deadline (<paramref name="timeout"/>, default 30 s) covers every step from getting the client to
    /// reading the output: the image check or pull, creating, starting and waiting for the helper, and its logs. Every
    /// Docker call is awaited against it, so a call that ignores its token cannot hold the caller either. The clean-up
    /// (kill and remove) has a short budget of its own after that; what it does not finish, the sweep removes later.
    /// The caller therefore has its answer at most timeout + clean-up budget after the call.</para>
    /// </summary>
    public async Task<(string Output, string Error, int ExitCode)> RunHostShellAsync(
        string command, string? serverId = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);

        // R1-38 (INV-40): the caller's cancellation reaches the command itself — the helper is killed, not left to
        // run on after the caller gave up — just as the timeout does. Neither is reported as "it did not run" once the
        // helper may have started. R1-57: the deadline starts here, before the first Docker call.
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

            // A create cut off by the deadline may still have made the helper on the host. It was never started, and
            // it carries the label, so the sweep removes it.
            id = await Within(docker.CreateAsync(new CreateContainerParameters
            {
                Image = HostShellImage,
                Cmd = new List<string> { "nsenter", "-t", "1", "-m", "-u", "-i", "-n", "-p", "--", "sh", "-c", command },
                Labels = new Dictionary<string, string> { [HostShellLabel] = "1" },
                HostConfig = new HostConfig { Privileged = true, PidMode = "host", AutoRemove = false }
            }, token), token);

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
        finally
        {
            if (docker is not null && id is not null)
                await CleanUpAsync(docker, id, kill: stage == Stage.Running);
        }
    }

    /// <summary>What a stopped run tells its caller. Before the start request the command did not run; from it on its
    /// effect is unknown (R1-38); once it finished, its exit code is known but its output is not.</summary>
    internal static string StoppedMessage(Stage stage, bool byCaller, TimeSpan timeout, long exitCode) => stage switch
    {
        Stage.Setup when byCaller => "Command cancelled before it started",
        Stage.Setup => $"Command timed out after {timeout.TotalSeconds}s while its helper container was prepared; it did not start",
        Stage.Running when byCaller => "Command cancelled — its effect is unknown",
        Stage.Running => $"Command timed out after {timeout.TotalSeconds}s",
        _ when byCaller => $"Command finished with exit code {exitCode}, but reading its output was cancelled",
        _ => $"Command finished with exit code {exitCode}, but its output could not be read within {timeout.TotalSeconds}s",
    };

    /// <summary>Kills (when it may still run) and removes the helper within the clean-up budget. A step that does not
    /// answer in time is left behind; the helper carries the label, so the sweep removes it.</summary>
    private async Task CleanUpAsync(IHostShellDocker docker, string id, bool kill)
    {
        using var budget = new CancellationTokenSource(_cleanupBudget);
        try
        {
            if (kill)
            {
                try { await Within(docker.KillAsync(id, budget.Token), budget.Token); }
                catch when (!budget.IsCancellationRequested) { /* already gone or exited; the remove below decides */ }
            }
            await Within(docker.RemoveAsync(id, budget.Token), budget.Token);
        }
        catch { /* best effort; the sweep removes what is left */ }
    }

    /// <summary>Awaits <paramref name="work"/> until <paramref name="token"/> fires, even when the call itself ignores
    /// the token. A call given up on keeps running in the background; its outcome is observed so it cannot surface as an
    /// unobserved exception.</summary>
    internal static async Task<T> Within<T>(Task<T> work, CancellationToken token)
    {
        try { return await work.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !work.IsCompleted)
        {
            Observe(work);
            throw;
        }
    }

    /// <inheritdoc cref="Within{T}(Task{T}, CancellationToken)"/>
    internal static async Task Within(Task work, CancellationToken token)
    {
        try { await work.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !work.IsCompleted)
        {
            Observe(work);
            throw;
        }
    }

    private static void Observe(Task work) =>
        _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// Best-effort cleanup of host-shell helper containers that leaked from a previous run which was
    /// interrupted before its finally-remove (e.g. the app container was rebuilt mid-command, or the
    /// mTLS link dropped, or a clean-up ran out of its budget). Only touches our own labelled, non-running
    /// containers older than a short grace period, so it never races a command that is currently in flight.
    /// Bounded by its own budget; it runs in the background and never holds a caller.
    /// </summary>
    private static async Task SweepHostShellLeftoversAsync(IHostShellDocker docker)
    {
        using var budget = new CancellationTokenSource(SweepBudget);
        var token = budget.Token;
        try
        {
            var list = await Within(docker.ListStoppedAsync(HostShellLabel, token), token);
            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            foreach (var (helperId, created) in list)
            {
                if (created > cutoff) continue; // skip very recent — might be an in-flight command
                try { await Within(docker.RemoveAsync(helperId, token), token); }
                catch when (!token.IsCancellationRequested) { /* best effort; next helper */ }
            }
        }
        catch { /* best effort */ }
    }
}
