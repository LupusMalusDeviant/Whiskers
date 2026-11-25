namespace Whiskers.Services.Mutations;

/// <summary>Contract 0023: a run the gateway answered without, because it was still running after its deadline and the
/// grace period. It stays here until it really ends.</summary>
/// <param name="CapabilityId">The capability it runs.</param>
/// <param name="OperationId">Its operation id.</param>
/// <param name="Target">The target as the gateway resolved it.</param>
/// <param name="Since">When it was abandoned.</param>
/// <param name="StartedAt">When its execution started.</param>
public sealed record AbandonedRun(string CapabilityId, string OperationId, MutationTarget Target, DateTimeOffset Since,
    DateTimeOffset StartedAt);

/// <summary>
/// Contract 0023: the abandoned runs that are still running (point 5) and the target guard of point 4b. A singleton:
/// the gauge <c>whiskers_mutations_abandoned_running</c>, the <c>/self-status</c> line, the readiness check and the
/// gateway's <see cref="MutationReasonCodes.TargetBusy"/> refusal all read it.
/// </summary>
public interface IAbandonedRuns
{
    /// <summary>What is running past its deadline right now, oldest first.</summary>
    IReadOnlyList<AbandonedRun> Running { get; }

    /// <summary>The abandoned run whose target overlaps <paramref name="target"/> (point 4b), skipping the runs whose
    /// operation id is in <paramref name="nestedIn"/> — the open scopes the asking call runs inside — or <c>null</c>.</summary>
    AbandonedRun? Overlapping(MutationTarget target, IReadOnlyCollection<string> nestedIn);

    /// <summary>Adds <paramref name="run"/>; disposing the handle removes it again, once.</summary>
    IDisposable Add(AbandonedRun run);
}

/// <inheritdoc cref="IAbandonedRuns"/>
public sealed class AbandonedRuns : IAbandonedRuns
{
    private readonly object _gate = new();
    private readonly Dictionary<long, AbandonedRun> _runs = new();
    private long _next;

    public IReadOnlyList<AbandonedRun> Running
    {
        get
        {
            lock (_gate) return _runs.OrderBy(r => r.Key).Select(r => r.Value).ToList();
        }
    }

    public AbandonedRun? Overlapping(MutationTarget target, IReadOnlyCollection<string> nestedIn)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(nestedIn);
        lock (_gate)
            return _runs.OrderBy(r => r.Key).Select(r => r.Value)
                .FirstOrDefault(run => !nestedIn.Contains(run.OperationId) && Overlap(run.Target, target));
    }

    public IDisposable Add(AbandonedRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            var id = ++_next;
            _runs[id] = run;
            return new Entry(this, id);
        }
    }

    private void Remove(long id)
    {
        lock (_gate) _runs.Remove(id);
    }

    /// <summary>The kinds whose target is a whole server: such a target overlaps every target on that server (4b).</summary>
    internal static readonly IReadOnlySet<string> ServerKinds =
        new HashSet<string>(StringComparer.Ordinal) { "host", "server" };

    /// <summary>
    /// Contract 0023 (4b): whether two resolved targets overlap — same kind, id and server; one's related resources hold
    /// the other (on the same server); or one of them is a host-level or server-level target on the other's server.
    /// </summary>
    internal static bool Overlap(MutationTarget a, MutationTarget b)
    {
        var serverA = ServerOf(a);
        var serverB = ServerOf(b);
        var sameServer = string.Equals(serverA, serverB, StringComparison.OrdinalIgnoreCase);

        if (string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
            && string.Equals(a.Id, b.Id, StringComparison.Ordinal) && sameServer)
            return true;

        if ((ServerKinds.Contains(a.Kind) || ServerKinds.Contains(b.Kind)) && serverA is not null && sameServer)
            return true;

        return sameServer && (Holds(a, b) || Holds(b, a));
    }

    /// <summary>The server a target sits on: its server id, or its own id for a host-level or server-level target.</summary>
    private static string? ServerOf(MutationTarget t) =>
        !string.IsNullOrEmpty(t.ServerId) ? t.ServerId
        : ServerKinds.Contains(t.Kind) && !string.IsNullOrEmpty(t.Id) ? t.Id
        : null;

    /// <summary>Whether <paramref name="outer"/>'s related resources name <paramref name="inner"/> (its id or name).</summary>
    private static bool Holds(MutationTarget outer, MutationTarget inner)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { inner.Id };
        if (inner.Name is { Length: > 0 } name) names.Add(name);
        names.Remove("");
        if (names.Count == 0) return false;
        if (outer.Related.Any(names.Contains)) return true;
        if (outer.RelatedContainerId is { } related && names.Contains(related)) return true;
        return outer.RelatedResources.Any(resource => resource.Any(names.Contains));
    }

    private sealed class Entry(AbandonedRuns runs, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) runs.Remove(id);
        }
    }
}

/// <summary>Contract 0023: the gateway's grace period and the readiness limit.</summary>
public sealed class MutationGatewayOptions
{
    /// <summary>G (D1): how long after its deadline the gateway still waits for an action before it answers without it.</summary>
    public TimeSpan AbandonGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>L (D5): how long an abandoned run may go on before readiness turns <c>Degraded</c>.</summary>
    public TimeSpan AbandonedLimit { get; set; } = TimeSpan.FromMinutes(15);
}
