using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Whiskers.Services.Docker.Operations;

/// <summary>
/// SSH-free host shell execution (one-shot privileged nsenter helper containers) for the
/// <see cref="DockerService"/> facade.
/// </summary>
internal sealed class HostShellOperations
{
    private readonly IDockerConnectionManager _connectionManager;
    private readonly MemoryCache _statsCache;

    private const string HostShellImage = "alpine:latest";
    // Label stamped on every one-shot host-shell helper container so leaked ones (from a run that
    // was interrupted before its finally-remove) can be identified and swept later.
    private const string HostShellLabel = "serverwatch.hostshell";

    public HostShellOperations(
        IDockerConnectionManager connectionManager,
        MemoryCache statsCache)
    {
        _connectionManager = connectionManager;
        _statsCache = statsCache;
    }

    private async Task<DockerClient> GetClient(string? serverId)
        => await _connectionManager.GetClientAsync(serverId);

    /// <summary>
    /// Runs a shell command on the host via a one-shot privileged container that nsenters into the
    /// host namespaces (pid 1). This is the SSH-free shell plane for TCP+mTLS servers: it goes over
    /// the same mTLS Docker connection, so no SSH key is involved. The container is created, started,
    /// waited on, its logs read, and then force-removed.
    /// </summary>
    public async Task<(string Output, string Error, int ExitCode)> RunHostShellAsync(
        string command, string? serverId = null, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var client = await GetClient(serverId);

        var cacheScope = serverId ?? "local";

        // Opportunistically clear helper containers leaked by a previously interrupted run — but at most
        // once every few minutes per server, not on every command (the leftovers are already dead).
        var sweepKey = $"hostshellsweep:{cacheScope}";
        if (!_statsCache.TryGetValue(sweepKey, out _))
        {
            _statsCache.Set(sweepKey, true, TimeSpan.FromMinutes(5));
            _ = SweepHostShellLeftoversAsync(client);
        }

        // Ensure the helper image exists on the target (pull once if missing). Cache the "present"
        // result per server for an hour so subsequent commands skip the inspect round-trip.
        var imageKey = $"hostshellimg:{cacheScope}";
        if (!_statsCache.TryGetValue(imageKey, out _))
        {
            try
            {
                await client.Images.InspectImageAsync(HostShellImage);
            }
            catch (DockerImageNotFoundException)
            {
                await client.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = "alpine", Tag = "latest" },
                    null, new Progress<JSONMessage>());
            }
            _statsCache.Set(imageKey, true, TimeSpan.FromHours(1));
        }

        var create = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = HostShellImage,
            Cmd = new List<string> { "nsenter", "-t", "1", "-m", "-u", "-i", "-n", "-p", "--", "sh", "-c", command },
            Labels = new Dictionary<string, string> { [HostShellLabel] = "1" },
            HostConfig = new HostConfig { Privileged = true, PidMode = "host", AutoRemove = false }
        });
        var id = create.ID;

        try
        {
            await client.Containers.StartContainerAsync(id, new ContainerStartParameters());

            long exitCode;
            using (var cts = new CancellationTokenSource(effectiveTimeout))
            {
                try
                {
                    var wait = await client.Containers.WaitContainerAsync(id, cts.Token);
                    exitCode = wait.StatusCode;
                }
                catch (OperationCanceledException)
                {
                    try { await client.Containers.KillContainerAsync(id, new ContainerKillParameters()); } catch { /* best effort */ }
                    return ("", $"Command timed out after {effectiveTimeout.TotalSeconds}s", -1);
                }
            }

            using var mux = await client.Containers.GetContainerLogsAsync(id, false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true });
            var stdout = new MemoryStream();
            var stderr = new MemoryStream();
            await mux.CopyOutputToAsync(Stream.Null, stdout, stderr, CancellationToken.None);
            stdout.Position = 0;
            stderr.Position = 0;
            var outStr = await new StreamReader(stdout).ReadToEndAsync();
            var errStr = await new StreamReader(stderr).ReadToEndAsync();

            return (outStr, errStr, (int)exitCode);
        }
        finally
        {
            try { await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }); }
            catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Best-effort cleanup of host-shell helper containers that leaked from a previous run which was
    /// interrupted before its finally-remove (e.g. the app container was rebuilt mid-command, or the
    /// mTLS link dropped). Only touches our own labelled, non-running containers older than a short
    /// grace period, so it never races a command that is currently in flight.
    /// </summary>
    private static async Task SweepHostShellLeftoversAsync(DockerClient client)
    {
        try
        {
            var list = await client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { [$"{HostShellLabel}=1"] = true },
                    ["status"] = new Dictionary<string, bool>
                    {
                        ["created"] = true, ["exited"] = true, ["dead"] = true
                    },
                }
            });
            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            foreach (var c in list)
            {
                if (c.Created > cutoff) continue; // skip very recent — might be an in-flight command
                try { await client.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true }); }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }
}
