namespace Whiskers.Services.Mutations;

/// <summary>
/// Contract 0014 (D3): an automation run or a webhook delivery — nobody is there to confirm. Every decision that keyed
/// on the automation surface or kind reads this predicate, so a webhook (its own kind and surface since INV-01)
/// decides exactly as it did when it ran as an automation.
/// </summary>
internal static class Unattended
{
    /// <summary>The surface of an unattended run.</summary>
    public static bool IsSurface(MutationSurface surface) =>
        surface is MutationSurface.Automation or MutationSurface.Webhook;

    /// <summary>The kind of an unattended run's principal.</summary>
    public static bool IsPrincipal(MutationPrincipal p) =>
        p.Kind is MutationPrincipalKind.Automation or MutationPrincipalKind.Webhook;

    /// <summary>Nobody is there to confirm: an automation, a webhook delivery, or anything made for an AI trigger
    /// (R1-13).</summary>
    public static bool Is(MutationRequest r) => IsSurface(r.Surface) || r.TriggerId is not null;
}
