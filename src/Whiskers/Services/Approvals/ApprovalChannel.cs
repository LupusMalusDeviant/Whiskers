namespace Whiskers.Services.Approvals;

/// <summary>Where a human decided an approval (contract 0019, D6). Persisted as "page" | "dialog" | "chat".</summary>
public enum ApprovalChannel
{
    /// <summary>The Approvals page (U-33).</summary>
    Page,

    /// <summary>The "Approval needed" dialog of the UI action that raised the approval (contract 0019).</summary>
    Dialog,

    /// <summary>The in-band card of the web agent chat (A-01).</summary>
    Chat,
}

/// <summary>The persisted names of <see cref="ApprovalChannel"/>.</summary>
public static class ApprovalChannels
{
    public const string Page = "page";
    public const string Dialog = "dialog";
    public const string Chat = "chat";

    /// <summary>The value written to <c>Approvals.DecidedVia</c> and to the execution's audit details.</summary>
    public static string Wire(this ApprovalChannel channel) => channel switch
    {
        ApprovalChannel.Page => Page,
        ApprovalChannel.Dialog => Dialog,
        ApprovalChannel.Chat => Chat,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown approval channel."),
    };
}
