using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Localization;
using MudBlazor;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;

namespace Whiskers.Components.Shared;

/// <summary>Contract 0019: whether this DI scope is a live Blazor circuit, and when its connection drops. One per
/// circuit (scoped, registered as a <see cref="CircuitHandler"/>). Outside a circuit it stays closed, so the prompt
/// shows nothing there.</summary>
public sealed class InlineApprovalCircuit : CircuitHandler
{
    private int _open;
    private int _down;

    /// <summary>The circuit is open and its connection is up.</summary>
    public bool IsLive => Volatile.Read(ref _open) == 1 && Volatile.Read(ref _down) == 0;

    /// <summary>Raised when the browser connection of this circuit drops.</summary>
    public event Action? ConnectionDown;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _open, 1);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _down, 0);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        MarkDown();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _open, 0);
        MarkDown();
        return Task.CompletedTask;
    }

    private void MarkDown()
    {
        Volatile.Write(ref _down, 1);
        ConnectionDown?.Invoke();
    }
}

/// <summary>
/// Contract 0019: <see cref="IInlineApprovalPrompt"/> over MudBlazor's <see cref="IDialogService"/>, with
/// <see cref="InlineApprovalDialog"/>. Scoped, for a Blazor circuit only. It never decides anything itself: only the
/// dialog records a decision, through the approval store on the "dialog" channel, and every result is checked
/// against the approval read back from the store before it is handed to the runner. Anything unclear is
/// <see cref="InlineApprovalOutcome.LeftPending"/> (fail closed).
/// </summary>
public sealed class MudInlineApprovalPrompt : IInlineApprovalPrompt
{
    /// <summary>How long after the approval's own expiry the prompt stops waiting for a dialog that did not close.</summary>
    internal static readonly TimeSpan ExpiryGrace = TimeSpan.FromSeconds(5);

    private readonly IDialogService _dialogs;
    private readonly IApprovalStore _approvals;
    private readonly InlineApprovalCircuit _circuit;
    private readonly IStringLocalizer<SharedResource>? _text;
    private readonly TimeProvider _time;

    public MudInlineApprovalPrompt(IDialogService dialogs, IApprovalStore approvals, InlineApprovalCircuit circuit,
        IStringLocalizer<SharedResource>? text = null, TimeProvider? time = null)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _circuit = circuit ?? throw new ArgumentNullException(nameof(circuit));
        _text = text;
        _time = time ?? TimeProvider.System;
    }

    public async Task<InlineApprovalResult> AskAsync(InlineApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Fail closed: no live circuit, no dialog — the approval stays pending on the Approvals page.
        if (!_circuit.IsLive || _approvals.Get(request.ApprovalId) is not { Status: ApprovalStatus.Pending } approval)
            return new InlineApprovalResult(InlineApprovalOutcome.LeftPending);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void Down() => stop.Cancel();
        _circuit.ConnectionDown += Down;
        try
        {
            var parameters = new DialogParameters<InlineApprovalDialog>
            {
                { x => x.ApprovalId, request.ApprovalId },
                { x => x.RequesterPayloads, request.RequesterPayloads },
                { x => x.ChangedSinceApproval, request.ChangedSinceApproval },
                { x => x.Time, _time },
            };
            var options = new DialogOptions
            {
                // D2: Escape and Close leave it pending; a click beside the dialog does nothing.
                CloseOnEscapeKey = true,
                BackdropClick = false,
                CloseButton = false,
                MaxWidth = MaxWidth.Small,
                FullWidth = true,
            };
            var title = _text?["InlineApproval_Title"] is { ResourceNotFound: false } t ? t.Value : "Approval needed";
            var reference = await _dialogs.ShowAsync<InlineApprovalDialog>(title, parameters, options);

            // D5: no timeout of its own — the approval's expiry, plus a grace for a dialog that did not close itself.
            var left = approval.ExpiresAt - _time.GetUtcNow().UtcDateTime + ExpiryGrace;
            var expiry = Task.Delay(left > TimeSpan.Zero ? left : TimeSpan.Zero, _time, stop.Token);
            var gone = Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            var finished = await Task.WhenAny(reference.Result, expiry, gone);
            if (finished != reference.Result)
            {
                reference.Close();
                return FromStore(request.ApprovalId);
            }
            var result = await reference.Result;
            return result is { Canceled: false, Data: InlineApprovalResult answered }
                ? Checked(request.ApprovalId, answered)
                : FromStore(request.ApprovalId);
        }
        finally
        {
            _circuit.ConnectionDown -= Down;
        }
    }

    /// <summary>An approval result counts only when the store agrees: approved here means approved in the dialog, "Run
    /// now" means approved (anywhere). Anything else is read from the store.</summary>
    private InlineApprovalResult Checked(string approvalId, InlineApprovalResult answered)
    {
        var approval = _approvals.Get(approvalId);
        return answered.Outcome switch
        {
            InlineApprovalOutcome.ApprovedHere when approval is { Status: ApprovalStatus.Approved, DecidedVia: ApprovalChannels.Dialog }
                => answered,
            InlineApprovalOutcome.ApprovedElsewhereRun when approval is { Status: ApprovalStatus.Approved }
                => answered,
            InlineApprovalOutcome.ApprovedHere or InlineApprovalOutcome.ApprovedElsewhereRun
                => new InlineApprovalResult(InlineApprovalOutcome.LeftPending),
            _ => FromStore(approvalId),
        };
    }

    /// <summary>The dialog closed without an approval: what the store says — rejected, expired, else pending.</summary>
    private InlineApprovalResult FromStore(string approvalId) =>
        _approvals.Get(approvalId) switch
        {
            { Status: ApprovalStatus.Rejected } => new InlineApprovalResult(InlineApprovalOutcome.Rejected),
            { Status: ApprovalStatus.Expired } => new InlineApprovalResult(InlineApprovalOutcome.Expired),
            { } a when a.IsExpiredAt(_time.GetUtcNow().UtcDateTime) => new InlineApprovalResult(InlineApprovalOutcome.Expired),
            _ => new InlineApprovalResult(InlineApprovalOutcome.LeftPending),
        };
}
