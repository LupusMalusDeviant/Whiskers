namespace Whiskers.Services.Auth;

/// <summary>
/// Records sign-ins, refused sign-ins and sign-outs in the audit log (R1-22). Hooks only: a call never changes what the
/// login or logout does, and a failed audit write never blocks a sign-in — it is logged and marks the audit trail
/// incomplete (the <c>audit</c> health check, R1-19).
/// </summary>
public interface IAuthAuditLog
{
    /// <summary>A completed sign-in of <paramref name="email"/> by <paramref name="method"/> (local, google, oidc).</summary>
    Task SignedInAsync(HttpContext context, string method, string email);

    /// <summary>A refused sign-in. <paramref name="account"/> is the account it was for, only when that account exists
    /// (an unknown name is never recorded: it may be a mistyped password). <paramref name="reason"/> is one of
    /// <see cref="AuthAuditReasons"/>.</summary>
    Task SignInRefusedAsync(HttpContext context, string method, string? account, string reason);

    /// <summary>A sign-out of the current user.</summary>
    Task SignedOutAsync(HttpContext context);

    /// <summary>A session ended by revalidation (R1-26): <paramref name="surface"/> is <c>cookie</c> or <c>circuit</c>,
    /// <paramref name="reason"/> one of <see cref="SessionRevocationReasons"/>. <paramref name="context"/> is the request
    /// when there is one (a circuit has none).</summary>
    Task SessionRevokedAsync(HttpContext? context, string? account, string surface, string reason) => Task.CompletedTask;
}

/// <summary>Why a sign-in was refused, as recorded in the audit log.</summary>
public static class AuthAuditReasons
{
    public const string UnknownAccount = "unknown_account";
    public const string WrongPassword = "wrong_password";
    public const string LockedOut = "locked_out";
    public const string NotWhitelisted = "not_whitelisted";
    public const string ProviderFailure = "provider_failure";
    /// <summary>The account had no security stamp and none could be stored, so no session could be tied to it (R1-26).</summary>
    public const string NoSecurityStamp = "no_security_stamp";

    // R1-27: why a Google/OIDC sign-in was refused at its admission (IFederatedAdmission).
    /// <summary>No explicit Admin exists yet: federated sign-in waits for the setup wizard.</summary>
    public const string SetupIncomplete = "setup_incomplete";
    /// <summary>The provider delivered no issuer or no subject.</summary>
    public const string NoSubject = "no_subject";
    /// <summary>An unbound subject without an email claim.</summary>
    public const string NoEmail = "no_email";
    public const string EmailUnverified = "email_unverified";
    public const string ProviderUntrusted = "provider_untrusted";
    /// <summary>A binding request was recorded; an Admin or the host operator confirms it.</summary>
    public const string PendingConfirmation = "pending_confirmation";
    /// <summary>The binding changed while it was being written (another subject or issuer got there first).</summary>
    public const string SubjectConflict = "subject_conflict";
    public const string StoreUnavailable = "store_unavailable";
    public const string AuditUnavailable = "audit_unavailable";
}
