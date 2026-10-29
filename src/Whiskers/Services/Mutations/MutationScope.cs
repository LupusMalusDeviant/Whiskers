namespace Whiskers.Services.Mutations;

/// <summary>
/// What a tool reported about its own work inside a gateway scope (R1-20): the record it created, the file it wrote, a
/// failure it caught. It enriches the gateway's one authoritative event instead of becoming a second security event.
/// </summary>
public sealed record MutationDomainResult(string Action, string TargetType, string TargetId, string TargetName,
    string? Details, string? ServerId, bool Success);

/// <summary>
/// The ambient marker the Mutation Gateway sets around an execution delegate (ADR-0005). It flows with the async
/// call chain (<see cref="AsyncLocal{T}"/>), so service calls made inside the delegate — including nested ones such
/// as stop + recreate inside an update — run "inside" the capability that was decided. Work queued with
/// <c>Task.Run</c> inherits it; work started from a hosted-service loop does not.
/// </summary>
public sealed class MutationScope : IDisposable
{
    private const int MaxReasonChars = 500;
    private const int MaxResults = 10;
    private static readonly AsyncLocal<MutationScope?> CurrentScope = new();

    private readonly MutationScope? _parent;
    private bool _disposed;

    private readonly string? _serverId;
    private readonly HashSet<string> _decided;
    private readonly List<MutationDomainResult> _results = [];

    private MutationScope(string capabilityId, string operationId, MutationPrincipal? principal, MutationScope? parent,
        MutationRequest? request)
    {
        CapabilityId = capabilityId;
        OperationId = operationId;
        Principal = principal;
        _parent = parent;
        PinnedPolicy = request?.PinnedPolicy ?? parent?.PinnedPolicy;
        TriggerId = request?.TriggerId ?? parent?.TriggerId;
        _serverId = request?.Target.ServerId;
        // What policy saw for this decision: the resolved target, what it also touches, and the string arguments.
        _decided = new HashSet<string>(StringComparer.Ordinal);
        if (request is not null)
        {
            _decided.Add(request.Target.Id);
            if (request.Target.Name is { } name) _decided.Add(name);
            _decided.UnionWith(request.Target.Related);
            foreach (var value in request.Arguments.Values)
                if (value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is { Length: > 0 } s)
                    _decided.Add(s);
            _decided.Remove("");
        }
    }

    /// <summary>
    /// True when this scope or an enclosing one decided on server <paramref name="serverId"/> about any of
    /// <paramref name="identities"/> (a container's id or name) — as its target, a related resource or an argument
    /// value policy saw (R1-07). Used to check that a plan computed again at execution touches nothing undecided.
    /// </summary>
    public bool Covers(string serverId, params string[] identities)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
            if (scope._serverId == serverId && identities.Any(i => i.Length > 0 && scope._decided.Contains(i)))
                return true;
        return false;
    }

    /// <summary>The innermost open scope on this async flow, or <c>null</c> outside the gateway.</summary>
    public static MutationScope? Current => CurrentScope.Value;

    public string CapabilityId { get; }
    public string OperationId { get; }

    /// <summary>Who the gateway let this mutation run for (an agent with its <c>OnBehalfOf</c>), when known.</summary>
    public MutationPrincipal? Principal { get; }

    /// <summary>The preset the run this scope belongs to is pinned to (INV-23, R1-12), from this scope or an enclosing
    /// one. A gateway call made inside the scope without a pin of its own is decided against it — a scheduled task an
    /// AI trigger runs stays under the trigger's preset.</summary>
    public Whiskers.Models.Agent.PinnedPolicy? PinnedPolicy { get; }

    /// <summary>The AI trigger whose run this scope belongs to (R1-13), from this scope or an enclosing one: a mutation
    /// nested in it is unattended as well, and its audit row names the trigger.</summary>
    public string? TriggerId { get; }

    /// <summary>The failure the delegate reported with <see cref="Fail"/>, or <c>null</c>. The first report wins.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    /// Reports on the current scope that the mutation failed although the code returns normally — for tools that
    /// answer with an error text instead of throwing. The gateway then records the operation as <c>Failed</c> and
    /// still hands the text back to the caller. Returns <paramref name="reason"/>, so a tool writes
    /// <c>return MutationScope.Fail("…");</c>. Outside a scope it only returns the text.
    /// </summary>
    public static string Fail(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        // The text can be a whole command output; the audit row only needs its start. Redacted first: a cut can split
        // a secret from what marks it as one.
        if (Current is { } scope && scope.FailureReason is null)
        {
            var safe = Whiskers.Utils.SecretRedactor.Redact(PolicyDecisions.WithoutUrlCredentials(reason)!);
            scope.FailureReason = safe.Length > MaxReasonChars ? safe[..MaxReasonChars] + "…" : safe;
        }
        return reason;
    }

    /// <summary>What the tool reported with <see cref="Record"/>, oldest first (at most ten).</summary>
    public IReadOnlyList<MutationDomainResult> Results
    {
        get { lock (_results) return _results.ToArray(); }
    }

    /// <summary>
    /// Adds what a tool reports about its work to the current scope, so it becomes part of the gateway's event for
    /// this mutation (R1-20). A result the tool reports as failed also fails the mutation (<see cref="Fail"/>): the
    /// event must not claim success for what the tool says went wrong. False outside a scope — the caller then
    /// writes the report on its own.
    /// </summary>
    public static bool Record(MutationDomainResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Current is not { } scope) return false;
        lock (scope._results)
            if (scope._results.Count < MaxResults) scope._results.Add(result);
        if (!result.Success)
            Fail(string.IsNullOrWhiteSpace(result.Details) ? $"{result.Action} failed." : $"{result.Action} failed: {result.Details}");
        return true;
    }

    /// <summary>
    /// Adds a detail the caller knows about the mutation it runs (R1-21: the template a page deployed, how many
    /// variables it changed) to the gateway's event, as a successful <see cref="MutationDomainResult"/> of the current
    /// capability. Outside a scope it does nothing.
    /// </summary>
    public static void Note(string details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(details);
        if (Current is { } scope)
            Record(new MutationDomainResult(scope.CapabilityId, "", "", "", details, scope._serverId, true));
    }

    /// <summary>Opened by the gateway only. Dispose restores the previous scope.</summary>
    internal static MutationScope Begin(string capabilityId, string operationId, MutationPrincipal? principal = null,
        MutationRequest? request = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var scope = new MutationScope(capabilityId, operationId, principal, CurrentScope.Value, request);
        CurrentScope.Value = scope;
        return scope;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(CurrentScope.Value, this))
            CurrentScope.Value = _parent;
    }
}
