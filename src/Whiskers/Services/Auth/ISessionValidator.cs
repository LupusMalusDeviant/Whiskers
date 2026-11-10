using System.Security.Claims;
using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>
/// Re-checks a signed-in web principal against the stores as they are now (R1-26, INV-09): the cookie on every
/// request, a Blazor circuit every <see cref="SessionRevalidatingAuthenticationStateProvider.RevalidationInterval"/>,
/// and the current-user resolver before every UI mutation. Fail closed: anything that cannot be shown to be valid
/// (a store that cannot be read, a deleted account, a changed security stamp, an email no longer allowed to sign in)
/// is <see cref="SessionCheck.Valid"/> = <c>false</c>. The answer never carries a claim value or a stamp.
/// </summary>
public interface ISessionValidator
{
    /// <summary>Whether <paramref name="principal"/> may still act, and with which role. Never throws, except
    /// <see cref="OperationCanceledException"/> for <paramref name="ct"/>.</summary>
    Task<SessionCheck> CheckAsync(ClaimsPrincipal? principal, CancellationToken ct = default);
}

/// <summary>The outcome of a session check. <paramref name="Reason"/> is one of <see cref="SessionRevocationReasons"/>
/// when the session is no longer valid; <paramref name="Role"/> is the role the principal holds now (Viewer when
/// invalid).</summary>
public sealed record SessionCheck(bool Valid, AppRole Role, string? Reason = null)
{
    public static SessionCheck Allowed(AppRole role) => new(true, role);
    public static SessionCheck Revoked(string reason) => new(false, AppRole.Viewer, reason);
}

/// <summary>Why a session was ended, as logged and audited (<c>auth.session_revoked</c>).</summary>
public static class SessionRevocationReasons
{
    public const string NotSignedIn = "not_signed_in";
    public const string SyntheticIdentity = "synthetic_identity";
    public const string NoEmail = "no_email";
    public const string AccountDeleted = "account_deleted";
    public const string AccountChanged = "account_changed";
    public const string StampMismatch = "stamp_mismatch";
    public const string NotWhitelisted = "not_whitelisted";
    public const string RoleLowered = "role_lowered";
    public const string StoreUnavailable = "store_unavailable";

    // R1-27: a federated session is valid only on an identity binding that still exists.
    /// <summary>A federated session without a binding claim, or one of an authentication type issued before R1-27.</summary>
    public const string BindingMissing = "binding_missing";
    /// <summary>The session's binding was removed or now belongs to another account.</summary>
    public const string BindingRevoked = "binding_revoked";
    /// <summary>A federated session on an instance without an explicit Admin.</summary>
    public const string SetupIncomplete = "setup_incomplete";

    /// <summary>Contract 0014 (D2): a session without its own id (<see cref="AuthConstants.SessionIdClaim"/>), issued
    /// before the upgrade. Ended once; the person signs in again.</summary>
    public const string SessionIdMissing = "session_id_missing";
}
