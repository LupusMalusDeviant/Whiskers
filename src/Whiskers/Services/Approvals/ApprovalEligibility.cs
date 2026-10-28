using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Approvals;

/// <summary>A human deciding an approval: their principal id (<c>user:&lt;email&gt;</c>), the level they hold right
/// now, and the name the approval records as <see cref="Approval.ResolvedBy"/>.</summary>
public sealed record ApprovalResolver(string PrincipalId, string Level, string Display)
{
    /// <summary>The signed-in web user, at the level their role grants at this moment.</summary>
    public static ApprovalResolver User(string email, AppRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new ApprovalResolver(PrincipalIds.User(email), LevelOf(role), email.Trim());
    }

    /// <summary>The current web user, or <c>null</c> when nobody is signed in (no email): such a caller decides nothing.</summary>
    public static async Task<ApprovalResolver?> CurrentAsync(ICurrentUserService currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        var email = await currentUser.GetEmailAsync();
        return string.IsNullOrWhiteSpace(email) ? null : User(email, await currentUser.GetRoleAsync());
    }

    public static string LevelOf(AppRole role) => role switch
    {
        AppRole.Admin => McpPermissionLevels.Admin,
        AppRole.Operator => McpPermissionLevels.Write,
        _ => McpPermissionLevels.Read,
    };
}

/// <summary>The outcome of a human decision on an approval.</summary>
public enum ApprovalDecisionResult
{
    /// <summary>Recorded; whoever waits on the approval was told.</summary>
    Decided,
    /// <summary>No such approval.</summary>
    NotFound,
    /// <summary>Decided, cancelled or expired already.</summary>
    NotPending,
    /// <summary>Past its expiry: it can no longer be decided, and is now expired.</summary>
    Expired,
    /// <summary>The resolver holds less than the approval's level (INV-34, INV-37).</summary>
    InsufficientLevel,
    /// <summary>The resolver asked for it, and the request may not be decided by its requester (INV-33, PC-D4).</summary>
    RequesterCannotDecide,
    /// <summary>The decision could not be put on record; nothing was decided.</summary>
    NotRecorded,
}

/// <summary>Raised instead of an approval when the requesting principal already has
/// <see cref="ApprovalEligibility.MaxPendingPerPrincipal"/> approvals waiting (C-19).</summary>
public sealed class ApprovalLimitException(string requester)
    : InvalidOperationException($"{requester} already has {ApprovalEligibility.MaxPendingPerPrincipal} approvals waiting " +
                                "for a decision; no further approval was raised.")
{
    public string Requester { get; } = requester;
}

/// <summary>
/// Who may decide an approval (R1-15). Approving and rejecting are held to the same rules (INV-37):
/// <list type="bullet">
/// <item>The resolver holds at least the approval's level — the effective level of the action, not the tool's
/// (INV-34).</item>
/// <item>No requester decides a non-interactive request: direct MCP, an AI trigger, a scheduled task, a webhook
/// (INV-33, PC-D4). An interactive request — the agent chat, a click in the UI — may be decided by the person who
/// made it, unless the active policy sets <c>RequireSecondApprover</c>; such a decision is recorded as a
/// self-approval.</item>
/// </list>
/// An unknown request kind counts as non-interactive (fail closed).
/// </summary>
public static class ApprovalEligibility
{
    /// <summary>How many approvals one principal may have waiting at a time (C-19).</summary>
    public const int MaxPendingPerPrincipal = 25;

    /// <summary>The request kinds (<see cref="Approval.ActorType"/>) a human made while present: the web agent chat,
    /// and a click in the UI or the web terminal.</summary>
    private static readonly HashSet<string> InteractiveKinds = new(StringComparer.Ordinal) { "agent-web", "web", "terminal" };

    public static bool IsInteractiveRequest(Approval approval) => InteractiveKinds.Contains(approval.ActorType);

    /// <summary>Whether <paramref name="resolver"/> asked for <paramref name="approval"/> itself.</summary>
    public static bool IsRequester(Approval approval, ApprovalResolver resolver) =>
        approval.Requesters.Contains(resolver.PrincipalId, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="resolver"/> may decide <paramref name="approval"/> (either way); <c>null</c>
    /// when they may, otherwise why not. Does not look at the approval's status or expiry.</summary>
    public static ApprovalDecisionResult? Check(Approval approval, ApprovalResolver resolver, bool requireSecondApprover)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(resolver);
        // Fail closed: an approval without a readable level asks for Admin (Normalize would read it as Read).
        var level = approval.Level?.Trim().ToLowerInvariant();
        var required = level is McpPermissionLevels.Read or McpPermissionLevels.Write or McpPermissionLevels.Admin
            ? level
            : McpPermissionLevels.Admin;
        if (!McpPermissionLevels.HasAccess(McpPermissionLevels.Normalize(resolver.Level), required))
            return ApprovalDecisionResult.InsufficientLevel;
        if (IsRequester(approval, resolver) && (requireSecondApprover || !IsInteractiveRequest(approval)))
            return ApprovalDecisionResult.RequesterCannotDecide;
        return null;
    }

    /// <summary>The principal ids behind a gateway principal: itself and everyone it acts for.</summary>
    public static IReadOnlyList<string> RequestersOf(MutationPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var ids = new List<string>();
        for (var p = principal; p is not null; p = p.OnBehalfOf)
            if (!string.IsNullOrWhiteSpace(p.Id) && !ids.Contains(p.Id, StringComparer.Ordinal))
                ids.Add(p.Id);
        return ids;
    }
}

/// <summary>How the UI reports a decision that did not go through (resource keys <c>Approval_Result_*</c>).</summary>
public static class ApprovalDecisionMessages
{
    public static string Key(ApprovalDecisionResult result) => $"Approval_Result_{result}";

    /// <summary>The approval can no longer be decided by anyone: its card goes away. Otherwise someone else (an
    /// approver at the required level, a second approver) still can.</summary>
    public static bool EndsTheCard(ApprovalDecisionResult result) => result is ApprovalDecisionResult.Decided
        or ApprovalDecisionResult.NotFound or ApprovalDecisionResult.NotPending or ApprovalDecisionResult.Expired;
}
