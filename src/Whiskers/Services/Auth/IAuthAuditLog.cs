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
}

/// <summary>Why a sign-in was refused, as recorded in the audit log.</summary>
public static class AuthAuditReasons
{
    public const string UnknownAccount = "unknown_account";
    public const string WrongPassword = "wrong_password";
    public const string LockedOut = "locked_out";
    public const string NotWhitelisted = "not_whitelisted";
    public const string ProviderFailure = "provider_failure";
}
