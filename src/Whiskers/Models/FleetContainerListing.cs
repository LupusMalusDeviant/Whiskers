namespace Whiskers.Models;

/// <summary>One server that was asked for its containers and did not answer.</summary>
/// <param name="ServerId">Configured server id.</param>
/// <param name="ServerName">Display name, for notifications and logs.</param>
/// <param name="Error">Why it failed (exception message or timeout).</param>
/// <param name="NotAsked">R1-41: the server was never reached because the call ran out of time in Whiskers' own budget
/// queue (<c>BudgetSaturatedException</c>). Its containers are just as unknown this cycle, but it says nothing about the
/// server, so reachability tracking neither counts nor clears an outage streak for it.</param>
public sealed record FleetServerFailure(string ServerId, string ServerName, string Error, bool NotAsked = false);

/// <summary>
/// A fleet-wide container list together with WHICH servers actually answered.
/// <para>The plain list cannot express the difference between "this host has no containers" and "this
/// host did not answer" — both are an empty result. Callers that keep per-server state across cycles
/// (log watermarks, previous container states, cooldowns) must know the difference: dropping that state
/// for an unreachable host silently re-baselines it on recovery, so everything that happened during the
/// outage is never evaluated. Reachability notifications need the same signal.</para>
/// </summary>
public sealed class FleetContainerListing
{
    public IReadOnlyList<ContainerInfo> Containers { get; init; } = Array.Empty<ContainerInfo>();

    /// <summary>Servers that answered this call (even with zero containers).</summary>
    public IReadOnlySet<string> RespondedServerIds { get; init; } = new HashSet<string>();

    /// <summary>Servers that were asked but failed or timed out.</summary>
    public IReadOnlyList<FleetServerFailure> FailedServers { get; init; } = Array.Empty<FleetServerFailure>();

    /// <summary>True when every asked server answered.</summary>
    public bool IsComplete => FailedServers.Count == 0;

    /// <summary>May per-server state for <paramref name="serverId"/> be dropped from an in-memory map?
    /// Yes for a server that answered (its container list is authoritative) and for one that is no longer
    /// configured (its state is stale); never for a server that failed this cycle.</summary>
    public bool MayPruneStateFor(string serverId) =>
        !FailedServers.Any(f => string.Equals(f.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
}
