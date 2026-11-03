namespace Whiskers.Services.Mutations;

/// <summary>
/// Marks a service-interface method that changes infrastructure, and names the capability it performs
/// (<c>&lt;area&gt;.&lt;verb&gt;</c>, ADR-0005). Two checks read it: the runtime scope guard refuses a call that runs
/// outside a Mutation Gateway scope that decided this capability (or one that composes it,
/// <see cref="MutationCompositions"/>), and <c>MutationBypassArchitectureTests</c> refuses every caller outside such a
/// context. Put it on the <b>interface</b> method; implementations inherit nothing.
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
/// Marks a raw execution primitive (R1-48): a method that runs whatever the caller hands it — a host shell command —
/// so it has no capability of its own. It runs only inside an open Mutation Gateway scope, of whatever capability was
/// decided; outside one the scope guard refuses it. Reads that use the same transport go through an unmarked method,
/// and <c>MutationGatewayCoverageTests</c> lists every caller of both.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class MutationPrimitiveAttribute : Attribute
{
}

/// <summary>
/// Marks a helper class whose methods run only as part of one capability's execution (R1-48): the class is the
/// execution of <see cref="CapabilityId"/>, and the operations it calls must be that capability or one it composes
/// (<see cref="MutationCompositions"/>). It is not an exemption: <c>MutationBypassArchitectureTests</c> checks every
/// call in the class against the capability, and at run time the scope guard checks the open scope.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MutationHandlerAttribute : Attribute
{
    public MutationHandlerAttribute(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        CapabilityId = capabilityId;
    }

    public string CapabilityId { get; }
}

/// <summary>
/// Marks a runner method that takes a capability id and an action and runs the action inside the Mutation Gateway
/// (<see cref="IUiMutationRunner"/>, <see cref="IAutomationMutationRunner"/>). <c>MutationBypassArchitectureTests</c>
/// treats a lambda handed to such a method, next to a capability id literal, as gated for that capability.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class MutationEntryPointAttribute : Attribute
{
}
