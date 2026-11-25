using Whiskers.Services.Mutations;

namespace Whiskers.Services.Guardrails;

/// <summary>Contract 0023 (D8): <c>guardrails.kill_switch_on</c> is verified by reading the store back — the active
/// preset's <c>ReadOnlyMode</c> is on. A store that could not load its configuration enforces the fail-closed policy
/// (the kill switch on), but the call did not set it: that is not verifiable.</summary>
public sealed class KillSwitchOnVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "guardrails.kill_switch_on";

    protected override Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var store = Module<IGuardrailStore>("guardrails");
        if (store.LoadError is not null)
            throw new MutationNotVerifiableException("the guardrail configuration could not be loaded; the fail-closed policy is enforced.");
        if (!store.Current.ReadOnlyMode)
            throw new MutationVerificationFailedException("the active preset's kill switch is off.");
        // Enforced, but a restart before the write would lose it: not verified as stored.
        if (store.KillSwitchNotWritten)
            throw new MutationNotVerifiableException("the kill switch is enforced but not written to guardrails.json yet; an earlier guardrail save still holds the store.");
        return Task.CompletedTask;
    }
}
