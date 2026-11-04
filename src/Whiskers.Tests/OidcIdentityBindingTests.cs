using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Startup;

namespace Whiskers.Tests;

/// <summary>
/// R1-27 (INV-05, contract 0003): a Google/OIDC sign-in is admitted on the stable subject — (issuer, subject) — the
/// provider vouches for, not on the email claim. The email binds a new subject once, to a configured address, and only
/// when a trusted provider verified it; a second subject or issuer with the same address is someone else until a person
/// confirms it. Local and federated sessions with one address are checked by different stores.
/// </summary>
public sealed class OidcIdentityBindingTests
{
    internal const string Root = "root@corp.test";
    internal const string Ops = "ops@corp.test";
    internal const string OidcIssuer = "https://auth.corp.test/application/o/whiskers/";
    internal const string OtherIssuer = "https://other-idp.test/";

    internal static FederatedSignInClaims Google(string subject, string? email, string? verified = "true") =>
        new("google", AuthConstants.GoogleIssuer, subject, email, verified, EmailIsStandardClaim: true);

    internal static FederatedSignInClaims Oidc(string subject, string? email, string? verified = "true",
        string issuer = OidcIssuer, bool standardClaim = true) =>
        new("oidc", issuer, subject, email, verified, standardClaim);

    // ------------------------------------------------------------------------------------------------ identity

    [Fact]
    public async Task Two_subjects_or_two_issuers_with_one_address_are_two_identities()
    {
        await using var world = await BindingWorld.CreateAsync(trustOidc: true);
        var first = await world.Admission.AdmitAsync(Google("g-ops-1", Ops));
        Assert.True(first.Admitted);
        Assert.True(first.BoundNow);

        // A second Google subject with the same verified address: not admitted on the first one's binding.
        var second = await world.Admission.AdmitAsync(Google("g-ops-2", Ops));
        Assert.False(second.Admitted);
        Assert.Equal(AuthAuditReasons.PendingConfirmation, second.Reason);
        // A second issuer (trusted, verified) with the same address: pending too (decision 4 a).
        var otherIssuer = await world.Admission.AdmitAsync(Oidc("o-ops", Ops));
        Assert.False(otherIssuer.Admitted);

        var snapshot = world.Store.Snapshot();
        var binding = Assert.Single(snapshot.Bindings);
        Assert.Equal(first.BindingId, binding.Id);
        Assert.Equal(AuthConstants.GoogleIssuer, binding.Issuer);
        Assert.Equal([PendingIdentityReasons.SecondIssuer, PendingIdentityReasons.SubjectChanged],
            snapshot.Pending.Select(p => p.Reason).Order(StringComparer.Ordinal));
        Assert.Null(world.Store.Find(AuthConstants.GoogleIssuer, "g-ops-2"));
        Assert.Null(world.Store.Find(OidcIssuer, "o-ops"));
        // The first subject keeps signing in.
        Assert.True((await world.Admission.AdmitAsync(Google("g-ops-1", Ops))).Admitted);
    }

    [Fact]
    public async Task A_bound_subject_keeps_its_account_when_the_idp_address_changes()
    {
        await using var world = await BindingWorld.CreateAsync();
        Assert.True((await world.Admission.AdmitAsync(Google("g-ops", Ops))).Admitted);

        var renamed = await world.Admission.AdmitAsync(Google("g-ops", "someone.else@corp.test", verified: "false"));
        Assert.True(renamed.Admitted);
        Assert.Equal(Ops, renamed.Account);
        // Not even the address of another configured account takes the subject elsewhere.
        var hijack = await world.Admission.AdmitAsync(Google("g-ops", Root));
        Assert.Equal(Ops, hijack.Account);
    }

    [Fact]
    public async Task A_local_and_a_federated_session_with_one_address_are_validated_by_different_stores()
    {
        await using var world = await BindingWorld.CreateAsync();
        await world.AddLocalUserAsync(Ops);
        var local = await world.LocalSessionAsync(Ops);
        var federated = await world.SignInAsync(Google("g-ops", Ops));
        Assert.True((await world.Validator.CheckAsync(local)).Valid);
        Assert.True((await world.Validator.CheckAsync(federated)).Valid);

        // Removing the binding ends the federated session only.
        var bindingId = federated.FindFirstValue(AuthConstants.BindingIdClaim)!;
        Assert.True((await world.AsAdminAsync("identity.binding_remove", () => world.Store.RemoveAsync(bindingId))).Success);
        Assert.Equal(SessionRevocationReasons.BindingRevoked, (await world.Validator.CheckAsync(federated)).Reason);
        Assert.True((await world.Validator.CheckAsync(local)).Valid);

        // A new stamp ends the local session only.
        var again = await world.SignInAsync(Google("g-ops", Ops));
        await world.ChangeStampAsync(Ops);
        Assert.Equal(SessionRevocationReasons.StampMismatch, (await world.Validator.CheckAsync(local)).Reason);
        Assert.True((await world.Validator.CheckAsync(again)).Valid);
        // A local cookie never passes on a binding, a federated one never on an Identity row.
        Assert.Equal(SessionRevocationReasons.BindingMissing, (await world.Validator.CheckAsync(new ClaimsPrincipal(
            new ClaimsIdentity(local.Claims, AuthConstants.FederatedScheme)))).Reason);
    }

    [Fact]
    public async Task A_recycled_address_after_prune_does_not_revive_the_old_subject()
    {
        await using var world = await BindingWorld.CreateAsync();
        var old = await world.SignInAsync(Google("g-old-owner", Ops));
        Assert.True((await world.Validator.CheckAsync(old)).Valid);

        // The address stops being an entry: its binding goes before the role write.
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.RemoveRoleAsync(Ops));
        Assert.Empty(world.Store.Snapshot().Bindings);
        Assert.False((await world.Validator.CheckAsync(old)).Valid);

        // The address is configured again (for someone else): the old session stays ended, and the old subject from a
        // provider that cannot vouch for it is someone new, waiting for confirmation.
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync(Ops, AppRole.Operator));
        Assert.Equal(SessionRevocationReasons.BindingRevoked, (await world.Validator.CheckAsync(old)).Reason);
        var oldSubject = await world.Admission.AdmitAsync(Oidc("g-old-owner", Ops));
        Assert.False(oldSubject.Admitted);
        Assert.Empty(world.Store.Snapshot().Bindings);
    }

    [Fact]
    public async Task Bindings_survive_a_restart()
    {
        await using var world = await BindingWorld.CreateAsync();
        var session = await world.SignInAsync(Google("g-ops", Ops));

        var restarted = world.NewStore();
        await restarted.InitializeAsync();
        Assert.True(restarted.IsAvailable);
        var binding = restarted.Find(AuthConstants.GoogleIssuer, "g-ops");
        Assert.NotNull(binding);
        Assert.Equal(Ops, binding.Account);
        Assert.Equal(session.FindFirstValue(AuthConstants.BindingIdClaim), binding.Id);
        var validator = new SessionValidator(world.Scopes, world.Whitelist, world.Roles, world.Configuration, bindings: restarted);
        Assert.True((await validator.CheckAsync(session)).Valid);
    }

    [Fact]
    public async Task The_issued_principal_carries_the_handle_not_the_idp_address()
    {
        await using var world = await BindingWorld.CreateAsync();
        // The entry as the administrator configured it; the IdP sends it in another case and with its own profile claims.
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync("Dev.Lead@Corp.Test", AppRole.Operator));
        var http = world.Http();
        var context = Ticket(http, "Google", new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "g-lead"), new Claim(ClaimTypes.Email, "dev.lead@corp.test"),
            new Claim(AuthConstants.EmailVerifiedClaim, "True"), new Claim(ClaimTypes.Name, "Dev Lead"),
            new Claim("urn:google:picture", "https://example.test/p.png"),
        ], "Google"));

        await FederatedSignIn.WhitelistGateAsync(context, "");

        Assert.Null(context.Result);
        var issued = context.Principal!;
        Assert.Equal(AuthConstants.FederatedScheme, issued.Identity!.AuthenticationType);
        Assert.Equal("Dev.Lead@Corp.Test", issued.FindFirstValue(ClaimTypes.Email));
        Assert.Equal("Dev.Lead@Corp.Test", issued.Identity.Name);
        Assert.Equal([ClaimTypes.Email, ClaimTypes.Name, AuthConstants.BindingIdClaim],
            issued.Claims.Select(c => c.Type).Order(StringComparer.Ordinal).ToArray().Order(StringComparer.Ordinal));
        Assert.Equal(AppRole.Operator, (await world.Validator.CheckAsync(issued)).Role);
    }

    // ------------------------------------------------------------------------------------------------ unverified email

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(" true ")]
    [InlineData("true\n")]
    [InlineData("\ttrue")]
    public async Task An_unverified_address_binds_nothing_and_admits_nobody(string? verified)
    {
        await using var world = await BindingWorld.CreateAsync();
        var entry = await world.Admission.AdmitAsync(Google("g-ops", Ops, verified));
        var stranger = await world.Admission.AdmitAsync(Google("g-x", "stranger@elsewhere.test", verified));

        Assert.False(entry.Admitted);
        Assert.Equal(AuthAuditReasons.PendingConfirmation, entry.Reason);
        Assert.Equal(Ops, entry.Account);
        Assert.False(stranger.Admitted);
        Assert.Equal(AuthAuditReasons.NotWhitelisted, stranger.Reason);
        Assert.Null(stranger.Account);   // recorded as anonymous
        var snapshot = world.Store.Snapshot();
        Assert.Empty(snapshot.Bindings);
        var pending = Assert.Single(snapshot.Pending);   // the entry only: a stranger leaves no record
        Assert.Equal(Ops, pending.Account);
        Assert.Equal(PendingIdentityReasons.EmailUnverified, pending.Reason);
        Assert.False(pending.EmailVerifiedAsserted);
        Assert.DoesNotContain(world.Audit.Rows, r => r.Action == AuthAuditLog.IdentityBoundAction);
    }

    [Fact]
    public async Task An_untrusted_provider_or_a_nonstandard_email_claim_binds_nothing_even_when_verified()
    {
        await using var world = await BindingWorld.CreateAsync();   // OIDC not trusted (decision 3 b)
        var untrusted = await world.Admission.AdmitAsync(Oidc("o-ops", Ops));
        Assert.False(untrusted.Admitted);
        Assert.Equal(PendingIdentityReasons.ProviderUntrusted, Assert.Single(world.Store.Snapshot().Pending).Reason);
        Assert.True(world.Store.Snapshot().Pending[0].EmailVerifiedAsserted);

        await using var trusting = await BindingWorld.CreateAsync(trustOidc: true);
        var nonstandard = await trusting.Admission.AdmitAsync(Oidc("o-ops", Ops, standardClaim: false));
        Assert.False(nonstandard.Admitted);
        Assert.Equal(PendingIdentityReasons.NonstandardEmailClaim, Assert.Single(trusting.Store.Snapshot().Pending).Reason);
        Assert.Empty(trusting.Store.Snapshot().Bindings);
        // The same, trusted and standard: bound.
        Assert.True((await trusting.Admission.AdmitAsync(Oidc("o-root", Root))).Admitted);
    }

    [Fact]
    public async Task The_configured_handlers_map_email_verified_to_the_claim_the_admission_reads()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Disabled"] = "false",
            ["GoogleAuth:ClientId"] = "google-client", ["GoogleAuth:ClientSecret"] = "test-only",
            ["Oidc:Enabled"] = "true", ["Oidc:Authority"] = "https://auth.corp.test/", ["Oidc:ClientId"] = "whiskers",
        });
        builder.AddWhiskersAuthentication();
        await using var sp = builder.Services.BuildServiceProvider();

        using var userinfo = JsonDocument.Parse("""{"sub":"s-1","email":"ops@corp.test","email_verified":true}""");
        using var denied = JsonDocument.Parse("""{"sub":"s-1","email":"ops@corp.test","email_verified":false}""");

        var google = sp.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get(GoogleDefaults.AuthenticationScheme);
        var googleUser = Run(google.ClaimActions, userinfo.RootElement, "Google");
        var googleClaims = FederatedSignIn.ClaimsOf(googleUser, "google", builder.Configuration);
        Assert.Equal("true", googleClaims.EmailVerified, ignoreCase: true);
        Assert.Equal("s-1", googleClaims.Subject);
        Assert.Equal(AuthConstants.GoogleIssuer, googleClaims.Issuer);
        Assert.False(IsTrue(FederatedSignIn.ClaimsOf(Run(google.ClaimActions, denied.RootElement, "Google"), "google",
            builder.Configuration).EmailVerified));

        var oidc = sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("oidc");
        var oidcUser = Run(oidc.ClaimActions, userinfo.RootElement, "oidc");
        // The ID token's subject, as the handler maps it inbound, with the token's issuer.
        ((ClaimsIdentity)oidcUser.Identity!).AddClaim(new Claim(ClaimTypes.NameIdentifier, "s-1", ClaimValueTypes.String, OidcIssuer));
        var oidcClaims = FederatedSignIn.ClaimsOf(oidcUser, "oidc", builder.Configuration);
        Assert.True(IsTrue(oidcClaims.EmailVerified));
        Assert.Equal(OidcIssuer, oidcClaims.Issuer);
        Assert.True(oidcClaims.EmailIsStandardClaim);
        Assert.False(IsTrue(FederatedSignIn.ClaimsOf(Run(oidc.ClaimActions, denied.RootElement, "oidc"), "oidc",
            builder.Configuration).EmailVerified));
        // Exactly "true" (any case), nothing trimmed: a padded value is passed on as it is and is not verified.
        foreach (var padded in new[] { " true ", "true\n", "\ttrue", "TRUE " })
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "s-1"),
                new Claim(AuthConstants.EmailVerifiedClaim, padded)], "Google"));
            var extracted = FederatedSignIn.ClaimsOf(user, "google", builder.Configuration).EmailVerified;
            Assert.Equal(padded, extracted);
            Assert.False(IdentityBindingStore.IsTrue(extracted));
        }
        Assert.True(IdentityBindingStore.IsTrue("True"));

        static ClaimsPrincipal Run(Microsoft.AspNetCore.Authentication.OAuth.Claims.ClaimActionCollection actions,
            JsonElement user, string type)
        {
            var identity = new ClaimsIdentity(type);
            foreach (var action in actions) action.Run(user, identity, type);
            return new ClaimsPrincipal(identity);
        }
        static bool IsTrue(string? v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------------ setup and migration

    [Fact]
    public async Task An_instance_without_an_admin_admits_no_federated_sign_in_and_ends_federated_sessions()
    {
        await using var world = await BindingWorld.CreateAsync(seedAdmin: false);
        var refused = await world.Admission.AdmitAsync(Google("g-ops", Ops));
        Assert.False(refused.Admitted);
        Assert.Equal(AuthAuditReasons.SetupIncomplete, refused.Reason);
        Assert.Empty(world.Store.Snapshot().Pending);
        var session = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, Ops),
            new Claim(AuthConstants.BindingIdClaim, "b_whatever")], AuthConstants.FederatedScheme));
        Assert.Equal(SessionRevocationReasons.SetupIncomplete, (await world.Validator.CheckAsync(session)).Reason);
    }

    [Fact]
    public async Task Existing_entries_bind_only_through_a_verified_trusted_sign_in_with_the_audit_row_first()
    {
        await using var world = await BindingWorld.CreateAsync();
        var bindingsFile = new DataPathOptions(world.Directory).IdentityBindingsJson;
        world.Audit.OnWrite = () => Assert.False(File.Exists(bindingsFile), "the binding was written before its audit row");

        var admitted = await world.Admission.AdmitAsync(Google("g-ops", Ops));

        Assert.True(admitted.Admitted);
        var row = Assert.Single(world.Audit.Rows, r => r.Action == AuthAuditLog.IdentityBoundAction);
        Assert.Equal(Ops, row.Actor);
        Assert.True(row.Success);
        Assert.Contains("method=verified_email", row.Details);
        Assert.DoesNotContain("g-ops", row.Details);   // the subject is not recorded, its fingerprint is
        Assert.True(File.Exists(bindingsFile));
    }

    [Fact]
    public async Task Without_its_audit_row_nothing_is_bound_and_the_sign_in_is_refused()
    {
        await using var world = await BindingWorld.CreateAsync();
        world.Audit.Down = true;

        var refused = await world.Admission.AdmitAsync(Google("g-ops", Ops));

        Assert.False(refused.Admitted);
        Assert.Equal(AuthAuditReasons.AuditUnavailable, refused.Reason);
        Assert.Empty(world.Store.Snapshot().Bindings);
        Assert.False(File.Exists(new DataPathOptions(world.Directory).IdentityBindingsJson));
    }

    [Fact]
    public async Task A_failed_save_refuses_the_sign_in_and_publishes_nothing()
    {
        await using var world = await BindingWorld.CreateAsync();
        var file = new DataPathOptions(world.Directory).IdentityBindingsJson;
        // The temporary file the durable write starts with cannot be created: a directory holds its name.
        world.BlockSaves();

        var refused = await world.Admission.AdmitAsync(Google("g-ops", Ops));

        Assert.False(refused.Admitted);
        Assert.Equal(AuthAuditReasons.StoreUnavailable, refused.Reason);
        // The announced binding's outcome follows its row.
        Assert.Contains(world.Audit.Rows, r => r.Action == AuthAuditLog.IdentityBoundAction && !r.Success);
        Assert.False(File.Exists(file));
        // A failed save leaves the store unavailable (federated paths refuse, readiness Degraded) until it is read and
        // pruned again.
        Assert.False(world.Store.IsAvailable);
        Assert.Throws<IdentityBindingStoreUnavailableException>(() => world.Store.Snapshot());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            (await new Whiskers.HealthChecks.IdentityBindingsReadyCheck(world.Store).CheckHealthAsync(new())).Status);

        world.Store.PersistForTests = null;
        Assert.True(await world.Store.RefreshAsync());
        Assert.Empty(world.Store.Snapshot().Bindings);
        Assert.Null(world.Store.Find(AuthConstants.GoogleIssuer, "g-ops"));
    }

    [Fact]
    public async Task A_failed_pending_save_is_refused_as_store_unavailable_and_records_no_request()
    {
        await using var world = await BindingWorld.CreateAsync();   // OIDC untrusted: the sign-in would be pending
        world.BlockSaves();

        var refused = await world.Admission.AdmitAsync(Oidc("o-ops", Ops));

        Assert.False(refused.Admitted);
        Assert.Equal(AuthAuditReasons.StoreUnavailable, refused.Reason);
        Assert.DoesNotContain(world.Audit.Rows, r => r.Action == AuthAuditLog.IdentityPendingAction);
        world.Store.PersistForTests = null;
        Assert.True(await world.Store.RefreshAsync());
        Assert.Empty(world.Store.Snapshot().Pending);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("directory")]
    public async Task An_unreadable_store_refuses_every_federated_path_and_leaves_local_sign_in_alone(string damage)
    {
        await using var world = await BindingWorld.CreateAsync();
        await world.AddLocalUserAsync(Ops);
        var federated = await world.SignInAsync(Google("g-ops", Ops));
        var local = await world.LocalSessionAsync(Ops);
        var file = new DataPathOptions(world.Directory).IdentityBindingsJson;
        File.Delete(file);
        if (damage == "directory" && File.Exists(file + ".bak")) File.Delete(file + ".bak");
        if (damage == "corrupt") await File.WriteAllTextAsync(file, "{ not json");
        else Directory.CreateDirectory(file);

        var store = world.NewStore();
        await store.InitializeAsync();   // the start goes on
        Assert.False(store.IsAvailable);
        var admission = new FederatedAdmission(store, world.Roles, world.Whitelist, world.Configuration);
        var validator = new SessionValidator(world.Scopes, world.Whitelist, world.Roles, world.Configuration, bindings: store);

        Assert.Equal(AuthAuditReasons.StoreUnavailable, (await admission.AdmitAsync(Google("g-ops", Ops))).Reason);
        Assert.Equal(AuthAuditReasons.StoreUnavailable, (await admission.AdmitAsync(Google("g-new", Root))).Reason);
        Assert.Equal(SessionRevocationReasons.StoreUnavailable, (await validator.CheckAsync(federated)).Reason);
        Assert.True((await validator.CheckAsync(local)).Valid);
        var health = await new Whiskers.HealthChecks.IdentityBindingsReadyCheck(store).CheckHealthAsync(new());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, health.Status);
        // The running instance follows the file: it refuses as well, without a restart.
        Assert.False(world.Store.IsAvailable);
        Assert.Equal(SessionRevocationReasons.StoreUnavailable, (await world.Validator.CheckAsync(federated)).Reason);
        // A role or whitelist write that removes an entry cannot prune an unreadable store: it is refused.
        var roles = new RoleService(world.Configuration, NullLogger<RoleService>.Instance, dataPaths: new DataPathOptions(world.Directory),
            writeGate: world.Gate, bindings: store);
        await roles.InitializeAsync();
        await Assert.ThrowsAsync<IdentityBindingStoreUnavailableException>(() =>
            GatewayArrange.RunAsync("config.roles", () => roles.RemoveRoleAsync(Ops)));
        Assert.True(roles.HasExplicitRole(Ops));
    }

    [Theory]
    [InlineData("Google")]
    [InlineData("oidc")]
    [InlineData("Cookies")]
    public async Task A_cookie_issued_before_r1_27_is_ended_as_binding_missing(string type)
    {
        await using var world = await BindingWorld.CreateAsync();
        var old = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, Ops), new Claim(ClaimTypes.Name, Ops)], type));
        Assert.Equal(SessionRevocationReasons.BindingMissing, (await world.Validator.CheckAsync(old)).Reason);
        var unbound = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, Ops)], AuthConstants.FederatedScheme));
        Assert.Equal(SessionRevocationReasons.BindingMissing, (await world.Validator.CheckAsync(unbound)).Reason);
    }

    [Fact]
    public async Task A_binding_whose_account_differs_from_the_cookie_is_revoked()
    {
        await using var world = await BindingWorld.CreateAsync();
        var session = await world.SignInAsync(Google("g-ops", Ops));
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, Root),
            new Claim(AuthConstants.BindingIdClaim, session.FindFirstValue(AuthConstants.BindingIdClaim)!)], AuthConstants.FederatedScheme));
        Assert.Equal(SessionRevocationReasons.BindingRevoked, (await world.Validator.CheckAsync(forged)).Reason);
    }

    // ------------------------------------------------------------------------------------------------ confirm / remove / CLI

    [Fact]
    public async Task An_admin_confirms_a_pending_request_through_the_gateway_and_a_new_subject_replaces_the_old()
    {
        await using var world = await BindingWorld.CreateAsync();
        var first = await world.SignInAsync(Google("g-ops-1", Ops));
        await world.Admission.AdmitAsync(Google("g-ops-2", Ops));   // pending subject_changed
        var request = Assert.Single(world.Store.Snapshot().Pending);

        // Outside a decision the scope guard of the app refuses; without an Admin principal the store refuses.
        Assert.Equal(IdentityBindingErrors.SessionInvalid,
            (await GatewayArrange.RunAsync("identity.binding_confirm", () => world.Store.ConfirmAsync(request.Id))).Error);
        Assert.Equal(IdentityBindingErrors.NotAuthorized,
            (await world.AsAsync("identity.binding_confirm", McpPermissionLevels.Write, () => world.Store.ConfirmAsync(request.Id))).Error);

        var confirmed = await world.AsAdminAsync("identity.binding_confirm", () => world.Store.ConfirmAsync(request.Id));
        Assert.True(confirmed.Success);
        var binding = Assert.Single(world.Store.Snapshot().Bindings);
        Assert.Equal(IdentityBindingMethods.Admin, binding.Method);
        Assert.Equal(PrincipalIds.User(Root), binding.BoundBy);
        Assert.Equal(Ops, world.Store.Find(AuthConstants.GoogleIssuer, "g-ops-2")!.Account);
        Assert.Null(world.Store.Find(AuthConstants.GoogleIssuer, "g-ops-1"));
        Assert.Equal(SessionRevocationReasons.BindingRevoked, (await world.Validator.CheckAsync(first)).Reason);
        Assert.Empty(world.Store.Snapshot().Pending);
        Assert.True((await world.Admission.AdmitAsync(Google("g-ops-2", Ops))).Admitted);
    }

    [Fact]
    public async Task The_last_way_the_last_admin_can_sign_in_is_not_removed()
    {
        await using var world = await BindingWorld.CreateAsync();   // Root is the only Admin, without a local account
        var root = await world.SignInAsync(Google("g-root", Root));
        var id = root.FindFirstValue(AuthConstants.BindingIdClaim)!;

        Assert.Equal(IdentityBindingErrors.LastAdmin,
            (await world.AsAdminAsync("identity.binding_remove", () => world.Store.RemoveAsync(id))).Error);
        Assert.Single(world.Store.Snapshot().Bindings);

        // With a local password the binding is not the last way any more.
        await world.AddLocalUserAsync(Root);
        Assert.True((await world.AsAdminAsync("identity.binding_remove", () => world.Store.RemoveAsync(id))).Success);
        Assert.Empty(world.Store.Snapshot().Bindings);
    }

    [Fact]
    public async Task With_authentication_disabled_the_panel_writes_are_refused()
    {
        await using var world = await BindingWorld.CreateAsync(authDisabled: true);
        Assert.Equal(IdentityBindingErrors.AuthDisabled,
            (await world.AsAdminAsync("identity.binding_remove", () => world.Store.RemoveAsync("b_x"))).Error);
        Assert.Equal(IdentityBindingErrors.AuthDisabled,
            (await world.AsAdminAsync("identity.binding_confirm", () => world.Store.ConfirmAsync("p_x"))).Error);
    }

    [Fact]
    public async Task The_host_cli_confirms_with_its_row_first_and_answers_with_its_exit_codes()
    {
        await using var world = await BindingWorld.CreateAsync();
        var sp = world.CliServices();
        var output = new StringWriter();

        Assert.Equal(2, await IdentityBindingCli.RunAsync(sp, [], output));
        Assert.Equal(2, await IdentityBindingCli.RunAsync(sp, [Ops, "--issuer"], output));
        Assert.Equal(2, await IdentityBindingCli.RunAsync(sp, [Ops, Root], output));
        Assert.Equal(3, await IdentityBindingCli.RunAsync(sp, [Ops], output));

        // Two issuers wait: ambiguous, one line per request without address or subject.
        await world.Admission.AdmitAsync(Oidc("o-ops", Ops));
        await world.Admission.AdmitAsync(Oidc("o2-ops", Ops, issuer: OtherIssuer));
        var ambiguous = new StringWriter();
        Assert.Equal(4, await IdentityBindingCli.RunAsync(sp, [Ops], ambiguous));
        Assert.Contains(OidcIssuer, ambiguous.ToString());
        Assert.Contains(OtherIssuer, ambiguous.ToString());
        Assert.DoesNotContain(Ops, ambiguous.ToString());
        Assert.DoesNotContain("o-ops", ambiguous.ToString());

        // No audit row, no binding.
        world.Audit.Down = true;
        Assert.Equal(1, await IdentityBindingCli.RunAsync(sp, IdentityBindingCli.Arguments([Ops, $"--issuer={OidcIssuer}"]), output));
        Assert.Empty(world.Store.Snapshot().Bindings);
        world.Audit.Down = false;

        var bindingsFile = new DataPathOptions(world.Directory).IdentityBindingsJson;
        var before = await File.ReadAllTextAsync(bindingsFile);
        world.Audit.OnWrite = () => Assert.Equal(before, File.ReadAllText(bindingsFile));
        var done = new StringWriter();
        Assert.Equal(0, await IdentityBindingCli.RunAsync(sp,
            IdentityBindingCli.Arguments([$"{IdentityBindingCli.Flag}={Ops}", "--issuer", OidcIssuer]), done));
        world.Audit.OnWrite = null;
        var row = Assert.Single(world.Audit.Rows, r => r.Action == AuthAuditLog.IdentityBoundAction && r.Success);
        Assert.Equal(IdentityBindingStore.CliActor, row.Actor);
        Assert.Contains("method=host_cli", row.Details);

        // The CLI's own store (its own process: its own gate) wrote the file; the running instance sees it without a restart
        // and the person signs in now.
        var bound = world.Store.Find(OidcIssuer, "o-ops");
        Assert.NotNull(bound);
        Assert.Equal(IdentityBindingMethods.HostCli, bound.Method);
        Assert.Equal("cli", bound.BoundBy);
        var admitted = await world.Admission.AdmitAsync(Oidc("o-ops", Ops));
        Assert.True(admitted.Admitted);
        Assert.Equal(bound.Id, admitted.BindingId);
    }

    [Fact]
    public async Task The_host_cli_and_the_running_instance_share_the_file_without_overwriting_each_other()
    {
        await using var world = await BindingWorld.CreateAsync();   // OIDC untrusted
        var googleOps = await world.SignInAsync(Google("g-ops", Ops));
        await world.Admission.AdmitAsync(Oidc("o-root", Root));      // pending provider_untrusted
        await world.Admission.AdmitAsync(Oidc("o-ops", Ops));        // pending second_issuer

        // The CLI's process: its own store and gate on the same data directory, loaded now.
        var cli = world.NewProcessStore();
        await cli.InitializeAsync();
        Assert.NotNull(cli.Find(AuthConstants.GoogleIssuer, "g-ops"));

        // The running instance revokes; the CLI then confirms from its earlier view — it must not write that view back.
        var googleId = googleOps.FindFirstValue(AuthConstants.BindingIdClaim)!;
        Assert.True((await world.AsAdminAsync("identity.binding_remove", () => world.Store.RemoveAsync(googleId))).Success);
        Assert.True((await cli.ConfirmFromHostAsync(Root, null)).Success);

        // Both directions, no restart: the live instance sees the CLI's binding, the revocation stands for both, the
        // other request is still there.
        Assert.Equal(Root, world.Store.Find(OidcIssuer, "o-root")?.Account);
        Assert.True((await world.Admission.AdmitAsync(Oidc("o-root", Root))).Admitted);
        Assert.Null(world.Store.Find(AuthConstants.GoogleIssuer, "g-ops"));
        Assert.Null(cli.Find(AuthConstants.GoogleIssuer, "g-ops"));
        Assert.Equal(SessionRevocationReasons.BindingRevoked, (await world.Validator.CheckAsync(googleOps)).Reason);
        Assert.Contains(world.Store.Snapshot().Pending, p => p.Account == Ops && p.Issuer == OidcIssuer);

        // The other way round: the CLI confirms Ops; the live instance's next write starts from that, not from its cache.
        Assert.True((await cli.ConfirmFromHostAsync(Ops, OidcIssuer)).Success);
        await world.Admission.AdmitAsync(Google("g-ops-new", Ops));   // live write: a pending request for Google
        Assert.Equal(Ops, world.Store.Find(OidcIssuer, "o-ops")?.Account);
        Assert.Equal(Ops, cli.Find(OidcIssuer, "o-ops")?.Account);
        Assert.Contains(cli.Snapshot().Pending, p => p.Account == Ops && p.Issuer == AuthConstants.GoogleIssuer);
    }

    [Fact]
    public async Task A_writer_waits_for_the_other_process_lock()
    {
        await using var world = await BindingWorld.CreateAsync();
        var lockPath = new DataPathOptions(world.Directory).IdentityBindingsJson + IdentityBindingStore.LockSuffix;
        var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var signIn = world.Admission.AdmitAsync(Google("g-ops", Ops));
        await Task.Delay(300);
        Assert.False(signIn.IsCompleted);   // the other process holds the lock
        await held.DisposeAsync();
        Assert.True((await signIn).Admitted);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    public async Task A_damaged_or_missing_file_is_never_replaced_by_its_previous_generation(string damage)
    {
        await using var world = await BindingWorld.CreateAsync();
        var first = await world.SignInAsync(Google("g-ops-1", Ops));
        await world.Admission.AdmitAsync(Google("g-ops-2", Ops));   // pending subject_changed
        var request = Assert.Single(world.Store.Snapshot().Pending);
        Assert.True((await world.AsAdminAsync("identity.binding_confirm", () => world.Store.ConfirmAsync(request.Id))).Success);
        var file = new DataPathOptions(world.Directory).IdentityBindingsJson;
        var previous = await File.ReadAllTextAsync(file + ".bak");
        Assert.Contains("g-ops-1", previous);   // the previous generation still holds the revoked subject

        if (damage == "corrupt") await File.WriteAllTextAsync(file, "{ torn");
        else File.Delete(file);
        var restarted = world.NewStore();
        await restarted.InitializeAsync();

        foreach (var store in new[] { restarted, world.Store })
        {
            Assert.False(store.IsAvailable);
            Assert.Throws<IdentityBindingStoreUnavailableException>(() => store.Find(AuthConstants.GoogleIssuer, "g-ops-1"));
            var validator = new SessionValidator(world.Scopes, world.Whitelist, world.Roles, world.Configuration, bindings: store);
            Assert.Equal(SessionRevocationReasons.StoreUnavailable, (await validator.CheckAsync(first)).Reason);
            var admission = new FederatedAdmission(store, world.Roles, world.Whitelist, world.Configuration);
            Assert.Equal(AuthAuditReasons.StoreUnavailable, (await admission.AdmitAsync(Google("g-ops-1", Ops))).Reason);
        }
        Assert.Equal(previous, await File.ReadAllTextAsync(file + ".bak"));   // untouched
        Assert.Equal(damage == "corrupt", File.Exists(file));                 // nothing written back
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"version":1,"pending":[]}""")]
    [InlineData("""{"version":1,"bindings":[]}""")]
    [InlineData("""{"bindings":[],"pending":[]}""")]
    [InlineData("""{"version":1,"bindings":null,"pending":[]}""")]
    [InlineData("""{"version":1,"bindings":[],"pending":null}""")]
    [InlineData("""{"version":null,"bindings":[],"pending":[]}""")]
    [InlineData("""{"version":2,"bindings":[],"pending":[]}""")]
    [InlineData("""null""")]
    [InlineData("""[]""")]
    public async Task An_incomplete_document_makes_the_store_unavailable(string json)
    {
        await using var world = await BindingWorld.CreateAsync();
        await File.WriteAllTextAsync(new DataPathOptions(world.Directory).IdentityBindingsJson, json);
        var store = world.NewStore();
        await store.InitializeAsync();
        Assert.False(store.IsAvailable);
        var admission = new FederatedAdmission(store, world.Roles, world.Whitelist, world.Configuration);
        Assert.Equal(AuthAuditReasons.StoreUnavailable, (await admission.AdmitAsync(Google("g-ops", Ops))).Reason);
    }

    [Fact]
    public async Task A_complete_empty_document_and_no_file_at_all_are_an_empty_store()
    {
        await using var world = await BindingWorld.CreateAsync();
        Assert.True(world.Store.IsAvailable);   // never written: neither the file nor its previous generation
        await File.WriteAllTextAsync(new DataPathOptions(world.Directory).IdentityBindingsJson,
            """{"version":1,"bindings":[],"pending":[]}""");
        var store = world.NewStore();
        await store.InitializeAsync();
        Assert.True(store.IsAvailable);
        Assert.Empty(store.Snapshot().Bindings);
    }

    [Fact]
    public async Task A_failed_start_up_prune_keeps_federated_access_closed_and_a_recycled_address_does_not_revive_the_old_subject()
    {
        await using var world = await BindingWorld.CreateAsync();
        var old = await world.SignInAsync(Google("g-ops", Ops));
        // roles.json edited by hand while Whiskers was stopped: Ops is gone. The start-up prune cannot be saved.
        var roles = new UserRoleData { Roles = [new UserRoleEntry { Email = Root, Role = AppRole.Admin }] };
        await File.WriteAllTextAsync(new DataPathOptions(world.Directory).RolesJson, JsonSerializer.Serialize(roles));
        await world.Roles.InitializeAsync();
        world.BlockSaves();

        await new IdentityBindingStartupPrune(world.Store).InitializeAsync();

        Assert.False(world.Store.IsAvailable);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            (await new Whiskers.HealthChecks.IdentityBindingsReadyCheck(world.Store).CheckHealthAsync(new())).Status);
        Assert.Equal(AuthAuditReasons.StoreUnavailable, (await world.Admission.AdmitAsync(Google("g-ops", Ops))).Reason);
        Assert.Equal(SessionRevocationReasons.StoreUnavailable, (await world.Validator.CheckAsync(old)).Reason);
        // Configuring the address again while the prune is owed is refused: the owed prune cannot be saved.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync(Ops, AppRole.Operator)));
        Assert.False(world.Roles.HasExplicitRole(Ops));

        // Saves work again: configuring the address again takes the owed prune with it — the old subject is gone.
        world.Store.PersistForTests = null;
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync(Ops, AppRole.Operator));
        Assert.True(world.Store.IsAvailable);
        Assert.Empty(world.Store.Snapshot().Bindings);
        Assert.Equal(SessionRevocationReasons.BindingRevoked, (await world.Validator.CheckAsync(old)).Reason);
        Assert.Null(world.Store.Find(AuthConstants.GoogleIssuer, "g-ops"));
    }

    [Fact]
    public async Task A_previous_generation_restored_live_does_not_revive_an_orphan_when_the_address_is_configured_again()
    {
        await using var world = await BindingWorld.CreateAsync();
        world.Store.RecoveryDelay = TimeSpan.FromHours(1);   // only the steps below prune
        var old = await world.SignInAsync(Google("g-ops", Ops));
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.RemoveRoleAsync(Ops));   // pruned; the .bak keeps it
        var file = new DataPathOptions(world.Directory).IdentityBindingsJson;
        Assert.Contains("g-ops", await File.ReadAllTextAsync(file + ".bak"));

        // The file is damaged, and the operator copies the .bak back while Whiskers runs.
        await File.WriteAllTextAsync(file, "{ torn");
        Assert.False(world.Store.IsAvailable);
        File.Copy(file + ".bak", file, overwrite: true);
        Assert.False(world.Store.IsAvailable);   // readable again, but not pruned yet

        // The address goes to someone new: the addition prunes first.
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync(Ops, AppRole.Operator));
        Assert.True(world.Store.IsAvailable);
        Assert.Null(world.Store.Find(AuthConstants.GoogleIssuer, "g-ops"));
        Assert.False((await world.Admission.AdmitAsync(Google("g-ops", Ops, verified: "false"))).Admitted);
        Assert.Equal(SessionRevocationReasons.BindingRevoked, (await world.Validator.CheckAsync(old)).Reason);
    }

    [Fact]
    public async Task A_file_unreadable_at_start_and_repaired_live_waits_for_a_prune()
    {
        await using var world = await BindingWorld.CreateAsync();
        var old = await world.SignInAsync(Google("g-ops", Ops));
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.RemoveRoleAsync(Ops));
        var file = new DataPathOptions(world.Directory).IdentityBindingsJson;
        var withOrphan = await File.ReadAllTextAsync(file + ".bak");
        await File.WriteAllTextAsync(file, "{ torn");

        var store = world.NewStore();
        store.RecoveryDelay = TimeSpan.FromHours(1);
        await store.InitializeAsync();
        await new IdentityBindingStartupPrune(store).InitializeAsync();   // skipped: unreadable
        Assert.False(store.IsAvailable);

        await File.WriteAllTextAsync(file, withOrphan);   // repaired live — with the orphan in it
        Assert.False(store.IsAvailable);
        Assert.Throws<IdentityBindingStoreUnavailableException>(() => store.Find(AuthConstants.GoogleIssuer, "g-ops"));

        var roles = new RoleService(world.Configuration, NullLogger<RoleService>.Instance, dataPaths: new DataPathOptions(world.Directory),
            writeGate: world.Gate, bindings: store);
        await roles.InitializeAsync();
        await GatewayArrange.RunAsync("config.roles", () => roles.SetRoleAsync(Ops, AppRole.Operator));
        Assert.True(store.IsAvailable);
        Assert.Null(store.Find(AuthConstants.GoogleIssuer, "g-ops"));
        var admission = new FederatedAdmission(store, roles, world.Whitelist, world.Configuration);
        Assert.False((await admission.AdmitAsync(Google("g-ops", Ops, verified: "false"))).Admitted);
        var validator = new SessionValidator(world.Scopes, world.Whitelist, roles, world.Configuration, bindings: store);
        Assert.Equal(SessionRevocationReasons.BindingRevoked, (await validator.CheckAsync(old)).Reason);
    }

    [Fact]
    public async Task A_start_up_prune_that_cannot_take_the_lock_leaves_the_store_unavailable()
    {
        await using var world = await BindingWorld.CreateAsync();
        await world.SignInAsync(Google("g-ops", Ops));
        var store = world.NewStore();
        store.LockWait = TimeSpan.FromMilliseconds(200);
        store.RecoveryDelay = TimeSpan.FromHours(1);
        await store.InitializeAsync();
        Assert.True(store.IsAvailable);

        var lockPath = new DataPathOptions(world.Directory).IdentityBindingsJson + IdentityBindingStore.LockSuffix;
        await using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await new IdentityBindingStartupPrune(store).InitializeAsync();
            Assert.False(store.IsAvailable);
        }
        Assert.True(await store.RefreshAsync());
        Assert.NotNull(store.Find(AuthConstants.GoogleIssuer, "g-ops"));
    }

    [Fact]
    public async Task A_failed_read_is_tried_again_without_the_file_changing()
    {
        await using var world = await BindingWorld.CreateAsync();
        await world.SignInAsync(Google("g-ops", Ops));
        world.Store.RecoveryDelay = TimeSpan.FromMilliseconds(50);
        var file = new DataPathOptions(world.Directory).IdentityBindingsJson;
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));   // outside the re-read window
        Assert.True(world.Store.IsAvailable);

        // A changed file that cannot be opened for a moment (an antivirus scan, a backup tool).
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        await using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(world.Store.IsAvailable);

        // Released, the file unchanged since: the store reads it again and becomes available after its prune.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!world.Store.IsAvailable && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.True(world.Store.IsAvailable);
    }

    [Fact]
    public async Task A_change_is_noticed_outside_the_re_read_window_and_with_unchanged_time_and_length()
    {
        await using var world = await BindingWorld.CreateAsync();
        var session = await world.SignInAsync(Google("g-ops", Ops));
        await world.Admission.AdmitAsync(Oidc("o-root", Root));   // a second write: the .bak exists from here on
        var file = new DataPathOptions(world.Directory).IdentityBindingsJson;
        Assert.True(File.Exists(file + ".bak"));
        var frozen = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, frozen);
        var today = Assert.Single(world.Store.Snapshot().Bindings).LastSignInDay;

        // Another process notes a sign-in a day later: same length; the file system keeps the old time.
        var tomorrow = new ManualClock(DateTimeOffset.UtcNow.AddDays(1));
        var other = new IdentityBindingStore(world.Services, world.Audit, world.Configuration, new AccessControlWriteGate(),
            new DataPathOptions(world.Directory), tomorrow);
        await other.InitializeAsync();
        var length = new FileInfo(file).Length;
        Assert.True((await other.RecordSignInAsync(session.FindFirstValue(AuthConstants.BindingIdClaim)!)).Success);
        File.SetLastWriteTimeUtc(file, frozen);
        Assert.Equal(length, new FileInfo(file).Length);

        var seen = Assert.Single(world.Store.Snapshot().Bindings).LastSignInDay;
        Assert.NotEqual(today, seen);
        Assert.Equal(tomorrow.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), seen);
    }

    [Fact]
    public async Task A_reader_holding_the_file_does_not_stop_a_writer()
    {
        await using var world = await BindingWorld.CreateAsync(trustOidc: true);
        await world.SignInAsync(Google("g-ops", Ops));
        var file = new DataPathOptions(world.Directory).IdentityBindingsJson;
        // A reader (this process's or the other one's) has the file open while the write replaces it, then lets go.
        var reader = IdentityBindingStore.OpenShared(file);
        var write = world.Admission.AdmitAsync(Oidc("o-root", Root));
        await Task.Delay(80);
        await reader.DisposeAsync();
        Assert.True((await write).Admitted);
        Assert.True(world.Store.IsAvailable);
        Assert.Equal(2, world.Store.Snapshot().Bindings.Count);
    }

    [Fact]
    public async Task The_sign_in_binding_decides_the_whitelist_again_under_the_gate()
    {
        await using var world = await BindingWorld.CreateAsync();
        await GatewayArrange.RunAsync("config.whitelist", () => world.Whitelist.SaveWhitelistAsync(
            new WhitelistData { Enabled = true, Emails = [Root, Ops] }));
        // Between the admission's checks and the binding, an Admin takes Ops off the whitelist (its role keeps it an entry).
        world.Store.BeforeBindGateForTests = () => GatewayArrange.RunAsync("config.whitelist", () =>
            world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [Root] }));

        var refused = await world.Admission.AdmitAsync(Google("g-ops", Ops));

        Assert.False(refused.Admitted);
        Assert.Equal(AuthAuditReasons.NotWhitelisted, refused.Reason);
        Assert.Empty(world.Store.Snapshot().Bindings);
        Assert.DoesNotContain(world.Audit.Rows, r => r.Action == AuthAuditLog.IdentityBoundAction);
    }

    [Fact]
    public async Task Pending_requests_expire_after_a_day_and_are_capped()
    {
        var time = new ManualClock(DateTimeOffset.UtcNow);
        await using var world = await BindingWorld.CreateAsync(time: time);
        await world.Admission.AdmitAsync(Oidc("o-ops", Ops));
        Assert.Single(world.Store.Snapshot().Pending);
        time.Advance(IdentityBindingStore.PendingLifetime + TimeSpan.FromMinutes(1));
        Assert.Empty(world.Store.Snapshot().Pending);
        Assert.Equal(3, await IdentityBindingCli.RunAsync(world.CliServices(), [Ops], new StringWriter()));   // expired

        for (var i = 0; i < IdentityBindingStore.MaxPending + 5; i++)
        {
            var address = $"user{i}@corp.test";
            await GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync(address, AppRole.Viewer));
            time.Advance(TimeSpan.FromSeconds(1));
            await world.Admission.AdmitAsync(Oidc($"o-{i}", address));
        }
        var pending = world.Store.Snapshot().Pending;
        Assert.Equal(IdentityBindingStore.MaxPending, pending.Count);
        Assert.DoesNotContain(pending, p => p.Account == "user0@corp.test");   // the oldest went first
        Assert.Contains(pending, p => p.Account == $"user{IdentityBindingStore.MaxPending + 4}@corp.test");
    }

    [Fact]
    public async Task Pruning_runs_before_the_role_or_whitelist_write_and_a_failed_prune_refuses_it()
    {
        await using var world = await BindingWorld.CreateAsync();
        await GatewayArrange.RunAsync("config.whitelist", () => world.Whitelist.SaveWhitelistAsync(
            new WhitelistData { Enabled = true, Emails = [Root, Ops, "listed@corp.test"] }));
        await world.SignInAsync(Google("g-listed", "listed@corp.test"));
        await world.SignInAsync(Google("g-ops", Ops));
        Assert.Equal(2, world.Store.Snapshot().Bindings.Count);

        // A whitelist save that drops an address prunes its binding; one still an entry by its role stays.
        await GatewayArrange.RunAsync("config.whitelist", () => world.Whitelist.SaveWhitelistAsync(
            new WhitelistData { Enabled = true, Emails = [Root] }));
        Assert.Equal([Ops], world.Store.Snapshot().Bindings.Select(b => b.Account));

        // A failed prune refuses the role write: the role stays.
        world.BlockSaves();
        await Assert.ThrowsAnyAsync<Exception>(() => GatewayArrange.RunAsync("config.roles", () => world.Roles.RemoveRoleAsync(Ops)));
        Assert.True(world.Roles.HasExplicitRole(Ops));
        Assert.False(world.Store.IsAvailable);
        world.Store.PersistForTests = null;
        Assert.True(await world.Store.RefreshAsync());
        Assert.Single(world.Store.Snapshot().Bindings);   // the refused write removed nothing
    }

    [Fact]
    public async Task The_start_up_prune_removes_bindings_of_addresses_that_are_no_longer_entries()
    {
        await using var world = await BindingWorld.CreateAsync();
        await world.SignInAsync(Google("g-ops", Ops));
        // roles.json edited by hand while Whiskers was stopped: Ops is gone.
        var roles = new UserRoleData { Roles = [new UserRoleEntry { Email = Root, Role = AppRole.Admin }] };
        await File.WriteAllTextAsync(new DataPathOptions(world.Directory).RolesJson, JsonSerializer.Serialize(roles));
        await world.Roles.InitializeAsync();

        await new IdentityBindingStartupPrune(world.Store).InitializeAsync();

        Assert.Empty(world.Store.Snapshot().Bindings);
    }

    // ------------------------------------------------------------------------------------------------ logging

    [Fact]
    public async Task No_log_line_of_any_path_carries_an_address_or_a_subject()
    {
        await using var world = await BindingWorld.CreateAsync(trustOidc: false);
        await world.AddLocalUserAsync(Root);
        await world.SignInAsync(Google("subject-of-ops-1", Ops));                         // bound
        await world.Admission.AdmitAsync(Google("subject-of-ops-2", Ops));                 // pending subject_changed
        await world.Admission.AdmitAsync(Oidc("subject-of-ops-3", Ops));                   // pending second_issuer
        await world.Admission.AdmitAsync(Google("subject-of-stranger", "stranger@elsewhere.test"));
        await world.Admission.AdmitAsync(Google("subject-of-ops-1", "renamed@corp.test"));  // bound, new address
        var pending = world.Store.Snapshot().Pending[0];
        await world.AsAdminAsync("identity.binding_confirm", () => world.Store.ConfirmAsync(pending.Id));
        await IdentityBindingCli.RunAsync(world.CliServices(), [Ops], new StringWriter());
        world.Audit.Down = true;
        await world.Admission.AdmitAsync(Google("subject-of-root", Root));                 // audit down
        world.Audit.Down = false;
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.RemoveRoleAsync(Ops));   // prune
        world.BlockSaves();
        await world.Admission.AdmitAsync(Google("subject-of-root", Root));                 // save fails
        await world.Validator.CheckAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, Ops)], "Google")));

        var text = string.Join("\n", world.Logs.Lines);
        Assert.NotEmpty(world.Logs.Lines);
        foreach (var secret in new[] { "corp.test", "elsewhere.test", "subject-of-" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_real_audit_writer_keeps_the_account_in_the_row_and_out_of_the_log()
    {
        await using var world = await BindingWorld.CreateAsync();
        using var trail = new AuditTrailFixture(vaultKey: null);
        var logs = new NotificationDeliveryTests.ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var paths = new DataPathOptions(world.Directory);

        // Rows written: the sign-in binding, a pending request, the host confirmation, and a queued sign-in row.
        var written = new AuditLogService(trail.Scopes, factory.CreateLogger<AuditLogService>());
        var store = new IdentityBindingStore(world.Services, written, world.Configuration, world.Gate, paths);
        var admission = new FederatedAdmission(store, world.Roles, world.Whitelist, world.Configuration);
        Assert.True((await admission.AdmitAsync(Google("g-ops", Ops))).Admitted);
        await admission.AdmitAsync(Oidc("o-root", Root));
        Assert.True((await store.ConfirmFromHostAsync(Root, null)).Success);
        var queue = new AuthAuditLog(written, factory.CreateLogger<AuthAuditLog>());
        await queue.StartAsync(default);
        await queue.SignedInAsync(new DefaultHttpContext(), "google", Ops);
        await queue.StopAsync(default);
        // Rows lost: the database is gone (the fallback line), and the queue is closed (the dropped-row line).
        var lost = new AuditLogService(new ThrowingScopes(), factory.CreateLogger<AuditLogService>());
        var lostStore = new IdentityBindingStore(world.Services, lost, world.Configuration, world.Gate, paths);
        var lostAdmission = new FederatedAdmission(lostStore, world.Roles, world.Whitelist, world.Configuration);
        await GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync("third@corp.test", AppRole.Viewer));
        Assert.Equal(AuthAuditReasons.AuditUnavailable, (await lostAdmission.AdmitAsync(Google("g-third", "third@corp.test"))).Reason);
        await lost.TryLogAsync(Ops, "web", AuthAuditLog.LoginFailedAction, AuthAuditLog.Target, "google", "google", "reason=x", success: false);
        await queue.SignInRefusedAsync(new DefaultHttpContext(), "oidc", Ops, AuthAuditReasons.PendingConfirmation);

        var text = string.Join("\n", logs.Lines);
        Assert.Contains("AUDIT WRITE FAILED", text);
        Assert.Contains(AuthAuditLog.IdentityBoundAction, text);
        Assert.DoesNotContain("corp.test", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint=", text, StringComparison.Ordinal);   // identity details stay out of the fallback
        // The persisted rows still name the account.
        using var scope = trail.Scope();
        var rows = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().ToListAsync();
        Assert.Contains(rows, r => r.Action == AuthAuditLog.IdentityBoundAction && r.Actor == Ops && r.TargetName == Ops);
        Assert.Contains(rows, r => r.Action == AuthAuditLog.IdentityBoundAction && r.Actor == IdentityBindingStore.CliActor && r.TargetName == Root);
        Assert.Contains(rows, r => r.Action == AuthAuditLog.LoginAction && r.Actor == Ops);
    }

    private sealed class ThrowingScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("audit db unavailable");
    }

    // ------------------------------------------------------------------------------------------------ helpers

    internal static TicketReceivedContext Ticket(HttpContext http, string scheme, ClaimsIdentity identity) =>
        new(http, new AuthenticationScheme(scheme, scheme, typeof(GoogleHandler)), new GoogleOptions(),
            new AuthenticationTicket(new ClaimsPrincipal(identity), scheme));
}

/// <summary>A real binding world on disk: roles, whitelist and identity bindings sharing one write gate, a local Identity
/// database, the session validator and the admission, with recorders for the audit log and every log line.</summary>
internal sealed class BindingWorld : IAsyncDisposable
{
    private readonly ServiceProvider _sp;
    public string Directory { get; }
    public IConfiguration Configuration { get; }
    public AccessControlWriteGate Gate { get; } = new();
    public RoleService Roles { get; private set; } = null!;
    public WhitelistService Whitelist { get; private set; } = null!;
    public IdentityBindingStore Store { get; private set; } = null!;
    public SessionValidator Validator { get; private set; } = null!;
    public FederatedAdmission Admission { get; private set; } = null!;
    public Audit Audit { get; } = new();
    public NotificationDeliveryTests.ListLoggerProvider Logs { get; } = new();
    public IServiceScopeFactory Scopes => _sp.GetRequiredService<IServiceScopeFactory>();
    public IServiceProvider Services => _sp;
    private readonly ILoggerFactory _loggers;

    private BindingWorld(string dir, IConfiguration configuration)
    {
        Directory = dir;
        Configuration = configuration;
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dir, "id.db")}"));
        services.AddIdentityCore<AppUser>().AddEntityFrameworkStores<WhiskersIdentityDbContext>();
        services.AddSingleton<IRoleService>(_ => Roles);
        services.AddSingleton<IWhitelistService>(_ => Whitelist);
        services.AddSingleton<IAuditLogService>(Audit);
        services.AddSingleton(configuration);
        _sp = services.BuildServiceProvider();
        _loggers = _sp.GetRequiredService<ILoggerFactory>();
    }

    public TimeProvider? Time { get; private set; }

    public static async Task<BindingWorld> CreateAsync(bool seedAdmin = true, bool trustOidc = false, bool authDisabled = false,
        TimeProvider? time = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sw-r127-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        var settings = new Dictionary<string, string?>
        {
            ["Oidc:TrustEmailVerified"] = trustOidc ? "true" : "false",
            ["Auth:Disabled"] = authDisabled ? "true" : "false",
        };
        if (seedAdmin) settings["WHISKERS_ADMIN_EMAIL"] = OidcIdentityBindingTests.Root;
        var world = new BindingWorld(dir, new ConfigurationBuilder().AddInMemoryCollection(settings).Build()) { Time = time };
        using (var scope = world._sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.EnsureCreatedAsync();
        var paths = new DataPathOptions(dir);
        world.Store = world.NewStore();
        await world.Store.InitializeAsync();
        world.Roles = new RoleService(world.Configuration, world._loggers.CreateLogger<RoleService>(), dataPaths: paths,
            writeGate: world.Gate, bindings: world.Store);
        await world.Roles.InitializeAsync();
        world.Whitelist = new WhitelistService(world.Configuration, world.Roles, world._loggers.CreateLogger<WhitelistService>(),
            paths, world.Gate, world.Store);
        await world.Whitelist.InitializeAsync();
        if (seedAdmin)
            await GatewayArrange.RunAsync("config.roles", () => world.Roles.SetRoleAsync(OidcIdentityBindingTests.Ops, AppRole.Operator));
        world.Validator = new SessionValidator(world.Scopes, world.Whitelist, world.Roles, world.Configuration,
            world._loggers.CreateLogger<SessionValidator>(), world.Store);
        world.Admission = new FederatedAdmission(world.Store, world.Roles, world.Whitelist, world.Configuration,
            world._loggers.CreateLogger<FederatedAdmission>());
        return world;
    }

    /// <summary>Another store instance over the same file in this process (a restart).</summary>
    public IdentityBindingStore NewStore() => new(_sp, Audit, Configuration, Gate, new DataPathOptions(Directory), Time,
        logger: _loggers.CreateLogger<IdentityBindingStore>());

    /// <summary>A store as another process (the host CLI) has it: same file, its own write gate.</summary>
    public IdentityBindingStore NewProcessStore() => new(_sp, Audit, Configuration, new AccessControlWriteGate(),
        new DataPathOptions(Directory), Time, logger: _loggers.CreateLogger<IdentityBindingStore>());

    /// <summary>The services the host CLI resolves: the initializers it runs and the binding store (another process).</summary>
    public IServiceProvider CliServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIdentityBindingStore>(_ => NewProcessStore());
        services.AddSingleton<Whiskers.Services.IInitializable>(sp =>
            (Whiskers.Services.IInitializable)sp.GetRequiredService<IIdentityBindingStore>());
        return services.BuildServiceProvider();
    }

    /// <summary>The principal the gate issues after an admission (asserted admitted).</summary>
    public async Task<ClaimsPrincipal> SignInAsync(FederatedSignInClaims claims)
    {
        var decision = await Admission.AdmitAsync(claims);
        Assert.True(decision.Admitted, decision.Reason);
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, decision.Account!), new Claim(ClaimTypes.Name, decision.Account!),
            new Claim(AuthConstants.BindingIdClaim, decision.BindingId!),
        ], AuthConstants.FederatedScheme));
    }

    public DefaultHttpContext Http() => new()
    {
        RequestServices = new ServiceCollection()
            .AddSingleton<IFederatedAdmission>(Admission)
            .AddSingleton<IAuthAuditLog>(new NullAuthAudit())
            .AddSingleton(Configuration)
            .BuildServiceProvider(),
    };

    public async Task AddLocalUserAsync(string email)
    {
        using var scope = _sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var created = await users.CreateAsync(new AppUser { UserName = email, Email = email, EmailConfirmed = true }, "CorrectHorse12!!");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Code)));
    }

    public async Task<ClaimsPrincipal> LocalSessionAsync(string email)
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

    public async Task ChangeStampAsync(string email)
    {
        using var scope = _sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True((await users.UpdateSecurityStampAsync((await users.FindByEmailAsync(email))!)).Succeeded);
    }

    /// <summary>Every later save of the bindings fails, as on a full disk.</summary>
    public void BlockSaves() => Store.PersistForTests = _ => throw new IOException("the disk is full");

    /// <summary>Runs <paramref name="write"/> inside a decision for a human Admin (Root), as the gateway does.</summary>
    public Task<T> AsAdminAsync<T>(string capability, Func<Task<T>> write) => AsAsync(capability, McpPermissionLevels.Admin, write);

    public async Task<T> AsAsync<T>(string capability, string level, Func<Task<T>> write)
    {
        var principal = new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(OidcIdentityBindingTests.Root),
            OidcIdentityBindingTests.Root, level);
        using (MutationScope.Begin(capability, "test-" + Guid.NewGuid().ToString("N"), principal))
            return await write();
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        TestSqlite.ClearPools(Directory);
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { /* temp */ }
    }

    private sealed class NullAuthAudit : IAuthAuditLog
    {
        public Task SignedInAsync(HttpContext context, string method, string email) => Task.CompletedTask;
        public Task SignInRefusedAsync(HttpContext context, string method, string? account, string reason) => Task.CompletedTask;
        public Task SignedOutAsync(HttpContext context) => Task.CompletedTask;
    }
}

/// <summary>The audit log as rows; <see cref="Down"/> loses every write; <see cref="OnWrite"/> runs before a row is kept.</summary>
internal sealed class Audit : IAuditLogService
{
    public sealed record Row(string Actor, string Action, string TargetId, string Details, bool Success);
    private readonly List<Row> _rows = [];
    public bool Down { get; set; }
    public Action? OnWrite { get; set; }
    public IReadOnlyList<Row> Rows { get { lock (_rows) return [.. _rows]; } }

    public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
        string targetName, string? details = null, string? serverId = null, bool success = true) =>
        TryLogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

    public Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
        string targetName, string? details = null, string? serverId = null, bool success = true)
    {
        if (Down) return Task.FromResult(false);
        OnWrite?.Invoke();
        lock (_rows) _rows.Add(new Row(actor, action, targetId, details ?? "", success));
        return Task.FromResult(true);
    }

    public Task<bool> TryLogAtAsync(DateTime occurredUtc, string actor, string actorType, string action, string targetType,
        string targetId, string targetName, string? details = null, string? serverId = null, bool success = true) =>
        TryLogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

    public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
        string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());

    public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
}
