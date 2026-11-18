using System.Text.Json;
using Whiskers.Services.Guardrails;

namespace Whiskers.Services.Approvals;

/// <summary>
/// Contract 0019: shows the approval raised for one UI call in the dialog the person is in. Registered scoped, for a
/// Blazor circuit only; no hosted service, MCP handler or automation can resolve it (INV-65).
/// </summary>
public interface IInlineApprovalPrompt
{
    /// <summary>Shows the recorded approval <see cref="InlineApprovalRequest.ApprovalId"/> and waits until the person
    /// decides it, closes the dialog, someone else decides it or it expires. Never decides anything by itself.</summary>
    Task<InlineApprovalResult> AskAsync(InlineApprovalRequest request, CancellationToken ct);
}

/// <summary>What the prompt shows: the approval this call raised.</summary>
/// <param name="ApprovalId">The recorded approval, read back from the store by the dialog.</param>
/// <param name="OperationId">The operation the approval was raised for.</param>
/// <param name="CapabilityId">The capability of the call.</param>
/// <param name="RequesterPayloads">The payload arguments of the runner's own in-memory request (withheld from the stored
/// card), shown to the requester only; never persisted or logged.</param>
public sealed record InlineApprovalRequest(string ApprovalId, string OperationId, string CapabilityId,
    IReadOnlyDictionary<string, JsonElement>? RequesterPayloads = null)
{
    /// <summary>The call was rebuilt after an approval and asked for a new one: shown as "changed since you approved"
    /// (contract 0019, D4).</summary>
    public bool ChangedSinceApproval { get; init; }
}

public enum InlineApprovalOutcome
{
    /// <summary>The person approved it in the dialog (recorded, via Dialog).</summary>
    ApprovedHere,
    /// <summary>Someone else approved it while the dialog was open, and the person then clicked "Run now".</summary>
    ApprovedElsewhereRun,
    /// <summary>Rejected in the dialog (recorded) or elsewhere.</summary>
    Rejected,
    /// <summary>Closed, cancelled or the circuit went away: the approval stays pending on the Approvals page.</summary>
    LeftPending,
    /// <summary>It expired while the dialog was open.</summary>
    Expired,
}

public sealed record InlineApprovalResult(InlineApprovalOutcome Outcome, ApprovalDecisionResult? Decision = null)
{
    /// <summary>Cancelled once the circuit the dialog ran in drops or closes (INV-65): the runner checks it right before
    /// the continuation, so a circuit lost after the decision runs nothing. <c>None</c> from a prompt without a circuit.</summary>
    public CancellationToken Circuit { get; init; }
}

/// <summary>Contract 0019 (D7): the rollout list of capabilities whose INV-59 approval may be given in the dialog.
/// Code, not configuration.</summary>
public static class InlineApproval
{
    /// <summary>Capabilities whose INV-59 approval may be given in the dialog (waves 1 and 2 in 1.0.0, D7). Wave 3
    /// (<c>container.remove</c>, <c>selfbackup.restore</c>, <c>mtls.shared_client_retire</c>,
    /// <c>volume.backup_delete</c>) and <c>server.hostkey_replace</c> are not listed: they keep today's refusal with
    /// the approval id and are approved on the Approvals page.</summary>
    public static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        // Wave 1: the onboarding window (the reported case) and the container actions on Dashboard and Container detail.
        "server.onboard",
        "container.start", "container.stop", "container.restart", "container.update", "container.rollback",
        // Wave 2: mTLS on Servers, volumes and DB queries that name the control plane, and the deploy / compose /
        // app-store paths whose plan touches a protected container.
        "mtls.client_migrate", "certificate.renew_mtls", "certificate.issue_mtls", "mtls.client_renew",
        "volume.backup", "volume.restore", "db.query",
        "compose.deploy", "app.deploy",
        // … and the Git deploy started from the UI (Git deploy page, a webhook's test run): a deploy path too (D7).
        "gitdeploy.deploy",
    };

    /// <summary>Whether the approval of a call of <paramref name="capabilityId"/> refused with
    /// <paramref name="matchedRules"/> may be given in the dialog: an INV-59 hit of a listed capability.</summary>
    public static bool Applies(string capabilityId, IReadOnlyList<string> matchedRules) =>
        matchedRules.Contains(ProtectedResourceRule.RuleId, StringComparer.Ordinal) && Capabilities.Contains(capabilityId);
}
