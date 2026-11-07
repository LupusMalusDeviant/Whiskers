using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-49 (A-11, TA-24): the local-account lifecycle. A user changes their own password; an Admin adds, resets and
/// deletes accounts; the last Admin who can sign in cannot be deleted, also when two Admins race; every credential
/// change ends the account's sessions through the real R1-26 validator; recovery needs an Admin or the host (CLI) and
/// its code is never logged, audited or stored readable. Everything runs on a real Identity store (SQLite), the real
/// roles/whitelist stores, the real session validator and the real Mutation Gateway.
/// </summary>
[Collection("WebAppBoot")] // the wire test boots the app and needs the process-wide WHISKERS_DATA_DIR
public sealed class LocalAccountLifecycleTests
{
    private const string Ops = "ops@local.test";
    private const string Viewer = "viewer@local.test";
    private const string Operator = "operator@local.test";

    // ------------------------------------------------------------------------------------------------ own password

    [Fact]
    public async Task A_user_changes_their_own_password_and_every_session_of_the_account_ends()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Viewer, AppRole.Viewer);
        var before = await world.SessionOfAsync(Viewer);
        var other = await world.SessionOfAsync(Viewer);   // a second browser
        Assert.True((await world.Validator.CheckAsync(before)).Valid);

        using var self = world.As(before);
        var result = await self.Mutations.RunAsync("account.password_change", new { account = Viewer },
            ct => self.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, LocalAccountWorld.NewPassword, ct));

        Assert.True(result.Success, result.ToString());
        // A Viewer may change their own password: the read-level capability ran through the gateway and was recorded.
        Assert.Contains("begin:account.password_change", world.Sink.Events);
        Assert.Contains("complete:Executed", world.Sink.Events);
        // Every cookie issued before ends on its next request (R1-26), the current one included.
        Assert.Equal(SessionRevocationReasons.StampMismatch, (await world.Validator.CheckAsync(before)).Reason);
        Assert.Equal(SessionRevocationReasons.StampMismatch, (await world.Validator.CheckAsync(other)).Reason);
        Assert.False(await world.PasswordWorksAsync(Viewer, LocalAccountWorld.Password));
        Assert.True(await world.PasswordWorksAsync(Viewer, LocalAccountWorld.NewPassword));
        Assert.True((await world.Validator.CheckAsync(await world.SessionOfAsync(Viewer))).Valid);
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused_and_counts_towards_the_lockout()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Viewer, AppRole.Viewer);
        var session = await world.SessionOfAsync(Viewer);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var self = world.As(session);
            var wrong = await self.Accounts.ChangeOwnPasswordAsync("Not-The-Password-1", LocalAccountWorld.NewPassword);
            Assert.Equal(LocalAccountErrors.WrongPassword, wrong.Error);
        }
        using (var self = world.As(session))
        {
            // Locked now (INV-10's five attempts): even the right password changes nothing until the lockout ends.
            var locked = await self.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, LocalAccountWorld.NewPassword);
            Assert.Equal(LocalAccountErrors.LockedOut, locked.Error);
        }
        Assert.True(await world.PasswordWorksAsync(Viewer, LocalAccountWorld.Password));
        Assert.True((await world.Validator.CheckAsync(session)).Valid);   // nothing changed, the session goes on
    }

    [Fact]
    public async Task A_new_password_must_meet_the_identity_policy()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Viewer, AppRole.Viewer);
        var session = await world.SessionOfAsync(Viewer);
        using var self = world.As(session);

        var weak = await self.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, "short1A!");

        Assert.Equal(LocalAccountErrors.PasswordPolicy, weak.Error);
        Assert.Contains("PasswordTooShort", weak.PolicyErrors);
        Assert.True(await world.PasswordWorksAsync(Viewer, LocalAccountWorld.Password));
        Assert.True((await world.Validator.CheckAsync(session)).Valid);
    }

    // ------------------------------------------------------------------------------------------------ admin reset

    [Fact]
    public async Task An_admin_reset_ends_the_sessions_removes_the_password_and_its_code_sets_a_new_one_exactly_once()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Operator);
        var opsSession = await world.SessionOfAsync(Ops);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));

        var reset = await admin.Mutations.RunAsync("account.password_reset", new { account = Ops },
            ct => admin.Accounts.ResetPasswordAsync(Ops, ct));

        Assert.True(reset.Success, reset.ToString());
        var code = Assert.IsType<string>(reset.RecoveryCode);
        Assert.Matches("^([2-9A-HJ-NP-Z]{4}-){5}[2-9A-HJ-NP-Z]{4}$", code);
        Assert.Equal(SessionRevocationReasons.StampMismatch, (await world.Validator.CheckAsync(opsSession)).Reason);
        Assert.False(await world.PasswordWorksAsync(Ops, LocalAccountWorld.Password));   // the old password is gone
        Assert.Equal(LocalAccountState.RecoveryPending, (await admin.Accounts.ListAsync()).Single(a => a.Email == Ops).State);

        using var anonymous = world.As(LocalAccountWorld.Anonymous);
        // The code cannot be guessed around: a wrong one is refused and counts towards the lockout.
        var wrong = await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, RecoveryCodes.Generate(), LocalAccountWorld.NewPassword);
        Assert.Equal(LocalAccountErrors.InvalidCode, wrong.Error);
        Assert.Equal(1, await world.FailedCountAsync(Ops));

        // Lower case and without dashes is the same code.
        var redeemed = await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code.Replace("-", "").ToLowerInvariant(),
            LocalAccountWorld.NewPassword);
        Assert.True(redeemed.Success, redeemed.ToString());
        Assert.True(await world.PasswordWorksAsync(Ops, LocalAccountWorld.NewPassword));
        Assert.Equal(0, await world.FailedCountAsync(Ops));
        Assert.True((await world.Validator.CheckAsync(await world.SessionOfAsync(Ops))).Valid);

        // Used up: the same code sets nothing a second time.
        var again = await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code, "Another-Password-56");
        Assert.Equal(LocalAccountErrors.InvalidCode, again.Error);
        Assert.True(await world.PasswordWorksAsync(Ops, LocalAccountWorld.NewPassword));
        Assert.Contains(world.Audit.Rows, r => r.Action == LocalAccountService.RecoveryRedeemedAction && r.Actor == Ops);
        Assert.Contains(world.Audit.Rows, r => r.Action == LocalAccountService.RecoveryRefusedAction && !r.Success);
    }

    [Fact]
    public async Task A_recovery_code_expires_and_a_policy_failure_keeps_it_usable()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
        await using var world = await LocalAccountWorld.CreateAsync(time: clock);
        await world.AddUserAsync(Ops, AppRole.Operator);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        var code = (await admin.Accounts.ResetPasswordAsync(Ops)).RecoveryCode!;
        using var anonymous = world.As(LocalAccountWorld.Anonymous);

        var weak = await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code, "weak");
        Assert.Equal(LocalAccountErrors.PasswordPolicy, weak.Error);

        clock.Advance(RecoveryCodes.Lifetime + TimeSpan.FromSeconds(1));
        var late = await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword);
        Assert.Equal(LocalAccountErrors.InvalidCode, late.Error);
        Assert.False(await world.PasswordWorksAsync(Ops, LocalAccountWorld.NewPassword));
    }

    // ------------------------------------------------------------------------------------------------ add, delete

    [Fact]
    public async Task An_admin_adds_a_local_user_who_sets_their_own_password_with_the_invitation_and_signs_in()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));

        var created = await admin.Mutations.RunAsync("account.create", new { account = "new@local.test", role = "Operator" },
            ct => admin.Accounts.CreateAsync("new@local.test", AppRole.Operator, ct));

        Assert.True(created.Success, created.ToString());
        Assert.Contains("begin:account.create", world.Sink.Events);
        Assert.True(world.Roles.HasExplicitRole("new@local.test"));
        Assert.Equal(AppRole.Operator, world.Roles.GetRole("new@local.test"));
        Assert.False(await world.PasswordWorksAsync("new@local.test", ""));   // no password until the owner sets one

        using var anonymous = world.As(LocalAccountWorld.Anonymous);
        Assert.True((await anonymous.Accounts.RedeemRecoveryCodeAsync("new@local.test", created.RecoveryCode!,
            LocalAccountWorld.NewPassword)).Success);
        var session = await world.SessionOfAsync("new@local.test");
        Assert.Equal(SessionCheck.Allowed(AppRole.Operator), await world.Validator.CheckAsync(session));

        // A second account with the same email, or an email holding another role, is refused.
        Assert.Equal(LocalAccountErrors.AlreadyExists,
            (await admin.Accounts.CreateAsync("NEW@local.test", AppRole.Operator)).Error);
        await world.Roles.SetRoleAsync("fed@local.test", AppRole.Viewer);
        Assert.Equal(LocalAccountErrors.RoleConflict, (await admin.Accounts.CreateAsync("fed@local.test", AppRole.Admin)).Error);
        Assert.Equal(AppRole.Viewer, world.Roles.GetRole("fed@local.test"));
        Assert.Equal(LocalAccountErrors.InvalidEmail, (await admin.Accounts.CreateAsync("not an email", AppRole.Viewer)).Error);
    }

    [Fact]
    public async Task Deleting_an_account_ends_its_sessions_and_removes_its_role()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Admin);
        var opsSession = await world.SessionOfAsync(Ops);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));

        var deleted = await admin.Mutations.RunAsync("account.delete", new { account = Ops },
            ct => admin.Accounts.DeleteAsync(Ops, ct));

        Assert.True(deleted.Success, deleted.ToString());
        Assert.Equal(SessionRevocationReasons.AccountDeleted, (await world.Validator.CheckAsync(opsSession)).Reason);
        Assert.False(world.Roles.HasExplicitRole(Ops));
        Assert.DoesNotContain(await admin.Accounts.ListAsync(), a => a.Email == Ops);
        // Nobody deletes their own account here; another Admin does.
        Assert.Equal(LocalAccountErrors.OwnAccount, (await admin.Accounts.DeleteAsync(LocalAccountWorld.Root)).Error);
        Assert.Equal(LocalAccountErrors.OwnAccount, (await admin.Accounts.ResetPasswordAsync(LocalAccountWorld.Root)).Error);
    }

    [Fact]
    public async Task The_last_admin_who_can_sign_in_cannot_be_deleted_and_the_last_explicit_admin_cannot_be_demoted()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        // Admin by role, but nothing to sign in with: an email without a local account (local sign-in only) and an
        // Admin still waiting for a recovery code.
        await world.Roles.SetRoleAsync("gone@local.test", AppRole.Admin);
        await world.AddUserAsync("pending@local.test", AppRole.Admin, password: null);
        using var stranded = world.As(await world.SessionOfAsync("pending@local.test"));
        Assert.Equal(AppRole.Admin, await stranded.User.GetRoleAsync());

        var refused = await stranded.Mutations.RunAsync("account.delete", new { account = LocalAccountWorld.Root },
            ct => stranded.Accounts.DeleteAsync(LocalAccountWorld.Root, ct));

        Assert.Equal(LocalAccountErrors.LastAdmin, refused.Error);
        Assert.True(await world.PasswordWorksAsync(LocalAccountWorld.Root, LocalAccountWorld.Password));
        Assert.Contains("complete:Failed", world.Sink.Events);   // a refusal the service reports is not "Executed"

        // Demoting or removing the last explicit Admin is refused by the role store (TA-02).
        await using var lone = await LocalAccountWorld.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => lone.Roles.SetRoleAsync(LocalAccountWorld.Root, AppRole.Operator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lone.Roles.RemoveRoleAsync(LocalAccountWorld.Root));

        // R1-27 (contract 0003, rule 6): once the Google/OIDC Admin has an active identity binding there is another
        // Admin who can sign in. A federated session alone (it always has a binding) is no longer "seen signing in".
        await world.BoundFederatedAsync("gone@local.test");
        Assert.True((await stranded.Accounts.DeleteAsync(LocalAccountWorld.Root)).Success);
    }

    [Fact]
    public async Task A_binding_removal_and_a_local_deletion_at_the_same_moment_leave_an_admin_who_can_sign_in()
    {
        // R1-27 review: local Admin A (password) and federated-only Admin B (binding). A removes B's binding while B deletes
        // A's account; each check alone sees the other's way in place. Forced interleaving: B's deletion runs while A's
        // removal stands between its last-Admin check and its commit (given up to two seconds to finish there).
        await using var world = await LocalAccountWorld.CreateAsync();
        const string fed = "fed@local.test";
        await world.Roles.SetRoleAsync(fed, AppRole.Admin);
        var bSession = await world.BoundFederatedAsync(fed);
        var bindingId = bSession.FindFirstValue(AuthConstants.BindingIdClaim)!;
        using var b = world.As(bSession);
        Task<LocalAccountResult>? deletion = null;
        world.Bindings.BeforeRemoveCommitForTests = async () =>
        {
            using (ExecutionContext.SuppressFlow())
                deletion = Task.Run(() => b.Accounts.DeleteAsync(LocalAccountWorld.Root));
            await Task.WhenAny(deletion, Task.Delay(TimeSpan.FromSeconds(2)));
        };

        var principal = new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(LocalAccountWorld.Root),
            LocalAccountWorld.Root, McpPermissionLevels.Admin);
        IdentityBindingResult removed;
        using (MutationScope.Begin("identity.binding_remove", "test-" + Guid.NewGuid().ToString("N"), principal))
            removed = await world.Bindings.RemoveAsync(bindingId);
        var deleted = await deletion!;

        Assert.True(removed.Success, removed.ToString());
        Assert.False(deleted.Success, deleted.ToString());
        Assert.True(await world.PasswordWorksAsync(LocalAccountWorld.Root, LocalAccountWorld.Password));
    }

    [Fact]
    public async Task Sign_in_identities_are_confirmed_and_removed_through_the_gateway_by_an_admin_only()
    {
        // R1-27: the panel's path end to end — the UI mutation runner, the gateway's decision, the store.
        await using var world = await LocalAccountWorld.CreateAsync();
        const string person = "person@local.test";
        await world.Roles.SetRoleAsync(person, AppRole.Operator);
        await world.AddUserAsync("op@local.test", AppRole.Operator);
        var admission = new FederatedAdmission(world.Bindings, world.Roles, world.Whitelist, world.Configuration);
        var pendingDecision = await admission.AdmitAsync(new FederatedSignInClaims("oidc", "https://idp.local.test/", "o-person",
            person, "true", EmailIsStandardClaim: true));   // OIDC is not trusted by default: pending
        Assert.Equal(AuthAuditReasons.PendingConfirmation, pendingDecision.Reason);
        var request = Assert.Single(world.Bindings.Snapshot().Pending).Id;

        using var operatorActing = world.As(await world.SessionOfAsync("op@local.test"));
        await Assert.ThrowsAsync<MutationRefusedException>(() => operatorActing.Mutations.RunAsync("identity.binding_confirm",
            new { binding = request }, ct => world.Bindings.ConfirmAsync(request, ct)));
        Assert.Empty(world.Bindings.Snapshot().Bindings);

        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        var confirmed = await admin.Mutations.RunAsync("identity.binding_confirm", new { binding = request },
            ct => world.Bindings.ConfirmAsync(request, ct));
        Assert.True(confirmed.Success, confirmed.ToString());
        var binding = Assert.Single(world.Bindings.Snapshot().Bindings);
        Assert.Equal(PrincipalIds.User(LocalAccountWorld.Root), binding.BoundBy);
        Assert.True((await admission.AdmitAsync(new FederatedSignInClaims("oidc", "https://idp.local.test/", "o-person",
            person, null, EmailIsStandardClaim: true))).Admitted);

        await Assert.ThrowsAsync<MutationRefusedException>(() => operatorActing.Mutations.RunAsync("identity.binding_remove",
            new { binding = binding.Id }, ct => world.Bindings.RemoveAsync(binding.Id, ct)));
        Assert.Single(world.Bindings.Snapshot().Bindings);
        Assert.True((await admin.Mutations.RunAsync("identity.binding_remove", new { binding = binding.Id },
            ct => world.Bindings.RemoveAsync(binding.Id, ct))).Success);
        Assert.Empty(world.Bindings.Snapshot().Bindings);
        Assert.Contains("complete:Executed", world.Sink.Events);
    }

    [Fact]
    public async Task An_admin_with_a_password_but_no_binding_counts_too()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync("pending@local.test", AppRole.Admin, password: null);
        using var pending = world.As(await world.SessionOfAsync("pending@local.test"));
        Assert.Equal(LocalAccountErrors.LastAdmin, (await pending.Accounts.DeleteAsync(LocalAccountWorld.Root)).Error);

        // Once the pending Admin has a password there is another Admin who can sign in.
        var code = (await world.As(await world.SessionOfAsync(LocalAccountWorld.Root)).Accounts
            .ResetPasswordAsync("pending@local.test")).RecoveryCode!;
        Assert.True((await world.As(LocalAccountWorld.Anonymous).Accounts
            .RedeemRecoveryCodeAsync("pending@local.test", code, LocalAccountWorld.NewPassword)).Success);
        using var signedIn = world.As(await world.SessionOfAsync("pending@local.test"));   // the new stamp
        Assert.True((await signedIn.Accounts.DeleteAsync(LocalAccountWorld.Root)).Success);
    }

    [Fact]
    public async Task Two_admins_deleting_each_other_at_the_same_moment_leave_an_admin()
    {
        {
            await using var world = await LocalAccountWorld.CreateAsync(seedRoot: false);
            await world.AddUserAsync("a@local.test", AppRole.Admin);
            await world.AddUserAsync("b@local.test", AppRole.Admin);
            // The worst interleaving, forced: the last-Admin check of each deletion waits (up to two seconds) for the
            // other one to reach the same check. Without serialisation both would pass it before either deletes.
            using var barrier = new Barrier(2);
            var roles = new RendezvousRoles(world.Roles, barrier);
            using var a = world.As(await world.SessionOfAsync("a@local.test"), roles);
            using var b = world.As(await world.SessionOfAsync("b@local.test"), roles);
            using var start = new ManualResetEventSlim();

            var aDeletesB = Task.Run(() => { start.Wait(); return a.Accounts.DeleteAsync("b@local.test"); });
            var bDeletesA = Task.Run(() => { start.Wait(); return b.Accounts.DeleteAsync("a@local.test"); });
            start.Set();
            var results = await Task.WhenAll(aDeletesB, bDeletesA);

            Assert.Equal(1, results.Count(r => r.Success));
            Assert.Contains(results, r => r.Error is LocalAccountErrors.SessionInvalid or LocalAccountErrors.LastAdmin);
            var admins = world.Roles.GetRoleData().Roles.Where(r => r.Role == AppRole.Admin).Select(r => r.Email).ToList();
            var survivor = Assert.Single(admins);
            Assert.True(await world.PasswordWorksAsync(survivor, LocalAccountWorld.Password));
        }
    }

    // ------------------------------------------------------------------------------------------------ RBAC

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task Viewers_and_operators_cannot_add_reset_or_delete_accounts(AppRole role)
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Operator);
        var caller = role == AppRole.Viewer ? Viewer : Operator;
        await world.AddUserAsync(caller, role);
        using var actor = world.As(await world.SessionOfAsync(caller));

        await Assert.ThrowsAsync<MutationRefusedException>(() => actor.Mutations.RunAsync("account.create",
            new { account = "x@local.test", role = "Admin" }, ct => actor.Accounts.CreateAsync("x@local.test", AppRole.Admin, ct)));
        await Assert.ThrowsAsync<MutationRefusedException>(() => actor.Mutations.RunAsync("account.password_reset",
            new { account = Ops }, ct => actor.Accounts.ResetPasswordAsync(Ops, ct)));
        await Assert.ThrowsAsync<MutationRefusedException>(() => actor.Mutations.RunAsync("account.delete",
            new { account = Ops }, ct => actor.Accounts.DeleteAsync(Ops, ct)));
        Assert.Equal(3, world.Sink.Events.Count(e => e.StartsWith("denied:account.", StringComparison.Ordinal)));

        // The service refuses as well, also when called past the gateway.
        Assert.Equal(LocalAccountErrors.NotAuthorized, (await actor.Accounts.CreateAsync("x@local.test", AppRole.Admin)).Error);
        Assert.Equal(LocalAccountErrors.NotAuthorized, (await actor.Accounts.ResetPasswordAsync(Ops)).Error);
        Assert.Equal(LocalAccountErrors.NotAuthorized, (await actor.Accounts.DeleteAsync(Ops)).Error);
        Assert.Empty(await actor.Accounts.ListAsync());

        Assert.Null(await world.FindAsync("x@local.test"));
        Assert.True(await world.PasswordWorksAsync(Ops, LocalAccountWorld.Password));
    }

    [Fact]
    public async Task A_demoted_admin_and_a_revoked_session_are_refused()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Admin);
        await world.AddUserAsync(Viewer, AppRole.Viewer);
        using var ops = world.As(await world.SessionOfAsync(Ops));
        Assert.Single(await world.As(await world.SessionOfAsync(Ops)).Accounts.ListAsync(), a => a.Email == Viewer);

        await world.Roles.SetRoleAsync(Ops, AppRole.Operator);   // the page may still show the Admin buttons
        await Assert.ThrowsAsync<MutationRefusedException>(() => ops.Mutations.RunAsync("account.password_reset",
            new { account = Viewer }, ct => ops.Accounts.ResetPasswordAsync(Viewer, ct)));
        Assert.Equal(LocalAccountErrors.NotAuthorized, (await ops.Accounts.DeleteAsync(Viewer)).Error);

        // A session whose stamp changed (its password was reset elsewhere) decides nothing, not even its own password.
        await world.Roles.SetRoleAsync(Ops, AppRole.Admin);
        using var root = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        Assert.True((await root.Accounts.ResetPasswordAsync(Ops)).Success);
        Assert.Equal(LocalAccountErrors.SessionInvalid, (await ops.Accounts.DeleteAsync(Viewer)).Error);
        Assert.Equal(LocalAccountErrors.SessionInvalid,
            (await ops.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, LocalAccountWorld.NewPassword)).Error);
        await Assert.ThrowsAsync<MutationRefusedException>(() => ops.Mutations.RunAsync("account.delete",
            new { account = Viewer }, ct => ops.Accounts.DeleteAsync(Viewer, ct)));
        Assert.NotNull(await world.FindAsync(Viewer));
    }

    [Fact]
    public async Task The_kill_switch_stops_account_administration_but_not_a_users_own_password_change()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;
        await using var world = await LocalAccountWorld.CreateAsync(policy: policy);
        await world.AddUserAsync(Viewer, AppRole.Viewer);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        using var self = world.As(await world.SessionOfAsync(Viewer));

        await Assert.ThrowsAsync<MutationRefusedException>(() => admin.Mutations.RunAsync("account.create",
            new { account = "x@local.test", role = "Viewer" }, ct => admin.Accounts.CreateAsync("x@local.test", AppRole.Viewer, ct)));
        var changed = await self.Mutations.RunAsync("account.password_change", new { account = Viewer },
            ct => self.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, LocalAccountWorld.NewPassword, ct));
        Assert.True(changed.Success);
    }

    // ------------------------------------------------------------------------------------------------ recovery

    [Fact]
    public async Task Host_recovery_needs_an_existing_account_writes_its_record_first_and_prints_the_code_only_to_the_terminal()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Admin);
        var opsSession = await world.SessionOfAsync(Ops);

        // No such account: nothing is created, no role granted.
        var unknown = new StringWriter();
        Assert.Equal(3, await LocalAccountRecoveryCli.RunAsync(world.Services, ["nobody@local.test"], unknown));
        Assert.Null(await world.FindAsync("nobody@local.test"));
        Assert.Equal(2, await LocalAccountRecoveryCli.RunAsync(world.Services, [], new StringWriter()));

        // No audit row, no change (fail closed).
        world.Audit.Down = true;
        Assert.Equal(1, await LocalAccountRecoveryCli.RunAsync(world.Services, [Ops], new StringWriter()));
        Assert.True(await world.PasswordWorksAsync(Ops, LocalAccountWorld.Password));
        Assert.True((await world.Validator.CheckAsync(opsSession)).Valid);
        world.Audit.Down = false;

        var output = new StringWriter();
        Assert.Equal(0, await LocalAccountRecoveryCli.RunAsync(world.Services,
            LocalAccountRecoveryCli.Arguments([$"{LocalAccountRecoveryCli.Flag}={Ops}"]), output));
        var code = Regex.Match(output.ToString(), "([2-9A-HJ-NP-Z]{4}-){5}[2-9A-HJ-NP-Z]{4}").Value;
        Assert.False(string.IsNullOrEmpty(code));
        var issued = Assert.Single(world.Audit.Rows, r => r.Action == LocalAccountRecoveryCli.IssuedAction && r.Success);
        Assert.Equal("cli (--recover-local-account)", issued.Actor);
        Assert.Equal(SessionRevocationReasons.StampMismatch, (await world.Validator.CheckAsync(opsSession)).Reason);
        Assert.False(await world.PasswordWorksAsync(Ops, LocalAccountWorld.Password));

        using var anonymous = world.As(LocalAccountWorld.Anonymous);
        Assert.True((await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword)).Success);
        Assert.True(await world.PasswordWorksAsync(Ops, LocalAccountWorld.NewPassword));
        Assert.Equal(AppRole.Admin, world.Roles.GetRole(Ops));   // recovery restores access, it grants nothing new
    }

    [Fact]
    public async Task No_password_or_recovery_code_reaches_a_log_the_audit_trail_or_the_database_in_readable_form()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Viewer, AppRole.Viewer);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        using var self = world.As(await world.SessionOfAsync(Viewer));
        using var anonymous = world.As(LocalAccountWorld.Anonymous);
        var secrets = new List<string> { LocalAccountWorld.Password, LocalAccountWorld.NewPassword };

        await self.Mutations.RunAsync("account.password_change", new { account = Viewer },
            ct => self.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, LocalAccountWorld.NewPassword, ct));
        var invited = await admin.Mutations.RunAsync("account.create", new { account = "new@local.test", role = "Viewer" },
            ct => admin.Accounts.CreateAsync("new@local.test", AppRole.Viewer, ct));
        var reset = await admin.Mutations.RunAsync("account.password_reset", new { account = Viewer },
            ct => admin.Accounts.ResetPasswordAsync(Viewer, ct));
        var cli = new StringWriter();
        await LocalAccountRecoveryCli.RunAsync(world.Services, ["new@local.test"], cli);
        var hostCode = Regex.Match(cli.ToString(), "([2-9A-HJ-NP-Z]{4}-){5}[2-9A-HJ-NP-Z]{4}").Value;
        secrets.AddRange([invited.RecoveryCode!, reset.RecoveryCode!, hostCode]);
        await anonymous.Accounts.RedeemRecoveryCodeAsync(Viewer, invited.RecoveryCode!, "Wrong-Code-Holder-7");   // wrong account
        await anonymous.Accounts.RedeemRecoveryCodeAsync(Viewer, reset.RecoveryCode!, "Third-Password-78");
        await anonymous.Accounts.RedeemRecoveryCodeAsync("new@local.test", hostCode, "Fourth-Password-90");
        secrets.AddRange(["Wrong-Code-Holder-7", "Third-Password-78", "Fourth-Password-90"]);
        Assert.False(reset.ToString().Contains(reset.RecoveryCode!, StringComparison.Ordinal));

        var recorded = world.Sink.Text + "\n" + world.Audit.Text + "\n" + string.Join("\n", world.Logs.Lines);
        var database = await world.DatabaseTextAsync();
        foreach (var secret in secrets)
        {
            // Not whole, not a prefix, with or without the dashes.
            foreach (var form in new[] { secret, secret[..8], RecoveryCodes.Normalize(secret)[..8] })
            {
                Assert.DoesNotContain(form, recorded, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(form, database, StringComparison.Ordinal);
            }
        }
        // What was recorded is the account and the role, never more.
        Assert.Contains("new@local.test", world.Sink.Text);
    }

    // ------------------------------------------------------------------------------------------------ review round 1

    [Fact]
    public async Task The_recovery_page_is_limited_per_client_and_per_account_and_audits_a_storm_once()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
        await using var world = await LocalAccountWorld.CreateAsync(time: clock);
        await world.AddUserAsync(Ops, AppRole.Operator);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        var code = (await admin.Accounts.ResetPasswordAsync(Ops)).RecoveryCode!;

        // Per client: ten attempts a minute, whatever they name; the eleventh is refused even with the right code.
        using (var noisy = world.As(LocalAccountWorld.Anonymous, client: "203.0.113.9"))
        {
            for (var i = 0; i < RecoveryAttemptLimiter.ClientLimit; i++)
                Assert.Equal(LocalAccountErrors.InvalidCode,
                    (await noisy.Accounts.RedeemRecoveryCodeAsync($"nobody{i}@local.test", code, LocalAccountWorld.NewPassword)).Error);
            var hashesBefore = world.Hasher.Verifications;
            for (var i = 0; i < 5; i++)
                Assert.Equal(LocalAccountErrors.InvalidCode,
                    (await noisy.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword)).Error);
            Assert.Equal(hashesBefore, world.Hasher.Verifications);   // a throttled attempt checks nothing
        }
        Assert.False(await world.PasswordWorksAsync(Ops, LocalAccountWorld.NewPassword));
        // Five throttled attempts, one audit row.
        Assert.Single(world.Audit.Rows, r => r.Details?.Contains("reason=rate_limited") == true);

        // Another client still gets through.
        using (var other = world.As(LocalAccountWorld.Anonymous, client: "203.0.113.10"))
            Assert.True((await other.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword)).Success);

        // Per account: ten attempts in 15 minutes, from whichever clients; the next one is refused even from a fresh
        // client with the right code. (Ops has no code now, so these tries do not lock it.)
        for (var i = 0; i < RecoveryAttemptLimiter.AccountLimit - 1; i++)   // one was used above
        {
            using var client = world.As(LocalAccountWorld.Anonymous, client: $"192.0.2.{i}");
            await client.Accounts.RedeemRecoveryCodeAsync(Ops, RecoveryCodes.Generate(), "irrelevant-Pass-1");
        }
        Assert.Equal(0, await world.FailedCountAsync(Ops));
        var second = (await admin.Accounts.ResetPasswordAsync(Ops)).RecoveryCode!;
        using (var fresh = world.As(LocalAccountWorld.Anonymous, client: "192.0.2.200"))
        {
            Assert.Equal(LocalAccountErrors.InvalidCode,
                (await fresh.Accounts.RedeemRecoveryCodeAsync(Ops, second, LocalAccountWorld.NewPassword + "x")).Error);
            clock.Advance(RecoveryAttemptLimiter.AccountWindow);
            Assert.True((await fresh.Accounts.RedeemRecoveryCodeAsync(Ops, second, LocalAccountWorld.NewPassword + "x")).Success);
        }
    }

    [Fact]
    public async Task Trying_codes_for_an_account_without_one_does_not_lock_it()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Viewer, AppRole.Viewer);
        for (var i = 0; i < 8; i++)
        {
            using var client = world.As(LocalAccountWorld.Anonymous, client: $"192.0.2.{i}");
            Assert.Equal(LocalAccountErrors.InvalidCode,
                (await client.Accounts.RedeemRecoveryCodeAsync(Viewer, RecoveryCodes.Generate(), LocalAccountWorld.NewPassword)).Error);
        }
        Assert.Equal(0, await world.FailedCountAsync(Viewer));
        using var self = world.As(await world.SessionOfAsync(Viewer));
        Assert.True((await self.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, LocalAccountWorld.NewPassword)).Success);
    }

    [Fact]
    public async Task A_correct_code_is_refused_while_the_account_is_locked_and_costs_the_same_hash_work()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Operator);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        var code = (await admin.Accounts.ResetPasswordAsync(Ops)).RecoveryCode!;
        using var anonymous = world.As(LocalAccountWorld.Anonymous);
        for (var i = 0; i < 5; i++)
            await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, RecoveryCodes.Generate(), LocalAccountWorld.NewPassword);

        var before = world.Hasher.Verifications;
        var locked = await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword);

        Assert.Equal(LocalAccountErrors.InvalidCode, locked.Error);
        Assert.Equal(before + 1, world.Hasher.Verifications);   // as much hashing as an unknown email or a wrong code
        Assert.False(await world.PasswordWorksAsync(Ops, LocalAccountWorld.NewPassword));
        Assert.Contains(world.Audit.Rows, r => r.Details?.Contains("reason=locked_out") == true);
    }

    [Fact]
    public async Task One_code_redeemed_twice_at_the_same_moment_sets_one_password()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Operator);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        var code = (await admin.Accounts.ResetPasswordAsync(Ops)).RecoveryCode!;
        using var first = world.As(LocalAccountWorld.Anonymous, client: "192.0.2.1");
        using var second = world.As(LocalAccountWorld.Anonymous, client: "192.0.2.2");
        using var start = new ManualResetEventSlim();

        var one = Task.Run(() => { start.Wait(); return first.Accounts.RedeemRecoveryCodeAsync(Ops, code, "First-Password-12"); });
        var two = Task.Run(() => { start.Wait(); return second.Accounts.RedeemRecoveryCodeAsync(Ops, code, "Second-Password-34"); });
        start.Set();
        var results = await Task.WhenAll(one, two);

        Assert.Equal(1, results.Count(r => r.Success));
        var winner = results[0].Success ? "First-Password-12" : "Second-Password-34";
        Assert.True(await world.PasswordWorksAsync(Ops, winner));
        Assert.False(await world.PasswordWorksAsync(Ops, winner == "First-Password-12" ? "Second-Password-34" : "First-Password-12"));
    }

    [Fact]
    public async Task A_redemption_that_cannot_be_audited_sets_nothing()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Operator);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        var code = (await admin.Accounts.ResetPasswordAsync(Ops)).RecoveryCode!;
        using var anonymous = world.As(LocalAccountWorld.Anonymous);

        world.Audit.Down = true;
        Assert.Equal(LocalAccountErrors.StoreUnavailable,
            (await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword)).Error);
        Assert.False(await world.PasswordWorksAsync(Ops, LocalAccountWorld.NewPassword));

        world.Audit.Down = false;   // the code is still good
        Assert.True((await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword)).Success);
        Assert.Single(world.Audit.Rows, r => r.Action == LocalAccountService.RecoveryRedeemedAction);
    }

    [Fact]
    public async Task With_authentication_disabled_no_account_is_added_reset_or_deleted()
    {
        await using var world = await LocalAccountWorld.CreateAsync(authDisabled: true);
        await world.AddUserAsync(Ops, AppRole.Admin);
        var lanAdmin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, "local@localhost"), new Claim(ClaimTypes.Name, "local")], AuthConstants.AuthDisabledScheme));
        using var lan = world.As(lanAdmin);
        Assert.Equal(AppRole.Admin, await lan.User.GetRoleAsync());

        Assert.Equal(LocalAccountErrors.AuthDisabled, (await lan.Accounts.CreateAsync("x@local.test", AppRole.Admin)).Error);
        Assert.Equal(LocalAccountErrors.AuthDisabled, (await lan.Accounts.ResetPasswordAsync(Ops)).Error);
        Assert.Equal(LocalAccountErrors.AuthDisabled, (await lan.Accounts.DeleteAsync(LocalAccountWorld.Root)).Error);
        Assert.Null(await world.FindAsync("x@local.test"));
        Assert.True(await world.PasswordWorksAsync(Ops, LocalAccountWorld.Password));
        Assert.NotNull(await world.FindAsync(LocalAccountWorld.Root));
    }

    [Fact]
    public async Task A_host_recovery_whose_save_throws_records_that_nothing_was_issued()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Admin);
        await world.DropTableAsync("AspNetUserTokens");

        var output = new StringWriter();
        Assert.Equal(1, await LocalAccountRecoveryCli.RunAsync(world.Services, [Ops], output));

        var rows = world.Audit.Rows.Where(r => r.Action == LocalAccountRecoveryCli.IssuedAction).ToList();
        Assert.Equal([true, false], rows.Select(r => r.Success));
        Assert.True(await world.PasswordWorksAsync(Ops, LocalAccountWorld.Password));
    }

    [Fact]
    public async Task An_account_whose_code_cannot_be_issued_is_not_left_half_created()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.DropTableAsync("AspNetUserTokens");
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));

        await Assert.ThrowsAnyAsync<Exception>(() => admin.Accounts.CreateAsync("new@local.test", AppRole.Viewer));

        Assert.Null(await world.FindAsync("new@local.test"));
        Assert.False(world.Roles.HasExplicitRole("new@local.test"));
    }

    [Fact]
    public async Task A_demotion_in_the_role_panel_during_a_deletion_leaves_an_admin_and_the_account()
    {
        await using var world = await LocalAccountWorld.CreateAsync(seedRoot: false);
        await world.AddUserAsync("a@local.test", AppRole.Admin);
        await world.AddUserAsync("b@local.test", AppRole.Admin);
        // A deletes B. Right after A's last-Admin check, B demotes A on the User roles panel (the role store does not
        // take the account lock; B is still an explicit Admin, so the role store allows it).
        var roles = new HookedRoles(world.Roles, () => world.Roles.SetRoleAsync("a@local.test", AppRole.Operator));
        using var a = world.As(await world.SessionOfAsync("a@local.test"), roles);

        var result = await a.Accounts.DeleteAsync("b@local.test");

        Assert.Equal(LocalAccountErrors.LastAdmin, result.Error);
        Assert.NotNull(await world.FindAsync("b@local.test"));          // nothing changed
        Assert.Equal(AppRole.Admin, world.Roles.GetRole("b@local.test"));
        Assert.True(await world.PasswordWorksAsync("b@local.test", LocalAccountWorld.Password));
    }

    /// <summary>The real role store; the first read of the whole role list (the last-Admin check) runs
    /// <paramref name="once"/> right after it.</summary>
    private sealed class HookedRoles(IRoleService inner, Func<Task> once) : IRoleService
    {
        private int _fired;
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public AppRole GetRole(string? email) => inner.GetRole(email);
        public bool HasRole(string? email, AppRole requiredRole) => inner.HasRole(email, requiredRole);
        public bool HasAnyRoles() => inner.HasAnyRoles();
        public bool HasExplicitRole(string? email) => inner.HasExplicitRole(email);
        public UserRoleData GetRoleData()
        {
            var data = inner.GetRoleData();
            if (Interlocked.Exchange(ref _fired, 1) == 0) once().GetAwaiter().GetResult();
            return data;
        }
        public Task SaveRoleDataAsync(UserRoleData data) => inner.SaveRoleDataAsync(data);
        public Task SetRoleAsync(string email, AppRole role) => inner.SetRoleAsync(email, role);
        public Task RemoveRoleAsync(string email) => inner.RemoveRoleAsync(email);
        public Task SetFirstAdminAsync(string email, Whiskers.Services.Auth.AccessControlWriteGate.Lease lease) => inner.SetFirstAdminAsync(email, lease);
    }

    // ------------------------------------------------------------------------------------------------ review round 2

    [Fact]
    public async Task An_admin_change_whose_circuit_closed_while_it_waited_does_not_keep_the_account_lock()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Admin);
        // A circuit closed while its change waited for the lock: its context is disposed when the change gets the lock.
        var gone = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        gone.Dispose();
        // Each one must get the lock (the one before released it) and fail; none may wait forever.
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => Bounded(gone.Accounts.CreateAsync("x@local.test", AppRole.Viewer)));
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => Bounded(gone.Accounts.ResetPasswordAsync(Ops)));
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => Bounded(gone.Accounts.DeleteAsync(Ops)));

        // The next Admin change still gets the lock.
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        Assert.True((await Bounded(admin.Accounts.ResetPasswordAsync(Ops))).Success);

        static async Task<T> Bounded<T>(Task<T> task)
        {
            Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));   // not stuck on the lock
            return await task;
        }
    }

    [Fact]
    public async Task A_redemption_whose_save_throws_records_its_outcome_and_the_code_stays_usable()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Operator);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        var code = (await admin.Accounts.ResetPasswordAsync(Ops)).RecoveryCode!;

        using (var broken = world.As(LocalAccountWorld.Anonymous,
                   users: sp => ActivatorUtilities.CreateInstance<ThrowingStampUsers>(sp)))
            await Assert.ThrowsAsync<IOException>(() =>
                broken.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword));

        // The "redeemed" row is followed by its outcome.
        var rows = world.Audit.Rows.Where(r => r.Action is LocalAccountService.RecoveryRedeemedAction
            or LocalAccountService.RecoveryRefusedAction).ToList();
        Assert.Equal([true, false], rows.Select(r => r.Success));
        Assert.Contains("reason=failed", rows[1].Details);
        Assert.False(await world.PasswordWorksAsync(Ops, LocalAccountWorld.NewPassword));
        using var anonymous = world.As(LocalAccountWorld.Anonymous);
        Assert.True((await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword)).Success);
    }

    [Fact]
    public async Task Only_the_role_stores_last_admin_refusal_reads_as_last_admin()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.AddUserAsync(Ops, AppRole.Operator);
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root),
            new RemoveThrows(world.Roles, new InvalidOperationException("the role file is read-only")));

        // Any other failure of the role store is a failure, not "last Admin" — and nothing was deleted.
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Accounts.DeleteAsync(Ops));
        Assert.NotNull(await world.FindAsync(Ops));
        Assert.Equal(AppRole.Operator, world.Roles.GetRole(Ops));
        // (The role store's own refusal — the target is the one explicit Admin left — stays "last Admin":
        // A_demotion_in_the_role_panel_during_a_deletion_leaves_an_admin_and_the_account.)
    }

    [Fact]
    public async Task With_authentication_disabled_a_host_issued_code_is_not_redeemed()
    {
        await using var world = await LocalAccountWorld.CreateAsync(authDisabled: true);
        await world.AddUserAsync(Ops, AppRole.Operator);
        var output = new StringWriter();
        Assert.Equal(0, await LocalAccountRecoveryCli.RunAsync(world.Services, [Ops], output));
        var code = Regex.Match(output.ToString(), "([2-9A-HJ-NP-Z]{4}-){5}[2-9A-HJ-NP-Z]{4}").Value;
        Assert.NotEmpty(code);
        var hashes = world.Hasher.Verifications;

        using var anonymous = world.As(LocalAccountWorld.Anonymous);
        Assert.Equal(LocalAccountErrors.AuthDisabled,
            (await anonymous.Accounts.RedeemRecoveryCodeAsync(Ops, code, LocalAccountWorld.NewPassword)).Error);

        Assert.Equal(hashes, world.Hasher.Verifications);
        Assert.False(await world.PasswordWorksAsync(Ops, LocalAccountWorld.NewPassword));
        Assert.DoesNotContain(world.Audit.Rows, r => r.Action == LocalAccountService.RecoveryRedeemedAction);
    }

    [Fact]
    public void A_client_is_its_ipv4_address_or_its_ipv6_64_and_anything_else_is_unknown()
    {
        Assert.Equal("203.0.113.5", RecoveryAttemptLimiter.ClientKey("203.0.113.5"));
        Assert.Equal("203.0.113.5", RecoveryAttemptLimiter.ClientKey("::ffff:203.0.113.5"));
        Assert.Equal(RecoveryAttemptLimiter.ClientKey("2001:db8:1:2::1"), RecoveryAttemptLimiter.ClientKey("2001:db8:1:2:ffff:ee:dd:9"));
        Assert.NotEqual(RecoveryAttemptLimiter.ClientKey("2001:db8:1:2::1"), RecoveryAttemptLimiter.ClientKey("2001:db8:1:3::1"));
        Assert.Equal(RecoveryAttemptLimiter.UnknownClient, RecoveryAttemptLimiter.ClientKey(null));
        Assert.Equal(RecoveryAttemptLimiter.UnknownClient, RecoveryAttemptLimiter.ClientKey("not-an-address"));
    }

    [Fact]
    public void Rotating_ipv6_addresses_within_one_64_shares_one_client_budget()
    {
        var limiter = new RecoveryAttemptLimiter(new ManualClock(DateTimeOffset.Parse("2026-11-03T10:00:00Z")));
        for (var i = 0; i < RecoveryAttemptLimiter.ClientLimit; i++)
            Assert.True(limiter.TryAcquire($"2001:db8:1:2::{i + 1:x}", $"a{i}@local.test").Allowed);

        Assert.False(limiter.TryAcquire("2001:db8:1:2:abcd::77", "fresh@local.test").Allowed);
        Assert.True(limiter.TryAcquire("2001:db8:1:3::1", "fresh@local.test").Allowed);
    }

    [Fact]
    public void A_full_limiter_puts_new_clients_and_accounts_into_one_overflow_bucket_and_sweeps_once_per_window()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
        var limiter = new RecoveryAttemptLimiter(clock, capacity: 4);
        for (var i = 0; i < 4; i++) Assert.True(limiter.TryAcquire($"192.0.2.{i}", $"a{i}@local.test").Allowed);

        // Table full: every new client shares one bucket of ten, however many addresses it rotates through.
        for (var i = 0; i < RecoveryAttemptLimiter.ClientLimit; i++)
            Assert.True(limiter.TryAcquire($"198.51.100.{i}", $"a{i % 4}@local.test").Allowed);
        Assert.False(limiter.TryAcquire("198.51.100.200", "a0@local.test").Allowed);
        Assert.True(limiter.TryAcquire("192.0.2.1", "a1@local.test").Allowed);   // a known client keeps its own
        var footprint = limiter.Footprint;
        Assert.Equal(5, footprint.Clients);                // four clients and the overflow bucket, no more
        Assert.Equal(1, footprint.ClientSweeps);           // one sweep in the window, not one per new client

        // New accounts too: they share one account budget.
        for (var i = 0; i < RecoveryAttemptLimiter.AccountLimit; i++)
            limiter.TryAcquire("192.0.2.3", $"new{i}@local.test");
        Assert.True(limiter.Footprint.Accounts <= 5);

        // After the window the expired keys go and a new client has its own budget again.
        clock.Advance(RecoveryAttemptLimiter.AccountWindow);
        Assert.True(limiter.TryAcquire("198.51.100.250", "late@local.test").Allowed);
        Assert.True(limiter.Footprint.Clients <= 4);
    }

    /// <summary>Identity's user manager whose final save fails with something other than a database conflict.</summary>
    private sealed class ThrowingStampUsers(IUserStore<AppUser> store, Microsoft.Extensions.Options.IOptions<IdentityOptions> options,
        IPasswordHasher<AppUser> hasher, IEnumerable<IUserValidator<AppUser>> userValidators,
        IEnumerable<IPasswordValidator<AppUser>> passwordValidators, ILookupNormalizer normalizer, IdentityErrorDescriber errors,
        IServiceProvider services, ILogger<UserManager<AppUser>> logger)
        : UserManager<AppUser>(store, options, hasher, userValidators, passwordValidators, normalizer, errors, services, logger)
    {
        public override Task<IdentityResult> UpdateSecurityStampAsync(AppUser user) => throw new IOException("the disk is gone");
    }

    /// <summary>The real role store, except that removing a role fails with <paramref name="failure"/>.</summary>
    private sealed class RemoveThrows(IRoleService inner, Exception failure) : IRoleService
    {
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public AppRole GetRole(string? email) => inner.GetRole(email);
        public bool HasRole(string? email, AppRole requiredRole) => inner.HasRole(email, requiredRole);
        public bool HasAnyRoles() => inner.HasAnyRoles();
        public bool HasExplicitRole(string? email) => inner.HasExplicitRole(email);
        public UserRoleData GetRoleData() => inner.GetRoleData();
        public Task SaveRoleDataAsync(UserRoleData data) => inner.SaveRoleDataAsync(data);
        public Task SetRoleAsync(string email, AppRole role) => inner.SetRoleAsync(email, role);
        public Task RemoveRoleAsync(string email) => Task.FromException(failure);
        public Task SetFirstAdminAsync(string email, Whiskers.Services.Auth.AccessControlWriteGate.Lease lease) => inner.SetFirstAdminAsync(email, lease);
    }

    // ------------------------------------------------------------------------------------------------ wire

    [Fact]
    public async Task On_the_running_app_a_host_recovery_signs_the_old_cookie_out_and_the_new_password_signs_in()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-r149-boot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var pwFile = Path.Combine(dataDir, "admin-pw.txt");
        await File.WriteAllTextAsync(pwFile, LocalAccountWorld.Password);
        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
        var config = new List<KeyValuePair<string, string?>>
        {
            new("Auth:Disabled", "false"),
            new("WHISKERS_ADMIN_EMAIL", LocalAccountWorld.Root),
            new("WHISKERS_ADMIN_PASSWORD_FILE", pwFile),
        };
        config.AddRange(BootSettings.OnlyCore());
        try
        {
            using var bootSettings = BootSettings.Apply(config);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SignInAsync(client, LocalAccountWorld.Root, LocalAccountWorld.Password);
            Assert.Equal(HttpStatusCode.NotFound, (await AdminProbeAsync(client)).StatusCode);

            // The app resolves the lifecycle service (DI wiring, behind the gateway's scope guard because its writes are
            // marked), and the host recovery ends the cookie at once.
            using (var scope = factory.Services.CreateScope())
                Assert.IsAssignableFrom<ILocalAccountService>(scope.ServiceProvider.GetRequiredService<ILocalAccountService>());
            var output = new StringWriter();
            Assert.Equal(0, await LocalAccountRecoveryCli.RunAsync(factory.Services, [LocalAccountWorld.Root], output));
            var code = Regex.Match(output.ToString(), "([2-9A-HJ-NP-Z]{4}-){5}[2-9A-HJ-NP-Z]{4}").Value;
            var probe = await AdminProbeAsync(client);
            Assert.Equal(HttpStatusCode.Redirect, probe.StatusCode);
            Assert.Contains("/login", probe.Headers.Location!.OriginalString);

            using (var scope = factory.Services.CreateScope())
            {
                var accounts = scope.ServiceProvider.GetRequiredService<ILocalAccountService>();
                Assert.True((await accounts.RedeemRecoveryCodeAsync(LocalAccountWorld.Root, code, LocalAccountWorld.NewPassword)).Success);
            }
            using var fresh = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SignInAsync(fresh, LocalAccountWorld.Root, LocalAccountWorld.NewPassword);
            Assert.Equal(HttpStatusCode.NotFound, (await AdminProbeAsync(fresh)).StatusCode);
            // The recovery page is reachable without a session.
            var page = new HttpRequestMessage(HttpMethod.Get, "/account/recover");
            page.Headers.Accept.ParseAdd("text/html");
            Assert.Equal(HttpStatusCode.OK, (await fresh.SendAsync(page)).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>The real role store; reading the whole role list (what the last-Admin check does) first waits for the
    /// other participant of <paramref name="barrier"/>, at most two seconds.</summary>
    private sealed class RendezvousRoles(IRoleService inner, Barrier barrier) : IRoleService
    {
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public AppRole GetRole(string? email) => inner.GetRole(email);
        public bool HasRole(string? email, AppRole requiredRole) => inner.HasRole(email, requiredRole);
        public bool HasAnyRoles() => inner.HasAnyRoles();
        public bool HasExplicitRole(string? email) => inner.HasExplicitRole(email);
        public UserRoleData GetRoleData()
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(2));
            return inner.GetRoleData();
        }
        public Task SaveRoleDataAsync(UserRoleData data) => inner.SaveRoleDataAsync(data);
        public Task SetRoleAsync(string email, AppRole role) => inner.SetRoleAsync(email, role);
        public Task RemoveRoleAsync(string email) => inner.RemoveRoleAsync(email);
        public Task SetFirstAdminAsync(string email, Whiskers.Services.Auth.AccessControlWriteGate.Lease lease) => inner.SetFirstAdminAsync(email, lease);
    }

    private static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/login-local", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = password, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("error", response.Headers.Location!.OriginalString);
    }

    private static Task<HttpResponseMessage> AdminProbeAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/backups/self/none/download");
        request.Headers.Accept.ParseAdd("text/html");
        return client.SendAsync(request);
    }
}

/// <summary>A real local-account world: Identity on SQLite with the production password and lockout options, roles,
/// whitelist, owner accounts, the R1-26 session validator and the Mutation Gateway, plus recorders for everything that
/// could carry a secret (gateway audit, audit log, log lines).</summary>
internal sealed class LocalAccountWorld : IAsyncDisposable
{
    public const string Root = "root@local.test";
    public const string Password = "CorrectHorse12!!";
    public const string NewPassword = "Battery-Staple-34";

    public static ClaimsPrincipal Anonymous { get; } = new(new ClaimsIdentity());

    private readonly string _dir;
    private readonly ServiceProvider _sp;
    private readonly TimeProvider _time;
    public IServiceProvider Services => _sp;
    public RoleService Roles { get; }
    public WhitelistService Whitelist { get; }
    public SessionValidator Validator { get; }
    public IdentityBindingStore Bindings { get; }
    public OwnerAccounts Owners { get; }
    public RecordingAuditLog Audit { get; }
    public RecordingSink Sink { get; } = new();
    public NotificationDeliveryTests.ListLoggerProvider Logs { get; }
    public MutationGateway Gateway { get; }
    public IConfiguration Configuration { get; }
    public RecoveryAttemptLimiter Limiter { get; }
    public CountingHasher Hasher { get; }
    /// <summary>The guardrail policy the gateway, the account service and the binding store read (contract 0009).</summary>
    public IGuardrailStore Guardrails { get; }

    private LocalAccountWorld(string dir, ServiceProvider sp, RoleService roles, WhitelistService whitelist,
        IConfiguration configuration, DataPathOptions paths, RecordingAuditLog audit,
        NotificationDeliveryTests.ListLoggerProvider logs, GuardrailPolicy policy, TimeProvider time, CountingHasher hasher,
        IGuardrailStore? guardrails = null)
    {
        Configuration = configuration;
        Hasher = hasher;
        Limiter = new RecoveryAttemptLimiter(time);
        _dir = dir;
        _sp = sp;
        _time = time;
        Roles = roles;
        Whitelist = whitelist;
        Audit = audit;
        Logs = logs;
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        // R1-27: the identity bindings a federated session is checked against (roles and whitelist read lazily).
        var store = guardrails ?? new AgentToolTestHelpers.FixedGuardrailStore(policy);
        Guardrails = store;
        Bindings = new IdentityBindingStore(new BindingServices(roles, whitelist, sp, store), audit, configuration,
            dataPaths: paths);
        Validator = new SessionValidator(scopes, whitelist, roles, configuration, bindings: Bindings);
        Owners = new OwnerAccounts(scopes, configuration, paths);
        Gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), Sink, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), store));
    }

    public static async Task<LocalAccountWorld> CreateAsync(bool seedRoot = true, GuardrailPolicy? policy = null,
        TimeProvider? time = null, bool authDisabled = false, IGuardrailStore? guardrails = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sw-r149-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var paths = new DataPathOptions(dir);
        var settings = new Dictionary<string, string?>();
        if (seedRoot) settings["WHISKERS_ADMIN_EMAIL"] = Root;
        if (authDisabled) settings["Auth:Disabled"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var logs = new NotificationDeliveryTests.ListLoggerProvider();
        var audit = new RecordingAuditLog();
        var hasher = new CountingHasher();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<IPasswordHasher<AppUser>>(hasher);   // Identity takes it (TryAdd)
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dir, "id.db")}"));
        // The production options (WhiskersAuthenticationExtensions): 12 characters and Identity's character classes,
        // five failures lock for 15 minutes.
        services.AddIdentityCore<AppUser>(o =>
            {
                o.Password.RequiredLength = 12;
                o.User.RequireUniqueEmail = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<WhiskersIdentityDbContext>();
        services.AddSingleton<IAuditLogService>(audit);
        if (time is not null) services.AddSingleton(time);
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.EnsureCreatedAsync();

        var roles = new RoleService(configuration, NullLogger<RoleService>.Instance, dataPaths: paths);
        await roles.InitializeAsync();
        var whitelist = new WhitelistService(configuration, roles, NullLogger<WhitelistService>.Instance, paths);
        await whitelist.InitializeAsync();
        var world = new LocalAccountWorld(dir, sp, roles, whitelist, configuration, paths, audit, logs,
            policy ?? GuardrailPolicy.SafeDefault(), time ?? TimeProvider.System, hasher, guardrails);
        if (seedRoot) await world.AddUserAsync(Root, role: null);   // the seeded Admin (role from WHISKERS_ADMIN_EMAIL)
        return world;
    }

    /// <summary>A local account; <paramref name="password"/> <c>null</c> = none yet (waiting for a recovery code).</summary>
    public async Task AddUserAsync(string email, AppRole? role, string? password = Password)
    {
        using (var scope = _sp.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
            var user = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Code)));
            if (password is null) Assert.True((await RecoveryCodes.IssueAsync(users, db, user, _time.GetUtcNow(), default)).Result.Succeeded);
        }
        if (role is { } r) await Roles.SetRoleAsync(email, r);
        Owners.RememberLocalAccounts();
    }

    /// <summary>The principal <c>/login-local</c> would put into a cookie for <paramref name="email"/> now.</summary>
    public async Task<ClaimsPrincipal> SessionOfAsync(string email)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
        var row = await db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == email.ToUpperInvariant());
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, row.Email!), new Claim(ClaimTypes.Name, row.Email!),
            new Claim(ClaimTypes.NameIdentifier, row.Id), new Claim(AuthConstants.SecurityStampClaim, row.SecurityStamp!),
        ], AuthConstants.LocalPasswordScheme));
    }

    /// <summary>R1-27: a Google sign-in of <paramref name="email"/> (a configured entry) bound by the admission, and the
    /// cookie principal the gate issues for it.</summary>
    public async Task<ClaimsPrincipal> BoundFederatedAsync(string email)
    {
        var admission = new FederatedAdmission(Bindings, Roles, Whitelist, Configuration);
        var decision = await admission.AdmitAsync(new FederatedSignInClaims("google", AuthConstants.GoogleIssuer,
            "g-" + email, email, "true", EmailIsStandardClaim: true));
        Assert.True(decision.Admitted, decision.Reason);
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, decision.Account!), new Claim(ClaimTypes.Name, decision.Account!),
            new Claim(AuthConstants.BindingIdClaim, decision.BindingId!),
        ], AuthConstants.FederatedScheme));
    }

    /// <summary>The binding store sees the world's roles and whitelist; everything else (the account database's scopes)
    /// comes from the world's provider.</summary>
    private sealed class BindingServices(IRoleService roles, IWhitelistService whitelist, IServiceProvider app,
        IGuardrailStore guardrails) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IGuardrailStore) ? guardrails
            : serviceType == typeof(IRoleService) ? roles
            : serviceType == typeof(IWhitelistService) ? whitelist
            : app.GetService(serviceType);
    }

    public Acting As(ClaimsPrincipal principal, IRoleService? roles = null, string? client = "198.51.100.7",
        Func<IServiceProvider, UserManager<AppUser>>? users = null)
    {
        var scope = _sp.CreateScope();
        var user = new SessionUser(Validator, principal);
        var accounts = new LocalAccountService(
            users?.Invoke(scope.ServiceProvider) ?? scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
            scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>(), roles ?? Roles, user, Owners, Audit, Limiter, new FixedClient(client), Configuration, _time,
            scope.ServiceProvider.GetRequiredService<ILogger<LocalAccountService>>(), Bindings, Guardrails);
        return new Acting(scope, accounts, new UiMutationRunner(Gateway, new CapabilityCatalog(), user), user);
    }

    public async Task<bool> PasswordWorksAsync(string email, string password)
    {
        using var scope = _sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email);
        return user is not null && await users.CheckPasswordAsync(user, password);
    }

    public async Task<int> FailedCountAsync(string email)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.NormalizedEmail == email.ToUpperInvariant())
            .Select(u => u.AccessFailedCount).SingleAsync();
    }

    public async Task<AppUser?> FindAsync(string email)
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email);
    }

    /// <summary>Every user and token row as text, the way a copy of the database file would show them.</summary>
    public async Task<string> DatabaseTextAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
        var users = await db.Users.AsNoTracking().ToListAsync();
        var tokens = await db.UserTokens.AsNoTracking().ToListAsync();
        var text = new StringBuilder(JsonSerializer.Serialize(users) + JsonSerializer.Serialize(tokens));
        foreach (var file in Directory.GetFiles(_dir, "id.db*"))
        {
            // Shared read: SQLite keeps the file open for writing.
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.Latin1);
            text.Append(await reader.ReadToEndAsync());
        }
        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        TestSqlite.ClearPools(_dir);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp */ }
    }

    internal sealed record Acting(IServiceScope Scope, LocalAccountService Accounts, UiMutationRunner Mutations,
        SessionUser User) : IDisposable
    {
        public void Dispose() => Scope.Dispose();
    }

    /// <summary>Identity's password hasher, counting the verifications (each is one PBKDF2 run).</summary>
    internal sealed class CountingHasher : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _inner = new();
        private int _verifications;
        public int Verifications => Volatile.Read(ref _verifications);
        public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);
        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    internal sealed class FixedClient(string? address) : IClientAddress
    {
        public string? Address => address;
    }

    public async Task DropTableAsync(string table)
    {
        using var scope = _sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database
            .ExecuteSqlRawAsync("DROP TABLE \"" + table + "\"");
    }

    /// <summary>The current user of a circuit, checked through the real session validator on every call (R1-26).</summary>
    internal sealed class SessionUser(ISessionValidator validator, ClaimsPrincipal principal) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult(principal.FindFirstValue(ClaimTypes.Email));
        public Task<SessionCheck> CheckSessionAsync() => validator.CheckAsync(principal);
        public async Task<AppRole> GetRoleAsync() => await CheckSessionAsync() is { Valid: true } s ? s.Role : AppRole.Viewer;
        public async Task<bool> HasRoleAsync(AppRole required) => await GetRoleAsync() >= required;
    }

    /// <summary>Everything the gateway hands to its audit sink, as text.</summary>
    internal sealed class RecordingSink : IMutationAuditSink
    {
        private readonly StringBuilder _text = new();
        public List<string> Events { get; } = [];
        public string Text { get { lock (_text) return _text.ToString(); } }

        private void Add(string evt, string text)
        {
            lock (_text)
            {
                Events.Add(evt);
                _text.AppendLine(text);
            }
        }

        private static string Describe(MutationRequest request) =>
            $"{request.CapabilityId} {request.Principal} {request.Target} {JsonSerializer.Serialize(request.Arguments)}";

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            Add($"begin:{capability.Id}", Describe(request));
            return Task.FromResult((long)Events.Count);
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct)
        {
            Add($"complete:{status}", $"{status} {verification} {reason}");
            return Task.CompletedTask;
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, IReadOnlyList<MutationDomainResult>? results, CancellationToken ct)
        {
            Add($"complete:{status}", $"{status} {verification} {reason} {string.Join(" ", results?.Select(r => r.ToString()) ?? [])}");
            return Task.CompletedTask;
        }

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            Add($"denied:{request.CapabilityId}:{status}", $"{Describe(request)} {reason}");
            return Task.CompletedTask;
        }
    }

    /// <summary>The audit log as rows; <see cref="Down"/> loses every write, as a locked database does.</summary>
    internal sealed class RecordingAuditLog : IAuditLogService
    {
        public sealed record Row(string Actor, string Action, string TargetId, string? Details, bool Success);
        private readonly List<Row> _rows = [];
        public bool Down { get; set; }
        public IReadOnlyList<Row> Rows { get { lock (_rows) return [.. _rows]; } }
        public string Text => string.Join("\n", Rows.Select(r => r.ToString()));

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) =>
            TryLogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

        public Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            if (Down) return Task.FromResult(false);
            lock (_rows) _rows.Add(new Row(actor, action, targetId, $"{targetName} {details}", success));
            return Task.FromResult(true);
        }

        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }
}
