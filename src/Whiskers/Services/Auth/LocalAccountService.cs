using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>
/// The local-account lifecycle (R1-49), on ASP.NET Identity's <see cref="UserManager{TUser}"/>: its password hashing,
/// password policy (<c>IdentityOptions.Password</c>), lockout and security stamp — nothing of its own. Scoped: the
/// Identity store and <see cref="WhiskersIdentityDbContext"/> are the same context of one scope, so a credential change
/// and its recovery-code row are saved together.
/// <para>Authority is re-checked here, whatever the page or the gateway decided: <see cref="ICurrentUserService.CheckSessionAsync"/>
/// (R1-26) must be valid and Admin for other people's accounts; the own password needs the current one. Admin changes
/// are serialised process-wide and re-check the caller and the last-Admin rule under that lock, so two Admins deleting
/// each other at once cannot leave the instance without one (Whiskers is a single instance, PRODUCT_CONTRACT §5).</para>
/// </summary>
public sealed class LocalAccountService : ILocalAccountService
{
    public const string RecoveryRedeemedAction = "auth.recovery_redeemed";
    public const string RecoveryRefusedAction = "auth.recovery_refused";

    // One Admin change at a time, also across scopes (each circuit has its own service) — and the same boundary as the
    // removal of an identity binding (R1-27): the last-usable-Admin check and the commit of a removal are one step.
    private static SemaphoreSlim AdminGate => AdminSignIn.Gate;

    private readonly UserManager<AppUser> _users;
    private readonly WhiskersIdentityDbContext _db;
    private readonly IRoleService _roles;
    private readonly ICurrentUserService _user;
    private readonly IOwnerAccounts _owners;
    private readonly IAuditLogService _audit;
    private readonly IRecoveryAttemptLimiter _limiter;
    private readonly string _client;
    private readonly bool _authDisabled;
    private readonly TimeProvider _time;
    private readonly ILogger<LocalAccountService>? _logger;
    private readonly IIdentityBindingStore? _bindings;
    private readonly Whiskers.Services.Guardrails.IGuardrailStore? _guardrails;

    /// <param name="bindings">R1-27: an Admin with an active Google/OIDC binding can sign in (last-Admin rule).</param>
    public LocalAccountService(UserManager<AppUser> users, WhiskersIdentityDbContext db, IRoleService roles,
        ICurrentUserService user, IOwnerAccounts owners, IAuditLogService audit, IRecoveryAttemptLimiter limiter,
        IClientAddress client, IConfiguration configuration, TimeProvider? time = null,
        ILogger<LocalAccountService>? logger = null, IIdentityBindingStore? bindings = null,
        Whiskers.Services.Guardrails.IGuardrailStore? guardrails = null)
    {
        _bindings = bindings;
        // Contract 0009 (E5): the kill switch may block recovery-code redemption. Registered in the app (Core).
        _guardrails = guardrails;
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(configuration);
        // Read now: in a circuit the service is created with the page, while the circuit's connection request is current.
        _client = client.Address ?? "unknown";
        // With AUTH_DISABLED every LAN client is the synthetic Admin: it must not mint real credentials (fail closed).
        _authDisabled = configuration.GetValue<bool>("Auth:Disabled");
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    // ------------------------------------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<LocalAccountInfo>> ListAsync(CancellationToken ct = default)
    {
        if ((await AdminAsync()).Error is not null) return [];
        var users = await _db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct);
        var codes = await _db.UserTokens.AsNoTracking()
            .Where(t => t.LoginProvider == RecoveryCodes.TokenProvider && t.Name == RecoveryCodes.TokenName)
            .ToDictionaryAsync(t => t.UserId, t => t.Value, ct);
        return users.Select(u => Info(u, codes.GetValueOrDefault(u.Id))).ToList();
    }

    public async Task<LocalAccountInfo?> GetOwnAccountAsync(CancellationToken ct = default)
    {
        var (email, error) = await SelfAsync();
        if (error is not null) return null;
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == _users.NormalizeEmail(email), ct);
        if (user is null) return null;
        return Info(user, (await RecoveryCodes.FindAsync(_db, user.Id, ct))?.Value);
    }

    private LocalAccountInfo Info(AppUser user, string? code)
    {
        var now = _time.GetUtcNow();
        var email = user.Email ?? user.UserName ?? "";
        DateTimeOffset? expires = RecoveryCodes.TryRead(code, out var e, out _) ? e : null;
        var state = user.LockoutEnd is { } end && end > now ? LocalAccountState.LockedOut
            : user.PasswordHash is not null ? LocalAccountState.Active
            : expires is not null ? LocalAccountState.RecoveryPending
            : LocalAccountState.NoCredential;
        return new LocalAccountInfo(email, _roles.GetRole(email), _roles.HasExplicitRole(email), state, expires);
    }

    // ------------------------------------------------------------------------------------------------ self service

    public async Task<LocalAccountResult> ChangeOwnPasswordAsync(string currentPassword, string newPassword,
        CancellationToken ct = default)
    {
        var (email, error) = await SelfAsync();
        if (error is not null) return LocalAccountResult.Refused(error);
        _db.ChangeTracker.Clear();   // a circuit lives long: read the account as it is now
        var user = await _users.FindByEmailAsync(email);
        if (user is null) return LocalAccountResult.Refused(LocalAccountErrors.NotFound);
        if (await _users.IsLockedOutAsync(user)) return LocalAccountResult.Refused(LocalAccountErrors.LockedOut);

        // Identity checks the current password, validates the new one against the policy, hashes it and changes the
        // security stamp (every session of the account ends, R1-26) in one save.
        var result = await _users.ChangePasswordAsync(user, currentPassword ?? "", newPassword ?? "");
        if (result.Succeeded)
        {
            _logger?.LogInformation("Local account {Email} changed its password; its sessions were ended", user.Email);
            return LocalAccountResult.Ok();
        }
        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
        {
            // A guessed current password counts like a failed sign-in (INV-10).
            await _users.AccessFailedAsync(user);
            return LocalAccountResult.Refused(LocalAccountErrors.WrongPassword);
        }
        return Policy(result);
    }

    // ------------------------------------------------------------------------------------------------ admin

    public async Task<LocalAccountResult> CreateAsync(string email, AppRole role, CancellationToken ct = default)
    {
        email = (email ?? "").Trim();
        if (!IsEmail(email)) return LocalAccountResult.Refused(LocalAccountErrors.InvalidEmail);
        if (!Enum.IsDefined(role)) return LocalAccountResult.Refused(LocalAccountErrors.RoleConflict);

        await AdminGate.WaitAsync(ct);
        try
        {
            // Inside the try: the context of a circuit closed while it waited is disposed, and Clear() throws — the
            // gate is released all the same.
            _db.ChangeTracker.Clear();   // a circuit lives long: read the account as it is now
            if ((await AccountAdminAsync()).Error is { } refused) return LocalAccountResult.Refused(refused);
            if (await _users.FindByEmailAsync(email) is not null) return LocalAccountResult.Refused(LocalAccountErrors.AlreadyExists);
            // An email that already holds a different explicit role (a Google/OIDC user) is not silently changed:
            // its role is managed in the role list.
            var explicitRole = _roles.HasExplicitRole(email);
            if (explicitRole && _roles.GetRole(email) != role) return LocalAccountResult.Refused(LocalAccountErrors.RoleConflict);

            var user = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await _users.CreateAsync(user);   // no password: the owner sets one with the code
            if (!created.Succeeded)
                return LocalAccountResult.Refused(created.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName")
                    ? LocalAccountErrors.AlreadyExists
                    : created.Errors.Any(e => e.Code is "InvalidEmail" or "InvalidUserName")
                        ? LocalAccountErrors.InvalidEmail
                        : LocalAccountErrors.Conflict);

            IdentityResult issued;
            string? code;
            DateTimeOffset expires;
            try
            {
                (issued, code, expires) = await RecoveryCodes.IssueAsync(_users, _db, user, _time.GetUtcNow(), ct);
            }
            catch
            {
                // No half-created account without role or credential stays behind (it would block a retry).
                await DeleteQuietlyAsync(email);
                throw;
            }
            if (!issued.Succeeded || code is null)
            {
                await DeleteQuietlyAsync(email);
                return LocalAccountResult.Refused(LocalAccountErrors.Conflict);
            }
            try
            {
                if (!explicitRole) await _roles.SetRoleAsync(email, role);
            }
            catch
            {
                // Without its role the account could not sign in (the whitelist is closed once roles exist): undo.
                await DeleteQuietlyAsync(email);
                throw;
            }
            _owners.RememberLocalAccounts();
            _logger?.LogInformation("Local account {Email} was created with role {Role}", email, role);
            return new LocalAccountResult(true) { RecoveryCode = code, RecoveryExpiresUtc = expires };
        }
        finally { AdminGate.Release(); }
    }

    public async Task<LocalAccountResult> ResetPasswordAsync(string email, CancellationToken ct = default)
    {
        email = (email ?? "").Trim();
        await AdminGate.WaitAsync(ct);
        try
        {
            // Inside the try: the context of a circuit closed while it waited is disposed, and Clear() throws — the
            // gate is released all the same.
            _db.ChangeTracker.Clear();   // a circuit lives long: read the account as it is now
            var (actor, refused) = await AccountAdminAsync();
            if (refused is not null) return LocalAccountResult.Refused(refused);
            if (SameEmail(actor, email)) return LocalAccountResult.Refused(LocalAccountErrors.OwnAccount);
            var user = await _users.FindByEmailAsync(email);
            if (user is null) return LocalAccountResult.Refused(LocalAccountErrors.NotFound);

            var (issued, code, expires) = await RecoveryCodes.IssueAsync(_users, _db, user, _time.GetUtcNow(), ct);
            if (!issued.Succeeded || code is null) return LocalAccountResult.Refused(LocalAccountErrors.Conflict);
            _logger?.LogInformation("The password of local account {Email} was reset; its sessions were ended", user.Email);
            return new LocalAccountResult(true) { RecoveryCode = code, RecoveryExpiresUtc = expires };
        }
        finally { AdminGate.Release(); }
    }

    public async Task<LocalAccountResult> DeleteAsync(string email, CancellationToken ct = default)
    {
        email = (email ?? "").Trim();
        await AdminGate.WaitAsync(ct);
        try
        {
            // Inside the try: the context of a circuit closed while it waited is disposed, and Clear() throws — the
            // gate is released all the same.
            _db.ChangeTracker.Clear();   // a circuit lives long: read the account as it is now
            // Re-checked under the lock: an Admin whose own account went a moment ago decides nothing.
            var (actor, refused) = await AccountAdminAsync();
            if (refused is not null) return LocalAccountResult.Refused(refused);
            if (SameEmail(actor, email)) return LocalAccountResult.Refused(LocalAccountErrors.OwnAccount);
            var user = await _users.FindByEmailAsync(email);
            if (user is null) return LocalAccountResult.Refused(LocalAccountErrors.NotFound);
            var target = user.Email ?? email;
            if (_roles.GetRole(target) == AppRole.Admin && !await AnotherUsableAdminAsync(target, ct))
                return LocalAccountResult.Refused(LocalAccountErrors.LastAdmin);

            // The role goes first, so a Google/OIDC sign-in with the same address does not inherit it — and so the role
            // store's own last-Admin rule (it does not take this lock: the User roles panel may have demoted the other
            // Admin a moment ago) refuses before anything has changed.
            AppRole? removedRole = null;
            if (_roles.HasExplicitRole(target))
            {
                var role = _roles.GetRole(target);
                try { await _roles.RemoveRoleAsync(target); }
                // Only the role store's last-Admin refusal (TA-02) reads as "last Admin": the target is then the one
                // explicit Admin left. Any other failure is a failure and goes on.
                catch (InvalidOperationException) when (IsLastExplicitAdmin(target))
                {
                    return LocalAccountResult.Refused(LocalAccountErrors.LastAdmin);
                }
                removedRole = role;
            }

            IdentityResult deleted;
            try { deleted = await _users.DeleteAsync(user); }
            catch
            {
                await RestoreRoleAsync(target, removedRole);
                throw;
            }
            if (!deleted.Succeeded)
            {
                await RestoreRoleAsync(target, removedRole);
                return LocalAccountResult.Refused(LocalAccountErrors.Conflict);
            }
            _logger?.LogInformation("Local account {Email} was deleted", target);
            return LocalAccountResult.Ok();
        }
        finally { AdminGate.Release(); }
    }

    /// <summary>Whether an Admin other than <paramref name="target"/> can still sign in (R1-27, contract 0003 rule 6 —
    /// the predicate the identity-binding store's last-Admin rule uses too): a local account with a password, or an
    /// active Google/OIDC identity binding. An Admin waiting for a recovery code without a binding does not count.</summary>
    private async Task<bool> AnotherUsableAdminAsync(string target, CancellationToken ct)
    {
        foreach (var admin in _roles.GetRoleData().Roles
                     .Where(r => r.Role == AppRole.Admin && !SameEmail(r.Email, target)).Select(r => r.Email))
        {
            if (await AdminSignIn.HasLocalPasswordAsync(_db, _users.KeyNormalizer, admin, ct)) return true;
            if (AdminSignIn.HasBinding(_bindings, admin)) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="email"/> is an explicit Admin and no other explicit Admin is left — the case the
    /// role store refuses to remove (TA-02).</summary>
    private bool IsLastExplicitAdmin(string email)
    {
        var admins = _roles.GetRoleData().Roles.Where(r => r.Role == AppRole.Admin).ToList();
        return admins.Any(r => SameEmail(r.Email, email)) && admins.All(r => SameEmail(r.Email, email));
    }

    /// <summary>Puts back a role removed for a deletion that then failed: the account still exists and keeps its role.</summary>
    private async Task RestoreRoleAsync(string email, AppRole? role)
    {
        if (role is not { } r) return;
        try { await _roles.SetRoleAsync(email, r); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError("The role of local account {Email} could not be restored after a failed deletion ({Error})",
                email, ex.GetType().Name);
        }
    }

    private async Task DeleteQuietlyAsync(string email)
    {
        try
        {
            _db.ChangeTracker.Clear();
            if (await _users.FindByEmailAsync(email) is { } half) await _users.DeleteAsync(half);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError("A half-created local account could not be removed again ({Error})", ex.GetType().Name);
        }
    }

    // ------------------------------------------------------------------------------------------------ recovery

    public async Task<LocalAccountResult> RedeemRecoveryCodeAsync(string email, string code, string newPassword,
        CancellationToken ct = default)
    {
        email = (email ?? "").Trim();
        var normalizedCode = RecoveryCodes.Normalize(code);
        // Without real authentication there is no local sign-in a new password could be for (the page leaves too).
        if (_authDisabled) return LocalAccountResult.Refused(LocalAccountErrors.AuthDisabled);

        // The per-client and per-account limit comes first: a refused attempt reads nothing and hashes nothing. Only the
        // first throttled attempt of a client in a window is audited; the storm is logged as a count (bounded rows).
        var attempt = _limiter.TryAcquire(_client, email);
        if (!attempt.Allowed)
        {
            if (attempt.RefusedInWindow == 1)
                return await RefusedAsync(null, "rate_limited");
            if ((attempt.RefusedInWindow & (attempt.RefusedInWindow - 1)) == 0)
                _logger?.LogWarning("{Count} recovery-code attempts of one client were throttled in the current window",
                    attempt.RefusedInWindow);
            return LocalAccountResult.Refused(LocalAccountErrors.InvalidCode);
        }

        // Contract 0009 (D5, E5): while the kill switch is on and an Admin blocked this exemption beforehand — or the
        // policy store is fail-closed (D4) — nothing is read or changed; one refusal names the kill switch's rule.
        if (_guardrails?.Current.KillSwitchBlocks(Whiskers.Configuration.KillSwitchExemptions.RecoveryCode) == true)
        {
            // The typed address is not recorded: no account was looked up (as for an unknown one).
            await _audit.TryLogAsync("anonymous", "web", RecoveryRefusedAction, AuthAuditLog.Target, "", "",
                $"reason=kill_switch; rule={Whiskers.Configuration.KillSwitchExemptions.RuleId}", success: false);
            return LocalAccountResult.Refused(LocalAccountErrors.KillSwitch);
        }

        _db.ChangeTracker.Clear();   // read the account as it is now, not as this scope last saw it
        var user = email.Length == 0 ? null : await _users.FindByEmailAsync(email);
        if (user is null)
        {
            // The same hashing work as a real check: the answer time does not tell which emails exist.
            BurnHash(normalizedCode);
            return await RefusedAsync(null, "unknown_account");
        }
        if (await _users.IsLockedOutAsync(user))
        {
            BurnHash(normalizedCode);
            return await RefusedAsync(user.Email, "locked_out");
        }

        var row = await RecoveryCodes.FindAsync(_db, user.Id, ct);
        if (row is null || !RecoveryCodes.TryRead(row.Value, out var expires, out var hash))
        {
            // No code to guess: not counted against the account, or anyone could lock out an account that has a
            // password by trying codes for it.
            BurnHash(normalizedCode);
            return await RefusedAsync(user.Email, "no_code");
        }
        var verified = _users.PasswordHasher.VerifyHashedPassword(user, hash, normalizedCode);
        if (verified == PasswordVerificationResult.Failed)
        {
            await _users.AccessFailedAsync(user);
            return await RefusedAsync(user.Email, "wrong_code");
        }
        if (_time.GetUtcNow() >= expires) return await RefusedAsync(user.Email, "expired");

        // The code was right: the policy may now say what is wrong with the new password; the code stays valid.
        var errors = new List<IdentityError>();
        foreach (var validator in _users.PasswordValidators)
        {
            var check = await validator.ValidateAsync(_users, user, newPassword ?? "");
            if (!check.Succeeded) errors.AddRange(check.Errors);
        }
        if (errors.Count > 0) return Policy(IdentityResult.Failed([.. errors]));

        // The record first: a password set anonymously that the trail does not show is not set.
        var account = user.Email ?? email;
        if (!await _audit.TryLogAsync(account, "web", RecoveryRedeemedAction, AuthAuditLog.Target, account, account,
                "a new password is set with a recovery code", success: true))
        {
            _logger?.LogError("A recovery-code redemption was refused: its audit row could not be written");
            return LocalAccountResult.Refused(LocalAccountErrors.StoreUnavailable);
        }

        // One save: the code is used up, the password set, the lockout cleared and the stamp changed. The account's
        // concurrency stamp makes a second use of the same code at the same moment fail.
        IdentityResult saved;
        try
        {
            _db.UserTokens.Remove(row);
            user.PasswordHash = _users.PasswordHasher.HashPassword(user, newPassword!);
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            saved = await _users.UpdateSecurityStampAsync(user);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            saved = IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure());
        }
        catch
        {
            // Whatever else went wrong: the row above announced a change, so its outcome follows before the error
            // goes on — and nothing of this attempt stays pending in the context.
            try { _db.ChangeTracker.Clear(); } catch (ObjectDisposedException) { /* the scope is gone with it */ }
            await RefusedAsync(account, "failed");
            throw;
        }
        if (!saved.Succeeded)
        {
            _db.ChangeTracker.Clear();
            // The row above announced a change that did not happen: its outcome follows.
            return await RefusedAsync(account, "conflict");
        }
        _logger?.LogInformation("Local account {Email} set a new password with a recovery code", user.Email);
        return LocalAccountResult.Ok();
    }

    private void BurnHash(string code) =>
        _users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, code);

    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<AppUser>().HashPassword(new AppUser(), RecoveryCodes.Generate()));

    /// <summary>The one answer for every refused redemption; the audit row says why (never the code).</summary>
    private async Task<LocalAccountResult> RefusedAsync(string? account, string reason)
    {
        await _audit.TryLogAsync(string.IsNullOrWhiteSpace(account) ? "anonymous" : account, "web", RecoveryRefusedAction,
            AuthAuditLog.Target, account ?? "", account ?? "", $"reason={reason}", success: false);
        return LocalAccountResult.Refused(LocalAccountErrors.InvalidCode);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>The caller as an Admin who may change accounts: refused while authentication is disabled.</summary>
    private async Task<(string? Email, string? Error)> AccountAdminAsync() =>
        _authDisabled ? (null, LocalAccountErrors.AuthDisabled) : await AdminAsync();

    /// <summary>The caller as an Admin, with the session checked now.</summary>
    private async Task<(string? Email, string? Error)> AdminAsync()
    {
        var session = await _user.CheckSessionAsync();
        if (!session.Valid) return (null, LocalAccountErrors.SessionInvalid);
        if (session.Role < AppRole.Admin) return (null, LocalAccountErrors.NotAuthorized);
        return (await _user.GetEmailAsync(), null);
    }

    /// <summary>The signed-in caller, with the session checked now.</summary>
    private async Task<(string Email, string? Error)> SelfAsync()
    {
        var session = await _user.CheckSessionAsync();
        if (!session.Valid) return ("", LocalAccountErrors.SessionInvalid);
        var email = await _user.GetEmailAsync();
        return string.IsNullOrWhiteSpace(email) ? ("", LocalAccountErrors.NotFound) : (email, null);
    }

    private static LocalAccountResult Policy(IdentityResult result) =>
        new(false, LocalAccountErrors.PasswordPolicy) { PolicyErrors = result.Errors.Select(e => e.Code).Distinct().ToList() };

    private static bool SameEmail(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsEmail(string email) =>
        email.Length is > 2 and <= 256
        && System.Net.Mail.MailAddress.TryCreate(email, out var address)
        && string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
}
