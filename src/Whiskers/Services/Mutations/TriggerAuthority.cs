namespace Whiskers.Services.Mutations;

/// <summary>
/// What an AI trigger may change right now (PC-D6, PC-D7, R1-13), asked by the gateway for every mutation a trigger's
/// run makes. A trigger is read-only unless an Admin granted it capabilities and targets; a granted write still runs
/// only while the Admin who granted it (the owner) holds that authority. Anything else is refused: the run is
/// unattended, so what would need a confirmation is a Deny.
/// </summary>
public interface ITriggerAuthority
{
    /// <summary><c>null</c> when trigger <paramref name="triggerId"/> may perform <paramref name="capability"/> (at
    /// its effective level) on the resolved <paramref name="target"/>; otherwise why not. An owner who lost the
    /// authority suspends the trigger and an Admin is told.</summary>
    Task<string?> RefusalAsync(string triggerId, Capability capability, MutationTarget target, CancellationToken ct);
}

public static class TriggerAuthority
{
    /// <summary>The trigger a principal acts as or for: <c>trigger:&lt;id&gt;</c> itself, or an agent session on
    /// its behalf.</summary>
    public static string? TriggerOf(MutationPrincipal? principal) =>
        principal is null
            ? null
            : PrincipalIds.TriggerIdOf(principal.Id) ?? PrincipalIds.TriggerIdOf(principal.OnBehalfOf?.Id);

    /// <summary>Whether <paramref name="value"/> fits <paramref name="glob"/> (<c>*</c>, <c>?</c>), ignoring case.</summary>
    public static bool GlobMatch(string glob, string? value)
    {
        if (string.IsNullOrWhiteSpace(glob) || string.IsNullOrEmpty(value)) return false;
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(glob.Trim())
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}
