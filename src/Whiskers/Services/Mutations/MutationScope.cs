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
/// <c>Task.Run</c> inherits it while the operation runs; once the gateway closes the scope, <see cref="Current"/> no
/// longer returns it on any flow (R1-48), so detached work cannot act on the decision afterwards. Work started from a
/// hosted-service loop never inherits it.
/// </summary>
public sealed class MutationScope : IDisposable
{
    private const int MaxReasonChars = 500;
    // What a tool or page reports beyond this is counted, not kept: more would not fit the event's capped details
    // (each entry costs ~130 characters even when its texts shrink). A configuration write's version entries (R1-22,
    // ConfigChange) are always kept: one per written section.
    private const int MaxResults = 20;

    /// <summary>The action of the last entry of <see cref="Results"/> when reports were left out: their count.</summary>
    public const string OmittedAction = "results.omitted";
    private static readonly AsyncLocal<MutationScope?> CurrentScope = new();

    private readonly MutationScope? _parent;
    private volatile bool _disposed;

    private readonly string? _serverId;
    private readonly HashSet<string> _decided;
    private readonly List<MutationDomainResult> _results = [];
    private int _dropped;
    private int _settingsShown;
    private readonly Dictionary<string, string> _readBack = new(StringComparer.Ordinal);

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
            // Contract 0006: the exposures this decision covers — the resolved target's, and the nested decision's
            // arguments (each line one exposure) as policy saw them.
            _exposures.UnionWith(request.Target.Exposures);
            foreach (var key in (string[])["exposure", "exposureWithheld"])
                if (MutationTargets.String(request.Arguments, key) is { } lines)
                    _exposures.UnionWith(lines.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private readonly HashSet<string> _exposures = new(StringComparer.Ordinal);
    private readonly HashSet<(string Server, string Id)> _createdByRun = [];
    private bool _agentRuleSet;
    private IReadOnlyList<string> _exposureConfirmClasses = [];

    /// <summary>Contract 0006: set by the gateway from the policy decision this scope runs under.</summary>
    internal void SetDecision(bool agentRuleSet, IReadOnlyList<string> exposureConfirmClasses)
    {
        _agentRuleSet = agentRuleSet;
        _exposureConfirmClasses = exposureConfirmClasses;
    }

    /// <summary>Contract 0006: this scope or an enclosing one was decided by the agent rule set
    /// (<c>GuardrailEngine.CreateDefault</c>) — the agent, an API key, an AI trigger or an automation that keeps their
    /// rules. Only then does the re-check after <c>up</c> stop an undecided exposure.</summary>
    public bool AgentRuleSet
    {
        get
        {
            for (var s = this; s is not null; s = s._parent)
                if (s._agentRuleSet) return true;
            return false;
        }
    }

    /// <summary>Contract 0006: the exposure classes that needed a confirmation under the agent rule set that decided this
    /// scope or an enclosing one (empty when none did).</summary>
    public IReadOnlySet<string> AgentExposureConfirmClasses
    {
        get
        {
            var classes = new HashSet<string>(StringComparer.Ordinal);
            for (var s = this; s is not null; s = s._parent)
                if (s._agentRuleSet) classes.UnionWith(s._exposureConfirmClasses);
            return classes;
        }
    }

    /// <summary>Contract 0006: whether this scope or an enclosing one decided the exposure <paramref name="identity"/> on
    /// server <paramref name="serverId"/>.</summary>
    public bool CoversExposure(string serverId, string identity)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
            if (scope._serverId == serverId && scope._exposures.Contains(identity))
                return true;
        return false;
    }

    /// <summary>Contract 0006: every exposure this scope and the enclosing ones decided on <paramref name="serverId"/>.</summary>
    public IReadOnlySet<string> DecidedExposures(string serverId)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        for (var scope = this; scope is not null; scope = scope._parent)
            if (scope._serverId == serverId) all.UnionWith(scope._exposures);
        return all;
    }

    /// <summary>
    /// Contract 0006 (Amendments 5, 6 and 10): the containers a compose run created or recreated on
    /// <paramref name="serverId"/>, kept in a set of their own. Only <c>container.disable_restart</c> and
    /// <c>container.stop</c> (through <see cref="AuthorizesTarget"/>) and the re-check's namespace rule
    /// (<see cref="CreatedByRun"/>) read it — <see cref="Covers"/> and <see cref="ComposePlan.EnsureDecided"/> never do.
    /// </summary>
    public void AddCreatedByRun(string serverId, IEnumerable<string> containerIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        lock (_createdByRun)
            foreach (var id in containerIds.Where(i => !string.IsNullOrWhiteSpace(i)))
                _createdByRun.Add((serverId, id));
    }

    /// <summary>Contract 0006: whether a compose run in this scope or an enclosing one created <paramref name="containerId"/>
    /// (a full id, or a unique prefix of one) on <paramref name="serverId"/>.</summary>
    public bool CreatedByRun(string serverId, string containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId)) return false;
        for (var scope = this; scope is not null; scope = scope._parent)
            lock (scope._createdByRun)
                if (scope._createdByRun.Any(c => string.Equals(c.Server, serverId, StringComparison.OrdinalIgnoreCase)
                                                 && c.Id.StartsWith(containerId, StringComparison.Ordinal)))
                    return true;
        return false;
    }

    /// <summary>The operations that may act on a container a compose run created (contract 0006, Amendment 5).</summary>
    internal static readonly IReadOnlySet<string> RunCreatedOperations =
        new HashSet<string>(StringComparer.Ordinal) { "container.disable_restart", "container.stop" };

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

    /// <summary>
    /// The innermost open scope on this async flow, or <c>null</c> outside the gateway. A scope the gateway has closed
    /// is never returned (R1-48): work that escaped it — a <c>Task.Run</c>, a timer or a fire-and-forget started
    /// inside the delegate that is still running once the operation ended — carries the closed scope in its copy of the
    /// async flow, and must not act on that decision. Neither does work under a scope whose enclosing scope was
    /// closed.
    /// </summary>
    public static MutationScope? Current
    {
        get
        {
            var scope = CurrentScope.Value;
            for (var s = scope; s is not null; s = s._parent)
                if (s._disposed) return null;
            return scope;
        }
    }

    /// <summary>True once the gateway closed this scope.</summary>
    public bool IsClosed => _disposed;

    /// <summary>
    /// True when the open scope on this flow, or an open scope enclosing it, decided <paramref name="capabilityId"/> or
    /// a capability that composes it (<see cref="MutationCompositions"/>). Nested decisions (a deployment decided, then
    /// each container it touches) all belong to the same execution, so any of them covers what it decided. False
    /// outside the gateway and after the scope was closed (R1-48).
    /// </summary>
    public static bool Authorizes(string capabilityId) => Authorizes(capabilityId, serverId: null);

    /// <summary>
    /// Like <see cref="Authorizes(string)"/>, for a call that names the server it acts on (R1-48, review m1): a nested
    /// operation that a decision only <em>composes</em> must act on the server that decision was taken for — an
    /// <c>app.deploy</c> on server A does not pull an image on server B. A call of the decided capability itself, a call
    /// that names no server and a decision without a server target are not matched by server (the gateway resolved the
    /// target of the decided call itself; nothing narrower is known). This form treats a missing server as "no server";
    /// the scope guard uses the overload below, which resolves an omitted server argument to the default server.
    /// </summary>
    public static bool Authorizes(string capabilityId, string? serverId) =>
        Authorizes(capabilityId, actsOnServer: !string.IsNullOrEmpty(serverId), serverId, canonical: null);

    /// <summary>
    /// The server-matching form the scope guard uses (R1-48, review round 2). <paramref name="actsOnServer"/> is false
    /// only for an operation that has no server at all; an operation whose server argument was <em>omitted</em> acts on
    /// the transport's default server, and <paramref name="server"/> is then that default's canonical id. A
    /// <c>null</c> <paramref name="server"/> on a server operation means the server could not be resolved: a composed
    /// call then matches no decision taken for a server (fail closed). <paramref name="canonical"/> maps the decision's
    /// server to the same canonical form before the comparison; when it cannot be resolved (<c>null</c>), the decision matches
    /// no composed server call either (review round 3).
    /// </summary>
    public static bool Authorizes(string capabilityId, bool actsOnServer, string? server, Func<string, string?>? canonical)
    {
        for (var s = Current; s is not null; s = s._parent)
        {
            if (string.Equals(s.CapabilityId, capabilityId, StringComparison.Ordinal)) return true;
            if (!MutationCompositions.Allows(s.CapabilityId, capabilityId)) continue;
            if (!actsOnServer || string.IsNullOrEmpty(s._serverId)) return true;
            // Review round 3: an identity that cannot be resolved — the call's or the decision's — matches nothing.
            var decided = canonical is null ? s._serverId : canonical(s._serverId);
            if (server is not null && decided is not null && string.Equals(decided, server, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The target binding of a call (<see cref="MutationTargetAttribute"/>): true when an open scope of this flow that
    /// authorizes <paramref name="capabilityId"/> (it decided it, or a capability that composes it) also decided
    /// <paramref name="identity"/> — as its resolved target, a related resource or an argument value policy saw — on
    /// the server the call acts on. Unlike <see cref="Authorizes(string, bool, string?, Func{string, string?}?)"/>, the
    /// server is matched for the decided capability itself too: a delegate that names the decided container on another
    /// server acts on another container. Servers compare by their canonical id (<paramref name="canonical"/>), or
    /// exactly as named when neither side resolves; anything else fails closed. A scope opened without a request
    /// decided nothing and covers no target.
    /// </summary>
    public static bool AuthorizesTarget(string capabilityId, string? identity, bool actsOnServer, string? namedServer,
        string? server, Func<string, string?>? canonical)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        // Contract 0006 (Amendment 5): the narrow way to a container a compose run created — only for the two operations
        // that stop it, inside a scope that authorizes them, on the server the run created it on.
        if (RunCreatedOperations.Contains(capabilityId) && Current is { } current
            && Authorizes(capabilityId, actsOnServer, server, canonical))
        {
            for (var s = current; s is not null; s = s._parent)
                lock (s._createdByRun)
                    foreach (var (created, id) in s._createdByRun)
                    {
                        if (!string.Equals(id, identity, StringComparison.Ordinal)) continue;
                        var decided = canonical is null ? null : canonical(created);
                        if (!actsOnServer
                            || (server is not null && decided is not null && string.Equals(decided, server, StringComparison.OrdinalIgnoreCase))
                            || (server is null && decided is null && namedServer is not null
                                && string.Equals(namedServer, created, StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
        }
        for (var s = Current; s is not null; s = s._parent)
        {
            if (!string.Equals(s.CapabilityId, capabilityId, StringComparison.Ordinal)
                && !MutationCompositions.Allows(s.CapabilityId, capabilityId))
                continue;
            if (!s._decided.Contains(identity)) continue;
            if (!actsOnServer || string.IsNullOrEmpty(s._serverId)) return true;
            var decided = canonical is null ? null : canonical(s._serverId);
            if (server is not null && decided is not null
                && string.Equals(decided, server, StringComparison.OrdinalIgnoreCase))
                return true;
            // Neither side resolves (no registry): only the very same name is the same server.
            if (server is null && decided is null && namedServer is not null
                && string.Equals(namedServer, s._serverId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when an open, attached scope of this flow decided a capability whose execution is a raw host
    /// command (<see cref="MutationCompositions.RawHostCommands"/>) — the condition for the host executor's primitive.</summary>
    public static bool AuthorizesRawHostCommand()
    {
        for (var s = Current; s is not null; s = s._parent)
            if (MutationCompositions.AllowsRawHostCommand(s.CapabilityId)) return true;
        return false;
    }

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

    /// <summary>The person whose approval this operation used (<c>user:&lt;email&gt;</c>), set by the gateway for this
    /// scope only; <c>null</c> when it ran without one. Contract 0001: that person answers for what a provisioned Git
    /// deployment later runs, so the provisioning tools make them its owner.</summary>
    public string? ApprovedBy { get; internal set; }

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

    /// <summary>What the tool reported with <see cref="Record"/>, oldest first (at most 20, plus the version entries of
    /// configuration writes); reports beyond that are counted in a last entry.</summary>
    public IReadOnlyList<MutationDomainResult> Results
    {
        get
        {
            lock (_results)
                return _dropped == 0
                    ? _results.ToArray()
                    : [.. _results, new MutationDomainResult(OmittedAction, "", "", "",
                        $"{_dropped} further result(s) not recorded", _serverId, true)];
        }
    }

    /// <summary>How many of <paramref name="wanted"/> changed settings a configuration write may still show in this
    /// operation's event, out of <paramref name="max"/> for the whole operation (R1-22).</summary>
    internal int TakeSettingBudget(int wanted, int max)
    {
        lock (_results)
        {
            var granted = Math.Clamp(max - _settingsShown, 0, wanted);
            _settingsShown += granted;
            return granted;
        }
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
            if (scope._results.Count < MaxResults || result.Action == ConfigChange.VersionAction) scope._results.Add(result);
            else scope._dropped++;
        if (!result.Success)
            Fail(string.IsNullOrWhiteSpace(result.Details) ? $"{result.Action} failed." : $"{result.Action} failed: {result.Details}");
        return true;
    }

    /// <summary>
    /// Adds a detail the caller knows about the mutation it runs (R1-21: the template a page deployed, how many
    /// variables it changed) to the gateway's event, as a <see cref="MutationDomainResult"/> of the current capability
    /// whose success is <paramref name="success"/> (the output of a run that failed is not a successful result).
    /// Outside a scope it does nothing.
    /// </summary>
    public static void Note(string details, bool success = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(details);
        if (Current is { } scope)
            Record(new MutationDomainResult(scope.CapabilityId, "", "", "", details, scope._serverId, success));
    }

    /// <summary>
    /// R1-37: tells the capability's verifier what the call really acted on when its arguments do not say it — e.g. the
    /// compose project a deployment ran under after the plan resolved it. In memory only (never audited); the first
    /// value per key wins. Outside a scope it does nothing.
    /// </summary>
    public static void ReadBack(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (Current is { } scope)
            lock (scope._readBack)
                scope._readBack.TryAdd(key, value);
    }

    /// <summary>What the call reported with <see cref="ReadBack"/>.</summary>
    public IReadOnlyDictionary<string, string> ReadBackFacts
    {
        get
        {
            lock (_readBack) return new Dictionary<string, string>(_readBack, StringComparer.Ordinal);
        }
    }

    /// <summary>Opened by the gateway only. Dispose restores the previous scope.</summary>
    internal static MutationScope Begin(string capabilityId, string operationId, MutationPrincipal? principal = null,
        MutationRequest? request = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        // The parent is the scope that is still open: a gateway call made from work that escaped a closed scope starts
        // on its own, and is not nested in (or inheriting the pin or trigger of) a decision that has ended.
        var scope = new MutationScope(capabilityId, operationId, principal, Current, request);
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
