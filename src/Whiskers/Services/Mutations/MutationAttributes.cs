namespace Whiskers.Services.Mutations;

/// <summary>
/// Marks a service-interface method that changes infrastructure, and names the capability it performs
/// (<c>&lt;area&gt;.&lt;verb&gt;</c>, ADR-0005). Two checks read it: the runtime scope guard reports a call that runs
/// outside a Mutation Gateway scope, and <c>MutationBypassArchitectureTests</c> lists every caller outside a
/// <see cref="MutationHandlerAttribute"/> type. Put it on the <b>interface</b> method; implementations inherit nothing.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class MutatingOperationAttribute : Attribute
{
    public MutatingOperationAttribute(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        CapabilityId = capabilityId;
    }

    public string CapabilityId { get; }
}

/// <summary>
/// Marks a class whose methods are the execution delegates the Mutation Gateway runs. Only these classes (and the
/// gateway itself) may call a <see cref="MutatingOperationAttribute"/> method; everything else goes through
/// <see cref="IMutationGateway"/>. Enforced by <c>MutationBypassArchitectureTests</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MutationHandlerAttribute : Attribute
{
}
