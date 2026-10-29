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

    public async Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(action);

        var args = ToArguments(arguments);
        // An unknown id still goes to the gateway, which refuses it and records the refusal.
        var capability = _catalog.Find(capabilityId);
        var target = capability is null
            ? new MutationTarget("unknown", "")
            : MutationTargets.FromArguments(capability, args);
        var request = new MutationRequest(capabilityId, await PrincipalAsync(), MutationSurface.Ui, target, args);

        // A service that answers with a failure value instead of throwing is recorded as Failed (MutationResults).
        var outcome = await _gateway.ExecuteAsync(request,
            async (_, token) => MutationResults.Report(await action(token)), ct);
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
                    outcome.Status, outcome.OperationId);
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
            await _gateway.RefuseAsync(new MutationRequest(capabilityId, await PrincipalAsync(), MutationSurface.Ui,
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

    /// <summary>The signed-in user. The role decides the level; the page's own role check stays in front of it.</summary>
    private async Task<MutationPrincipal> PrincipalAsync()
    {
        var email = await _user.GetEmailAsync();
        var role = await _user.GetRoleAsync();
        var level = role switch
        {
            AppRole.Admin => McpPermissionLevels.Admin,
            AppRole.Operator => McpPermissionLevels.Write,
            _ => McpPermissionLevels.Read,
        };
        return string.IsNullOrWhiteSpace(email)
            ? new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.Anonymous, "unknown", level)
            : new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(email), email, level);
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
