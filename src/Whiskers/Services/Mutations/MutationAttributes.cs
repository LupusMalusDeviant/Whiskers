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
/// so it has no capability of its own. It runs only inside an open Mutation Gateway scope whose capability's execution
/// is a raw host command (<see cref="MutationCompositions.RawHostCommands"/>); anywhere else the scope guard refuses it. Reads that use the same transport go through an unmarked method,
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

/// <summary>
/// Marks the parameter of a <see cref="MutatingOperationAttribute"/> method that names the object the call acts on (a
/// container id, a systemd unit, an nginx site). The scope guard then binds the call to the decision, not only to its
/// capability: the value must be an identity the open decision covered — its resolved target, a related resource or an
/// argument value policy saw — on the server it was decided for (<see cref="MutationScope.AuthorizesTarget"/>). An
/// execution delegate that acts on anything else (a re-resolved name that now points elsewhere, a value captured
/// before the approval, a different server) is refused before it reaches the implementation, however it got the
/// value. Put it on the <b>interface</b> method's parameter; at most one per method.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class MutationTargetAttribute : Attribute
{
}
