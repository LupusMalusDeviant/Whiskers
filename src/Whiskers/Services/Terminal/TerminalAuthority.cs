using Microsoft.Extensions.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Terminal;

/// <summary>
/// Re-checks a running session against what opening it required (R1-17): the kill switch stops every session
/// (INV-21), and one whose owner no longer holds Admin — role lowered, removed from the whitelist, not allowed to sign
/// in — ends (INV-09). The owner is read the way an automation's owner is (<see cref="IAutomationOwnerResolver"/>).
/// With <c>Auth:Disabled</c> the synthetic local user is Admin by that mode (as the web UI treats it), not by a role.
/// </summary>
public sealed class TerminalAuthority : ITerminalAuthority
{
    private readonly IGuardrailStore _guardrails;
    private readonly IAutomationOwnerResolver _owners;
    private readonly bool _authDisabled;

    public TerminalAuthority(IGuardrailStore guardrails, IAutomationOwnerResolver owners, IConfiguration? configuration = null)
    {
        _guardrails = guardrails ?? throw new ArgumentNullException(nameof(guardrails));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _authDisabled = configuration?.GetValue<bool>("Auth:Disabled") == true;
    }

    public TerminalCloseReason? MustEnd(TerminalSession session)
    {
        if (_guardrails.Current.ReadOnlyMode) return TerminalCloseReason.KillSwitch;
        if (_authDisabled && session.Owner.Id == PrincipalIds.User(CurrentUserService.AuthDisabledEmail)) return null;
        return McpPermissionLevels.Normalize(_owners.Resolve(session.Owner.Id).PermissionLevel) == McpPermissionLevels.Admin
            ? null
            : TerminalCloseReason.Revoked;
    }
}
