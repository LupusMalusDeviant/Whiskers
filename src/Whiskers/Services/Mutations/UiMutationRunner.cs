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
    Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default);

    /// <inheritdoc cref="RunAsync{T}"/>
    Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
        CancellationToken ct = default);
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

    public UiMutationRunner(IMutationGateway gateway, ICapabilityCatalog catalog, ICurrentUserService user)
    {
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

        var outcome = await _gateway.ExecuteAsync(request, (_, token) => action(token), ct);
        switch (outcome.Status)
        {
            case MutationStatus.Executed:
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
                throw new MutationRefusedException(outcome.Reason ?? "The action was refused.", outcome.Status,
                    outcome.OperationId);
        }
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
        var id = string.IsNullOrWhiteSpace(email) ? "unknown" : email;
        return new MutationPrincipal(MutationPrincipalKind.Human, id, id, level);
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
