using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Auth;

/// <summary>
/// The lifecycle of local (ASP.NET Identity) accounts, R1-49: a user changes their own password, an Admin adds,
/// resets and deletes accounts, and a person holding a one-time recovery code sets a new password. Every write goes
/// through the Mutation Gateway (the marked methods) and checks its authority again itself: Admin for other people's
/// accounts, the signed-in user for their own password (which needs the current one). Every credential change gives
/// the account a new security stamp, so R1-26 ends every session issued before it (<see cref="ISessionValidator"/>).
/// <para>Secrets never leave this service except as the return value of the call that made them: no password,
/// hash or recovery code is logged, audited or put into an argument the gateway records — not even a prefix.</para>
/// <para>Recovery codes replace "temporary passwords": a reset removes the password and hands out a code that cannot
/// sign in; it can only set a new password (<see cref="RedeemRecoveryCodeAsync"/>), so the change on first use is
/// enforced by construction. Codes are random (<see cref="RecoveryCodes"/>), stored only as an Identity password hash,
/// valid for <see cref="RecoveryCodes.Lifetime"/> and for one use.</para>
/// </summary>
public interface ILocalAccountService
{
    /// <summary>Every local account with its role and state. Admin only: anyone else gets an empty list.</summary>
    Task<IReadOnlyList<LocalAccountInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>The signed-in user's own local account, or <c>null</c> when they have none (a Google/OIDC user) or the
    /// session is no longer valid.</summary>
    Task<LocalAccountInfo?> GetOwnAccountAsync(CancellationToken ct = default);

    /// <summary>Changes the signed-in user's own password. Needs the current password; a wrong one counts towards the
    /// lockout like a failed sign-in. Ends every session of the account, the current one included.</summary>
    [MutatingOperation("account.password_change")]
    Task<LocalAccountResult> ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>Admin: adds a local account for <paramref name="email"/> with <paramref name="role"/>. The account has
    /// no password: the result carries a recovery code (an invitation) with which its owner sets one.</summary>
    [MutatingOperation("account.create")]
    Task<LocalAccountResult> CreateAsync(string email, AppRole role, CancellationToken ct = default);

    /// <summary>Admin: removes another account's password, ends its sessions and hands out a recovery code. An Admin
    /// changes their own password with <see cref="ChangeOwnPasswordAsync"/>.</summary>
    [MutatingOperation("account.password_reset")]
    Task<LocalAccountResult> ResetPasswordAsync(string email, CancellationToken ct = default);

    /// <summary>Admin: deletes another local account and its explicit role entry. Refused for the caller's own
    /// account and for the last Admin who can still sign in.</summary>
    [MutatingOperation("account.delete")]
    Task<LocalAccountResult> DeleteAsync(string email, CancellationToken ct = default);

    /// <summary>Anonymous: sets a new password with a recovery code. A wrong, used or expired code, an unknown email and
    /// a locked account all give the same answer; a wrong code counts towards the lockout. Not a gateway call: there is
    /// no signed-in principal (like a sign-in); every outcome is audited.</summary>
    Task<LocalAccountResult> RedeemRecoveryCodeAsync(string email, string code, string newPassword, CancellationToken ct = default);
}

/// <summary>A local account as the account list shows it. Never carries a hash, stamp or code.</summary>
public sealed record LocalAccountInfo(string Email, AppRole Role, bool HasExplicitRole, LocalAccountState State,
    DateTimeOffset? RecoveryExpiresUtc);

/// <summary>Whether a local account can sign in.</summary>
public enum LocalAccountState
{
    /// <summary>A password is set.</summary>
    Active,
    /// <summary>No password; a recovery code is waiting to be used (or has expired, see the expiry).</summary>
    RecoveryPending,
    /// <summary>Locked after too many wrong passwords or codes, until the lockout ends.</summary>
    LockedOut,
    /// <summary>No password and no recovery code: an Admin or the host operator has to issue one.</summary>
    NoCredential,
}

/// <summary>
/// The outcome of a lifecycle call. <see cref="Success"/> is read by the gateway (MutationResults): a refused call is
/// recorded as Failed. <see cref="RecoveryCode"/> is set only on the call that made it and must be shown once and never
/// stored. <see cref="Error"/> is one of <see cref="LocalAccountErrors"/>; <see cref="PolicyErrors"/> are the Identity
/// error codes of a password the policy refused.
/// </summary>
public sealed record LocalAccountResult(bool Success, string? Error = null)
{
    public IReadOnlyList<string> PolicyErrors { get; init; } = [];
    public string? RecoveryCode { get; init; }
    public DateTimeOffset? RecoveryExpiresUtc { get; init; }

    /// <summary>Never prints the recovery code (a result that reaches a log or a debugger display stays clean).</summary>
    public override string ToString() =>
        $"LocalAccountResult {{ Success = {Success}, Error = {Error ?? "-"}, PolicyErrors = {PolicyErrors.Count}, " +
        $"RecoveryCode = {(RecoveryCode is null ? "-" : "[issued]")} }}";

    public static LocalAccountResult Ok() => new(true);
    public static LocalAccountResult Refused(string error) => new(false, error);
}

/// <summary>Why a lifecycle call was refused (shown by the pages through the <c>LocalAccounts_Error_*</c> texts).</summary>
public static class LocalAccountErrors
{
    public const string NotAuthorized = "not_authorized";
    public const string SessionInvalid = "session_invalid";
    public const string NotFound = "not_found";
    public const string AlreadyExists = "already_exists";
    public const string InvalidEmail = "invalid_email";
    public const string RoleConflict = "role_conflict";
    public const string OwnAccount = "own_account";
    public const string LastAdmin = "last_admin";
    public const string WrongPassword = "wrong_password";
    public const string LockedOut = "locked_out";
    public const string PasswordPolicy = "password_policy";
    /// <summary>The recovery code, the email or the account state did not allow the change — deliberately one answer.</summary>
    public const string InvalidCode = "invalid_code";
    public const string StoreUnavailable = "store_unavailable";
    public const string Conflict = "conflict";
}
