using System.Runtime.ExceptionServices;
using System.Text.Json;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;

namespace Whiskers.Services.Mutations;

/// <summary>
/// The UI's entry to the Mutation Gateway (ADR-0005, WP2 step 2.2). A page hands over the capability, the arguments
/// it acts on and the service call; the runner adds the signed-in user as a <see cref="MutationPrincipalKind.Human"/>
/// principal on <see cref="MutationSurface.Ui"/> and runs the call inside the gateway.
/// <para>The call must be a lambda written directly in the <c>RunAsync</c> call: the architecture test recognises a
/// mutation as gated by exactly that shape (capability id as a string literal, then the lambda).</para>
/// </summary>
public interface IUiMutationRunner
{
    /// <summary>
    /// Runs <paramref name="action"/> through the gateway and returns its result.
    /// Contract 0012 (D3): it runs on the object the gateway resolved and confirmed — the page writes its type in the
    /// lambda (<c>(ResolvedContainer c, CancellationToken ct) =&gt; …</c>) and acts on it, never on an id of its own. A <typeparamref name="TTarget"/> that is not the capability's
    /// <see cref="Capability.TargetType"/> (or a base of it) is refused before the gateway is asked, and the refusal is
    /// recorded (INV-45).
    /// </summary>
    /// <exception cref="MutationRefusedException">The gateway refused the call (level, tool switch, kill switch,
    /// protected resource, unknown target, audit not writable). Nothing ran.</exception>
    /// <exception cref="MutationFailedException">The capability's deadline cancelled the token passed to
    /// <paramref name="action"/> and the action stopped on it; its effect is unknown. Also (contract 0023) when the
    /// action went on past its deadline and the grace period (<see cref="MutationFailedException.Abandoned"/>), or a run
    /// nested in it did (<see cref="MutationReasonCodes.NestedAbandoned"/>).</exception>
    /// <remarks>An exception thrown by <paramref name="action"/> itself is rethrown unchanged, so a page's existing
    /// error handling keeps working.
    /// <para>The deadline is cooperative (INV-40): every execution has one, and it cancels the action's token. An
    /// action that ignores the token is waited for at most its deadline plus the grace period (contract 0023); then
    /// this call throws <see cref="MutationFailedException"/> with <see cref="MutationFailedException.Abandoned"/>, while
    /// the action runs on in the background, its target held until it ends, and its late end is audited.</para></remarks>
    [MutationEntryPoint]
    Task<T> RunAsync<TTarget, T>(string capabilityId, object arguments, Func<TTarget, CancellationToken, Task<T>> action,
        CancellationToken ct = default) where TTarget : ResolvedTarget =>
        // A runner that cannot obtain a resolved object (a test double) refuses: only the resolver makes one (D4 a).
        throw new NotSupportedException($"{GetType().Name} cannot run on a resolved target.");

    /// <inheritdoc cref="RunAsync{TTarget, T}"/>
    [MutationEntryPoint]
    Task RunAsync<TTarget>(string capabilityId, object arguments, Func<TTarget, CancellationToken, Task> action,
        CancellationToken ct = default) where TTarget : ResolvedTarget =>
        throw new NotSupportedException($"{GetType().Name} cannot run on a resolved target.");

    /// <summary>
    /// Records that the page refused <paramref name="capabilityId"/> on <paramref name="arguments"/> itself (its role
    /// check, R1-21): one Denied event of the gateway for the signed-in user, with the target the arguments name.
    /// Nothing runs. Never throws, except <see cref="OperationCanceledException"/> when <paramref name="ct"/> is
    /// cancelled — the page shows its own message either way.
    /// </summary>
    Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default);
}

/// <summary>The gateway refused a UI mutation; <see cref="Exception.Message"/> is the reason to show.</summary>
public sealed class MutationRefusedException(string message, MutationStatus status, string operationId)
    : InvalidOperationException(message)
{
    public MutationStatus Status { get; } = status;
    public string OperationId { get; } = operationId;

    /// <summary>The policy rules that decided the refusal (empty when the policy step did not decide it).</summary>
    public IReadOnlyList<string> MatchedRules { get; init; } = [];

    /// <summary>The approval this refusal waits for (PendingApproval only), or the one a dialog left undecided, rejected
    /// or saw expire (contract 0019); null otherwise.</summary>
    public string? ApprovalId { get; init; }
}

/// <summary>A UI mutation did not finish in time (the capability's deadline), or went on past it (contract 0023).</summary>
public sealed class MutationFailedException(string message, Exception? inner)
    : InvalidOperationException(message, inner)
{
    /// <summary>Contract 0023: the action was abandoned after its deadline and the grace period and is still running
    /// (<see cref="MutationReasonCodes.Abandoned"/>). False for a run whose nested run is still running
    /// (<see cref="MutationReasonCodes.NestedAbandoned"/>), as on the outcome.</summary>
    public bool Abandoned { get; init; }

    /// <summary>The outcome's reason code, when it had one (contract 0023: <c>Abandoned</c> or <c>NestedAbandoned</c>).</summary>
    public string? ReasonCode { get; init; }
}

public sealed class UiMutationRunner : IUiMutationRunner
{
    private readonly IMutationGateway _gateway;
    private readonly ICapabilityCatalog _catalog;
    private readonly ICurrentUserService _user;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>? _text;
    private readonly Microsoft.Extensions.Logging.ILogger<UiMutationRunner>? _logger;
    private readonly IInlineApprovalPrompt? _prompt;
    private readonly IApprovalStore? _approvals;

    /// <param name="prompt">Contract 0019: the dialog that shows an INV-59 approval raised by this call (a Blazor
    /// circuit only). Without it every PendingApproval is refused as before.</param>
    /// <param name="approvals">The approval store the dialog reads the raised approval from.</param>
    public UiMutationRunner(IMutationGateway gateway, ICapabilityCatalog catalog, ICurrentUserService user,
        Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>? text = null,
        Microsoft.Extensions.Logging.ILogger<UiMutationRunner>? logger = null,
        IInlineApprovalPrompt? prompt = null, IApprovalStore? approvals = null)
    {
        _prompt = prompt;
        _approvals = approvals;
        _text = text;
        _logger = logger;
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    public Task<T> RunAsync<TTarget, T>(string capabilityId, object arguments,
        Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunCoreAsync<T>(capabilityId, arguments, typeof(TTarget),
            (context, token) => action(ResolvedTargetOf<TTarget>(context), token), ct);
    }

    public Task RunAsync<TTarget>(string capabilityId, object arguments, Func<TTarget, CancellationToken, Task> action,
        CancellationToken ct = default) where TTarget : ResolvedTarget
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunCoreAsync<bool>(capabilityId, arguments, typeof(TTarget), async (context, token) =>
        {
            await action(ResolvedTargetOf<TTarget>(context), token);
            return true;
        }, ct);
    }

    /// <summary>Contract 0012 (D3): the decided object as the lambda's type. The type was checked against the
    /// capability before the gateway was asked; an object missing here (a gateway without a resolver) fails the run
    /// before the lambda acts on anything.</summary>
    internal static TTarget ResolvedTargetOf<TTarget>(MutationContext context) where TTarget : ResolvedTarget =>
        context.Target as TTarget ?? throw new InvalidOperationException(
            $"'{context.Capability.Id}' has no resolved {typeof(TTarget).Name} to act on; nothing ran.");

    /// <summary>Contract 0012 (D3): why <paramref name="targetType"/> cannot receive what <paramref name="capability"/>
    /// resolves to, or <c>null</c> when it can (its declared type or a base of it).</summary>
    internal static string? TargetTypeMismatch(Capability? capability, Type? targetType) =>
        capability is null || targetType is null || targetType.IsAssignableFrom(capability.TargetType)
            ? null
            : $"'{capability.Id}' acts on a {capability.TargetType.Name}, not a {targetType.Name}; nothing ran.";

    private async Task<T> RunCoreAsync<T>(string capabilityId, object arguments, Type? targetType,
        Func<MutationContext, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(arguments);

        var args = ToArguments(arguments);
        // An unknown id still goes to the gateway, which refuses it and records the refusal.
        var capability = _catalog.Find(capabilityId);
        var attempt = await AttemptAsync(capabilityId, args, capability, targetType, action, ct);
        // Contract 0019: an INV-59 approval raised by this very call may be given in the dialog the person is in.
        return attempt.Outcome.Status == MutationStatus.PendingApproval
            ? await ContinueInlineAsync(capabilityId, args, capability, targetType, action, attempt, ct)
            : Settle(attempt.Outcome, ct);
    }

    /// <summary>One gateway call on a request built from scratch: the principal and its session read now (R1-26), the
    /// lambda's target type checked (contract 0012, D3). Throws the refusal of a revoked session or a mismatched type.</summary>
    private async Task<(MutationOutcome<T> Outcome, MutationPrincipal Principal)> AttemptAsync<T>(string capabilityId,
        Dictionary<string, JsonElement> args, Capability? capability, Type? targetType,
        Func<MutationContext, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var target = capability is null
            ? new MutationTarget("unknown", "")
            : MutationTargets.FromArguments(capability, args);
        var (principal, session) = await PrincipalAsync();
        var request = new MutationRequest(capabilityId, principal, MutationSurface.Ui, target,
            new Dictionary<string, JsonElement>(args, StringComparer.Ordinal));

        // R1-26: the session is re-checked for every mutation, never taken from the role the page cached. A revoked
        // session (deleted account, removed from the whitelist, changed stamp, unreadable store) is refused and the
        // refusal recorded by the gateway; nothing runs.
        if (!session.Valid)
        {
            var refused = await _gateway.RefuseAsync(request,
                $"The session is no longer valid ({session.Reason}); sign in again.", ct);
            var text = _text?["Session_Revoked_Refusal"];
            throw new MutationRefusedException(text is { ResourceNotFound: false } ? text.Value
                    : "Your session has ended. Sign in again.", MutationStatus.Denied, refused.OperationId);
        }

        // Contract 0012 (D3): a lambda typed for another kind of object is refused before the gateway is asked.
        if (TargetTypeMismatch(capability, targetType) is { } mismatch)
        {
            var refused = await _gateway.RefuseAsync(request, mismatch, ct);
            throw new MutationRefusedException(mismatch, MutationStatus.Denied, refused.OperationId);
        }

        // A service that answers with a failure value instead of throwing is recorded as Failed (MutationResults).
        var outcome = await _gateway.ExecuteAsync(request,
            async (context, token) => MutationResults.Report(await action(context, token)), ct);
        return (outcome, principal);
    }

    /// <summary>
    /// Contract 0019 (INV-65): a <see cref="MutationStatus.PendingApproval"/> under INV-59 for a capability of the
    /// rollout list, raised by this very call for this very person, is shown in the dialog. Only an approval given
    /// there (or elsewhere, followed by "Run now") continues the call — within this runner call, through the gateway,
    /// on a request rebuilt from scratch. Anything else is today's refusal, now with the approval id. Nothing ever runs
    /// after this call has ended.
    /// </summary>
    private async Task<T> ContinueInlineAsync<T>(string capabilityId, Dictionary<string, JsonElement> args,
        Capability? capability, Type? targetType, Func<MutationContext, CancellationToken, Task<T>> action,
        (MutationOutcome<T> Outcome, MutationPrincipal Principal) first, CancellationToken ct)
    {
        var raised = first.Outcome;
        if (Promptable(capabilityId, raised, first.Principal) is null) return Settle(raised, ct);

        var asked = await AskAsync(new InlineApprovalRequest(raised.ApprovalId!, raised.OperationId, capabilityId,
            PayloadsOf(args)), ct);
        if (asked.Outcome is not (InlineApprovalOutcome.ApprovedHere or InlineApprovalOutcome.ApprovedElsewhereRun))
            throw NotApproved(raised, asked.Outcome);

        // Contract 0019 (D4): the same capability and arguments, the principal and session read again; the gateway
        // continues the original operation through its approve-then-retry path and uses the grant once.
        // INV-65: only while the page and the circuit the decision was made in are still there.
        StillThere(asked, ct);
        var second = await AttemptAsync(capabilityId, args, capability, targetType, action, ct);
        if (second.Outcome.Status != MutationStatus.PendingApproval
            || string.Equals(second.Outcome.ApprovalId, raised.ApprovalId, StringComparison.Ordinal))
            return Settle(second.Outcome, ct);

        // Another approval was raised. Either the grant was used first by another call (nothing drifted) …
        var granted = _approvals?.Get(raised.ApprovalId!);
        if (granted?.ConsumedAt is { } usedAt)
            throw Refusal(second.Outcome, Text("InlineApproval_AlreadyUsed",
                $"Your approval was already used at {usedAt:u} (operation {granted.CorrelationId}); nothing ran here. " +
                $"The new approval {second.Outcome.ApprovalId} is pending on the Approvals page.",
                usedAt.ToLocalTime().ToString("HH:mm:ss"), granted.CorrelationId ?? "?", second.Outcome.ApprovalId ?? "?"));

        // … or the binding changed since the approval (D4): the new approval is shown once, marked as changed.
        var drifted = second.Outcome;
        if (Promptable(capabilityId, drifted, second.Principal) is null) return Settle(drifted, ct);
        var again = await AskAsync(new InlineApprovalRequest(drifted.ApprovalId!, drifted.OperationId, capabilityId,
            PayloadsOf(args)) { ChangedSinceApproval = true }, ct);
        if (again.Outcome is not (InlineApprovalOutcome.ApprovedHere or InlineApprovalOutcome.ApprovedElsewhereRun))
            throw NotApproved(drifted, again.Outcome);
        StillThere(again, ct);
        // A third PendingApproval is today's refusal: there is no loop.
        return Settle((await AttemptAsync(capabilityId, args, capability, targetType, action, ct)).Outcome, ct);
    }

    /// <summary>The approval to show in the dialog, or <c>null</c> when it is not shown (contract 0019, step 2): a
    /// prompt and the store are registered, the capability is on the rollout list with an INV-59 hit, and the approval
    /// read from the store is pending, raised for this operation, by the web UI, with this person as first requester.</summary>
    private Whiskers.Models.Agent.Approval? Promptable<T>(string capabilityId, MutationOutcome<T> outcome,
        MutationPrincipal principal)
    {
        if (_prompt is null || _approvals is null || outcome.ApprovalId is not { } approvalId) return null;
        if (!InlineApproval.Applies(capabilityId, outcome.MatchedRules)) return null;
        Whiskers.Models.Agent.Approval? approval;
        try { approval = _approvals.Get(approvalId); }
        catch (Exception ex)
        {
            _logger?.LogWarning("The approval of {Capability} could not be read for the dialog ({Error})", capabilityId,
                ex.GetType().Name);
            return null;
        }
        return approval is { Status: Whiskers.Models.Agent.ApprovalStatus.Pending }
               && string.Equals(approval.CorrelationId, outcome.OperationId, StringComparison.Ordinal)
               && string.Equals(approval.ActorType, "web", StringComparison.Ordinal)
               && approval.Requesters.Count > 0
               && string.Equals(approval.Requesters[0], principal.Id, StringComparison.Ordinal)
            ? approval
            : null;
    }

    /// <summary>Asks the prompt. A prompt that throws leaves the approval pending (fail closed), logged without
    /// arguments; a cancelled call stays cancelled.</summary>
    private async Task<InlineApprovalResult> AskAsync(InlineApprovalRequest request, CancellationToken ct)
    {
        InlineApprovalResult result;
        try
        {
            result = await _prompt!.AskAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogWarning("The approval dialog of {Capability} failed ({Error}); the approval stays pending",
                request.CapabilityId, ex.GetType().Name);
            result = new InlineApprovalResult(InlineApprovalOutcome.LeftPending);
        }
        // The page, the dialog or the circuit went away: nothing runs.
        ct.ThrowIfCancellationRequested();
        return result ?? new InlineApprovalResult(InlineApprovalOutcome.LeftPending);
    }

    /// <summary>INV-65: a continuation runs only within the same runner call, directly after the decision: the page's
    /// call is not cancelled and the circuit the dialog ran in has not dropped or closed since. Otherwise nothing runs
    /// (the grant stays approved and unused, and lapses).</summary>
    private void StillThere(InlineApprovalResult decided, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (decided.Circuit.IsCancellationRequested)
        {
            _logger?.LogWarning("The circuit of an approval dialog dropped after the decision; nothing ran");
            throw new OperationCanceledException("The circuit dropped after the approval; nothing ran.", decided.Circuit);
        }
    }

    /// <summary>The refusal of an approval the dialog did not approve: left pending, rejected or expired.</summary>
    private MutationRefusedException NotApproved<T>(MutationOutcome<T> outcome, InlineApprovalOutcome result)
    {
        var id = outcome.ApprovalId ?? "?";
        var (status, text) = result switch
        {
            InlineApprovalOutcome.Rejected => (MutationStatus.Denied, Text("InlineApproval_Refused_Rejected",
                $"Approval {id} was rejected by {RejectedBy(id)}; nothing ran.", id, RejectedBy(id))),
            InlineApprovalOutcome.Expired => (MutationStatus.Denied, Text("InlineApproval_Refused_Expired",
                $"Approval {id} expired; nothing ran.", id)),
            // The dialog was gone (closed, its wait ended) while someone approved it: nothing ran, and it is not pending.
            _ when _approvals?.Get(id) is { Status: Whiskers.Models.Agent.ApprovalStatus.Approved } approved =>
                (MutationStatus.PendingApproval, Text("InlineApproval_Refused_ApprovedAfterClose",
                    $"Approval {id} was approved by {approved.ResolvedBy}, but its dialog had closed; nothing ran. Repeat the action within {Whiskers.Services.Approvals.ApprovalStore.GrantLifetime.TotalMinutes:0} minutes to run it once.",
                    id, approved.ResolvedBy ?? "?", Whiskers.Services.Approvals.ApprovalStore.GrantLifetime.TotalMinutes.ToString("0"))),
            _ => (MutationStatus.PendingApproval, Text("InlineApproval_Refused_LeftPending",
                $"Approval {id} was left on the Approvals page; nothing ran. Once it is approved there, run the action again.", id)),
        };
        return new MutationRefusedException(text, status, outcome.OperationId)
        {
            MatchedRules = outcome.MatchedRules,
            ApprovalId = outcome.ApprovalId,
        };
    }

    private string RejectedBy(string approvalId) => _approvals?.Get(approvalId)?.ResolvedBy ?? "?";

    /// <summary>The payload arguments of the runner's own request (contract 0019, wave 2), for the requester's eyes
    /// only: never persisted or logged.</summary>
    private static IReadOnlyDictionary<string, JsonElement>? PayloadsOf(Dictionary<string, JsonElement> args)
    {
        var payloads = args.Where(a => Whiskers.Utils.SinkRedaction.IsPayload(a.Key))
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        return payloads.Count == 0 ? null : payloads;
    }

    private string Text(string key, string fallback, params object[] args)
    {
        var text = _text?[key, args];
        return text is { ResourceNotFound: false } ? text.Value : fallback;
    }

    /// <summary>The result of a gateway call for the page: the value, the action's own exception, or a refusal.</summary>
    private T Settle<T>(MutationOutcome<T> outcome, CancellationToken ct)
    {
        switch (outcome.Status)
        {
            // Contract 0023: a run that went on past its deadline (or whose nested run did) is never read as the
            // action's answer, nor as the page's own cancellation — before every other failed branch.
            case MutationStatus.Failed when MutationReasonCodes.IsAbandoned(outcome.ReasonCode):
                throw new MutationFailedException(LocalizedReason(outcome) ?? outcome.Reason ?? "The action ran past its deadline.",
                    outcome.Error)
                {
                    Abandoned = outcome.Abandoned,
                    ReasonCode = outcome.ReasonCode,
                };
            case MutationStatus.Executed:
                return outcome.Value!;
            case MutationStatus.Failed when outcome.Error is null:
                // A reported failure: the action ran and answered; the page shows its own error from the value.
                return outcome.Value!;
            case MutationStatus.Failed when outcome.Error is { } error and not OperationCanceledException:
                ExceptionDispatchInfo.Capture(error).Throw();
                throw error;   // unreachable
            case MutationStatus.Failed when ct.IsCancellationRequested && outcome.Error is OperationCanceledException oce:
                ExceptionDispatchInfo.Capture(oce).Throw();
                throw oce;     // unreachable
            case MutationStatus.Failed:
                throw new MutationFailedException(outcome.Reason ?? "The action failed.", outcome.Error);
            default:
                throw Refusal(outcome);
        }
    }

    private MutationRefusedException Refusal<T>(MutationOutcome<T> outcome, string? text = null) =>
        new(text ?? LocalizedReason(outcome) ?? outcome.Reason ?? "The action was refused.", outcome.Status,
            outcome.OperationId)
        {
            MatchedRules = outcome.MatchedRules,
            ApprovalId = outcome.ApprovalId,
        };

    public async Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            var args = ToArguments(arguments);
            var capability = _catalog.Find(capabilityId);
            var target = capability is null
                ? new MutationTarget("unknown", "")
                : MutationTargets.FromArguments(capability, args);
            await _gateway.RefuseAsync(new MutationRequest(capabilityId, (await PrincipalAsync()).Principal, MutationSurface.Ui,
                target, args), reason, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The refusal stands whether or not it could be recorded. A failure inside the gateway is logged and kept
            // there (R1-19); one before it (principal, arguments) is logged here — the capability only, never arguments.
            // The exception type only: its message may carry argument values.
            if (_logger is not null)
                Microsoft.Extensions.Logging.LoggerExtensions.LogError(_logger,
                    "A UI refusal of {Capability} could not be recorded ({Error})", capabilityId, ex.GetType().Name);
        }
    }

    /// <summary>The refusal in the user's language when the gateway gave it a code; <c>null</c> otherwise.</summary>
    private string? LocalizedReason<T>(MutationOutcome<T> outcome)
    {
        if (_text is null || outcome.ReasonCode is not { } code) return null;
        var text = _text[MutationReasonCodes.KeyOf(code), outcome.ReasonArgs.Cast<object>().ToArray()];
        return text.ResourceNotFound ? null : text.Value;
    }

    /// <summary>The signed-in user, with the session re-checked now (R1-26). The current role decides the level; the
    /// page's own role check stays in front of it. A revoked session holds <c>read</c>.</summary>
    private async Task<(MutationPrincipal Principal, SessionCheck Session)> PrincipalAsync()
    {
        var email = await _user.GetEmailAsync();
        var session = await _user.CheckSessionAsync();
        var role = session.Valid ? session.Role : AppRole.Viewer;
        var level = CapabilityCatalog.LevelFor(role);
        var principal = string.IsNullOrWhiteSpace(email)
            ? new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.Anonymous, "unknown", level)
            : new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(email), email, level);
        // Contract 0014 (D2): the credential the user signed in with and the web session, beside the account handle.
        var (credential, webSession) = await _user.GetPrincipalFieldsAsync();
        principal = principal with { CredentialId = credential, SessionId = webSession };
        return (principal, session);
    }

    /// <summary>An anonymous object (<c>new { containerId, serverId }</c>) → tool-style arguments. Null values are
    /// left out.</summary>
    internal static Dictionary<string, JsonElement> ToArguments(object arguments)
    {
        if (arguments is IReadOnlyDictionary<string, JsonElement> ready)
            return new Dictionary<string, JsonElement>(ready, StringComparer.Ordinal);
        var element = JsonSerializer.SerializeToElement(arguments, arguments.GetType());
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Arguments must be an object with named properties.", nameof(arguments));
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind != JsonValueKind.Null)
                result[property.Name] = property.Value.Clone();
        return result;
    }
}
