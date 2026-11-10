using System.Runtime.ExceptionServices;
using System.Text.Json;
using Whiskers.Models;
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
    /// <summary>Runs <paramref name="action"/> through the gateway and returns its result.</summary>
    /// <exception cref="MutationRefusedException">The gateway refused the call (level, tool switch, kill switch,
    /// protected resource, unknown target, audit not writable). Nothing ran.</exception>
    /// <exception cref="MutationFailedException">The call ran past its deadline.</exception>
    /// <remarks>An exception thrown by <paramref name="action"/> itself is rethrown unchanged, so a page's existing
    /// error handling keeps working.</remarks>
    [MutationEntryPoint]
    Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default);

    /// <inheritdoc cref="RunAsync{T}"/>
    [MutationEntryPoint]
    Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
        CancellationToken ct = default);

    /// <summary>
    /// Contract 0012 (D3): runs <paramref name="action"/> on the object the gateway resolved and confirmed — the page
    /// writes its type in the lambda (<c>(ResolvedContainer c, CancellationToken ct) =&gt; …</c>) and acts on it, never
    /// on an id of its own. A <typeparamref name="TTarget"/> that is not the capability's
    /// <see cref="Capability.TargetType"/> (or a base of it) is refused before the gateway is asked, and the refusal is
    /// recorded (INV-45).
    /// </summary>
    /// <inheritdoc cref="RunAsync{T}" path="/exception"/>
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
}

/// <summary>A UI mutation did not finish in time (the capability's deadline).</summary>
public sealed class MutationFailedException(string message, Exception? inner)
    : InvalidOperationException(message, inner);

public sealed class UiMutationRunner : IUiMutationRunner
{
    private readonly IMutationGateway _gateway;
    private readonly ICapabilityCatalog _catalog;
    private readonly ICurrentUserService _user;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>? _text;
    private readonly Microsoft.Extensions.Logging.ILogger<UiMutationRunner>? _logger;

    public UiMutationRunner(IMutationGateway gateway, ICapabilityCatalog catalog, ICurrentUserService user,
        Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>? text = null,
        Microsoft.Extensions.Logging.ILogger<UiMutationRunner>? logger = null)
    {
        _text = text;
        _logger = logger;
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunCoreAsync<T>(capabilityId, arguments, null, (_, token) => action(token), ct);
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
        var target = capability is null
            ? new MutationTarget("unknown", "")
            : MutationTargets.FromArguments(capability, args);
        var (principal, session) = await PrincipalAsync();
        var request = new MutationRequest(capabilityId, principal, MutationSurface.Ui, target, args);

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
        switch (outcome.Status)
        {
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
                throw new MutationRefusedException(LocalizedReason(outcome) ?? outcome.Reason ?? "The action was refused.",
                    outcome.Status, outcome.OperationId) { MatchedRules = outcome.MatchedRules };
        }
    }

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

    public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync<bool>(capabilityId, arguments, async token =>
        {
            await action(token);
            return true;
        }, ct);
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
