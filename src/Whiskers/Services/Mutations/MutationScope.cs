namespace Whiskers.Services.Mutations;

/// <summary>
/// The ambient marker the Mutation Gateway sets around an execution delegate (ADR-0005). It flows with the async
/// call chain (<see cref="AsyncLocal{T}"/>), so service calls made inside the delegate — including nested ones such
/// as stop + recreate inside an update — run "inside" the capability that was decided. Work queued with
/// <c>Task.Run</c> inherits it; work started from a hosted-service loop does not.
/// </summary>
public sealed class MutationScope : IDisposable
{
    private static readonly AsyncLocal<MutationScope?> CurrentScope = new();

    private readonly MutationScope? _parent;
    private bool _disposed;

    private MutationScope(string capabilityId, string operationId, MutationScope? parent)
    {
        CapabilityId = capabilityId;
        OperationId = operationId;
        _parent = parent;
    }

    /// <summary>The innermost open scope on this async flow, or <c>null</c> outside the gateway.</summary>
    public static MutationScope? Current => CurrentScope.Value;

    public string CapabilityId { get; }
    public string OperationId { get; }

    /// <summary>Opened by the gateway only. Dispose restores the previous scope.</summary>
    internal static MutationScope Begin(string capabilityId, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var scope = new MutationScope(capabilityId, operationId, CurrentScope.Value);
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
