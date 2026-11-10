using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Startup;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0009: the exemptions of the read-only kill switch, each pinned while the switch is on. The setting
/// (<see cref="GuardrailPolicy.KillSwitchBlockedExemptions"/>): left out of the stored and hashed policy while empty, an
/// unknown id fails the store closed. The blockable exemptions — own password change (E4), recovery-code redemption (E5),
/// federated binding at sign-in (E6) — work under the switch unless blocked; blocked, or with the store fail-closed (D4),
/// they are refused with nothing changed and one audit event naming <c>read-only-mode</c>. The host CLIs (E3) work
/// either way. E1, E2 and the gateway-level proofs are in <see cref="ReadOnlyKillSwitchWireTests"/>.
/// </summary>
public sealed class KillSwitchExemptionTests : IDisposable
{
    private const string Ops = OidcIdentityBindingTests.Ops;
    private const string LocalOps = "ops@local.test";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-0009-" + Guid.NewGuid().ToString("N"));

    private static readonly AgentPrincipal Admin =
        new(AgentPrincipalKind.WebUser, "admin@x", McpPermissionLevels.Admin, null, UserEmail: "admin@x");

    public KillSwitchExemptionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp */ }
    }

    /// <summary>The kill switch on, with <paramref name="blocked"/> blocked beforehand.</summary>
    private static GuardrailPolicy Killed(params string[] blocked)
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;
        policy.KillSwitchBlockedExemptions = [.. blocked];
        return policy;
    }

    /// <summary>A real store over an unreadable <c>guardrails.json</c>: fail-closed (INV-25).</summary>
    private async Task<GuardrailStore> FailClosedStoreAsync()
    {
        var path = Path.Combine(_dir, $"guardrails-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ not json");
        var store = new GuardrailStore(filePath: path);
        await store.InitializeAsync();
        Assert.NotNull(store.LoadError);
        return store;
    }

    // ------------------------------------------------------------------------------------------------ the setting (D3, D4)

    [Fact]
    public async Task An_empty_block_list_is_left_out_of_the_stored_and_hashed_policy()
    {
        var plain = GuardrailPolicy.SafeDefault();
        var empty = GuardrailPolicy.SafeDefault();
        empty.KillSwitchBlockedExemptions = [];

        Assert.Null(empty.KillSwitchBlockedExemptions);
        var before = PolicySnapshot.Of("Standard", plain);
        Assert.Equal(before.VersionHash, PolicySnapshot.Of("Standard", empty).VersionHash);
        Assert.DoesNotContain(nameof(GuardrailPolicy.KillSwitchBlockedExemptions), before.Json);

        // The stored file leaves it out too, and a block is a new version.
        var path = Path.Combine(_dir, "guardrails.json");
        var store = new GuardrailStore(filePath: path);
        await store.InitializeAsync();
        var config = store.Config;
        config.ActivePolicy().KillSwitchBlockedExemptions = [];
        await GatewayArrange.RunAsync("guardrails.save", () => store.SaveConfigAsync(config, Admin));
        Assert.DoesNotContain(nameof(GuardrailPolicy.KillSwitchBlockedExemptions), await File.ReadAllTextAsync(path));
        var blocked = GuardrailPolicy.SafeDefault();
        blocked.KillSwitchBlockedExemptions = [KillSwitchExemptions.PasswordChange];
        Assert.NotEqual(before.VersionHash, PolicySnapshot.Of("Standard", blocked).VersionHash);
    }

    [Fact]
    public async Task An_unknown_exemption_is_never_saved_and_fails_the_store_closed()
    {
        var path = Path.Combine(_dir, "guardrails.json");
        var store = new GuardrailStore(filePath: path);
        await store.InitializeAsync();

        // A known id is saved and read back.
        var config = store.Config;
        config.ActivePolicy().KillSwitchBlockedExemptions = [KillSwitchExemptions.RecoveryCode];
        await GatewayArrange.RunAsync("guardrails.save", () => store.SaveConfigAsync(config, Admin));
        var reloaded = new GuardrailStore(filePath: path);
        await reloaded.InitializeAsync();
        Assert.Null(reloaded.LoadError);
        Assert.Equal([KillSwitchExemptions.RecoveryCode], reloaded.Current.KillSwitchBlockedExemptions);

        // An unknown one — E1–E3 are not blockable — is refused on save; nothing changes.
        foreach (var unknown in new[] { "switch_off", "host_cli", "bogus" })
        {
            config.ActivePolicy().KillSwitchBlockedExemptions = [unknown];
            var refused = await Assert.ThrowsAsync<GuardrailConfigException>(() =>
                GatewayArrange.RunAsync("guardrails.save", () => store.SaveConfigAsync(config, Admin)));
            Assert.Equal(GuardrailConfigProblemKind.UnknownKillSwitchExemption, refused.Problem.Kind);
            Assert.Equal([KillSwitchExemptions.RecoveryCode], store.Current.KillSwitchBlockedExemptions);
        }

        // In the file it fails the store closed (INV-25): the kill switch on, every exemption blocked.
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"recovery_code\"", "\"bogus\""));
        var broken = new GuardrailStore(filePath: path);
        await broken.InitializeAsync();
        Assert.NotNull(broken.LoadError);
        Assert.True(broken.Current.ReadOnlyMode);
    }

    [Fact]
    public async Task A_fail_closed_store_blocks_every_blockable_exemption()
    {
        var store = await FailClosedStoreAsync();
        Assert.All(KillSwitchExemptions.Blockable, e => Assert.True(store.Current.KillSwitchBlocks(e), e));
        Assert.Equal(KillSwitchExemptions.Blockable, GuardrailPolicy.FailClosed().KillSwitchBlockedExemptions);
        // Without the kill switch nothing is blocked, whatever the list says.
        var off = Killed([.. KillSwitchExemptions.Blockable]);
        off.ReadOnlyMode = false;
        Assert.All(KillSwitchExemptions.Blockable, e => Assert.False(off.KillSwitchBlocks(e), e));
    }

    // ------------------------------------------------------------------------------------------------ E4 own password

    [Fact]
    public async Task E4_an_own_password_change_works_under_the_kill_switch_unless_it_is_blocked()
    {
        await using (var world = await LocalAccountWorld.CreateAsync(policy: Killed()))
        {
            await world.AddUserAsync(LocalOps, AppRole.Viewer);
            using var self = world.As(await world.SessionOfAsync(LocalOps));
            var changed = await self.Mutations.RunAsync("account.password_change", new { account = LocalOps },
                (ResolvedTarget _, CancellationToken ct) => self.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, LocalAccountWorld.NewPassword, ct));
            Assert.True(changed.Success);
        }

        await using (var world = await LocalAccountWorld.CreateAsync(policy: Killed(KillSwitchExemptions.PasswordChange)))
        {
            await world.AddUserAsync(LocalOps, AppRole.Viewer);
            using var self = world.As(await world.SessionOfAsync(LocalOps));
            var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => self.Mutations.RunAsync("account.password_change",
                new { account = LocalOps },
                (ResolvedTarget _, CancellationToken ct) => self.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, LocalAccountWorld.NewPassword, ct)));
            Assert.Equal(MutationStatus.Denied, refused.Status);
            Assert.True(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.Password));
            Assert.Equal(["denied:account.password_change:Denied"], world.Sink.Events);
            Assert.Contains("own password changes are blocked while the kill switch is on", world.Sink.Text);
        }
    }

    [Fact]
    public async Task E4_a_fail_closed_store_blocks_the_own_password_change()
    {
        await using var world = await LocalAccountWorld.CreateAsync(guardrails: await FailClosedStoreAsync());
        await world.AddUserAsync(LocalOps, AppRole.Viewer);
        using var self = world.As(await world.SessionOfAsync(LocalOps));

        await Assert.ThrowsAsync<MutationRefusedException>(() => self.Mutations.RunAsync("account.password_change",
            new { account = LocalOps },
            (ResolvedTarget _, CancellationToken ct) => self.Accounts.ChangeOwnPasswordAsync(LocalAccountWorld.Password, LocalAccountWorld.NewPassword, ct)));

        Assert.True(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.Password));
        Assert.Equal(["denied:account.password_change:Denied"], world.Sink.Events);
        Assert.Contains("own password changes are blocked while the kill switch is on", world.Sink.Text);
    }

    // ------------------------------------------------------------------------------------------------ E3 + E5 recovery

    [Fact]
    public async Task E3_E5_under_the_kill_switch_the_host_cli_issues_a_code_and_it_is_redeemed_unless_blocked()
    {
        // Not blocked: the host CLI (E3) issues, the person redeems (E5).
        var policy = Killed();
        await using (var world = await LocalAccountWorld.CreateAsync(policy: policy))
        {
            await world.AddUserAsync(LocalOps, AppRole.Admin);
            var code = await IssueAsync(world, LocalOps);
            using var anonymous = world.As(LocalAccountWorld.Anonymous);
            Assert.True((await anonymous.Accounts.RedeemRecoveryCodeAsync(LocalOps, code, LocalAccountWorld.NewPassword)).Success);
            Assert.True(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.NewPassword));
        }

        // Blocked: the CLI still issues (fixed, E3); the redemption is refused with nothing changed and one event.
        var blocked = Killed(KillSwitchExemptions.RecoveryCode);
        await using (var world = await LocalAccountWorld.CreateAsync(policy: blocked))
        {
            await world.AddUserAsync(LocalOps, AppRole.Admin);
            var code = await IssueAsync(world, LocalOps, force: true);
            var rowsBefore = world.Audit.Rows.Count;
            using var anonymous = world.As(LocalAccountWorld.Anonymous);

            var refused = await anonymous.Accounts.RedeemRecoveryCodeAsync(LocalOps, code, LocalAccountWorld.NewPassword);

            Assert.Equal(LocalAccountErrors.KillSwitch, refused.Error);
            Assert.False(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.NewPassword));
            var row = Assert.Single(world.Audit.Rows.Skip(rowsBefore));
            Assert.Equal((LocalAccountService.RecoveryRefusedAction, false), (row.Action, row.Success));
            Assert.Contains("rule=read-only-mode", row.Details);
            Assert.DoesNotContain(LocalOps, row.Actor + row.TargetId);   // no account was looked up

            // The code was not touched: with the kill switch off it still works.
            blocked.ReadOnlyMode = false;
            Assert.True((await anonymous.Accounts.RedeemRecoveryCodeAsync(LocalOps, code, LocalAccountWorld.NewPassword)).Success);
        }
    }

    [Fact]
    public async Task E3_E5_with_a_fail_closed_store_the_host_cli_still_issues_a_code_and_the_redemption_is_blocked()
    {
        await using var world = await LocalAccountWorld.CreateAsync(guardrails: await FailClosedStoreAsync());
        await world.AddUserAsync(LocalOps, AppRole.Admin);
        var code = await IssueAsync(world, LocalOps, force: true);
        using var anonymous = world.As(LocalAccountWorld.Anonymous);

        var refused = await anonymous.Accounts.RedeemRecoveryCodeAsync(LocalOps, code, LocalAccountWorld.NewPassword);

        Assert.Equal(LocalAccountErrors.KillSwitch, refused.Error);
        Assert.False(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.NewPassword));
        Assert.Single(world.Audit.Rows, r => r.Action == LocalAccountService.RecoveryRefusedAction
                                             && r.Details!.Contains("rule=read-only-mode"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E3_while_redeeming_is_blocked_the_recovery_cli_refuses_without_force_and_names_the_way_back(bool failClosed)
    {
        // Contract 0009 A1.2: issuing would clear the password and leave a code that cannot be redeemed.
        await using var world = failClosed
            ? await LocalAccountWorld.CreateAsync(guardrails: await FailClosedStoreAsync())
            : await LocalAccountWorld.CreateAsync(policy: Killed(KillSwitchExemptions.RecoveryCode));
        await world.AddUserAsync(LocalOps, AppRole.Admin);
        var rowsBefore = world.Audit.Rows.Count;

        var output = new StringWriter();
        Assert.Equal(4, await LocalAccountRecoveryCli.RunAsync(world.Services, [LocalOps], output));

        // Nothing changed: the password works, no row was written, and the message says why and what helps.
        Assert.True(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.Password));
        Assert.Equal(rowsBefore, world.Audit.Rows.Count);
        var text = output.ToString();
        Assert.Contains("could not be redeemed", text);
        Assert.Contains(failClosed ? "cannot be read" : "kill switch is on", text);
        Assert.Contains("put a valid guardrails.json into the data directory", text);
        Assert.Contains("ReadOnlyMode or the recovery_code block", text);
        Assert.Contains(LocalAccountRecoveryCli.ForceFlag, text);
        Assert.DoesNotMatch("([2-9A-HJ-NP-Z]{4}-){5}[2-9A-HJ-NP-Z]{4}", text);

        // With --force it issues (E3 stays usable, D1), warns, and the password is gone.
        var forced = new StringWriter();
        Assert.Equal(0, await LocalAccountRecoveryCli.RunAsync(world.Services, [LocalOps, LocalAccountRecoveryCli.ForceFlag], forced));
        Assert.Contains("Warning (--force)", forced.ToString());
        Assert.Contains("put a valid guardrails.json into the data directory", forced.ToString());
        Assert.Matches("([2-9A-HJ-NP-Z]{4}-){5}[2-9A-HJ-NP-Z]{4}", forced.ToString());
        Assert.False(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.Password));
        // The audit trail shows the issue was forced past the block, and which one.
        var issued = Assert.Single(world.Audit.Rows, r => r.Action == LocalAccountRecoveryCli.IssuedAction);
        Assert.Contains("forced (--force)", issued.Details);
        Assert.Contains(failClosed ? "fail-closed guardrails" : "the recovery_code block", issued.Details);
    }

    [Fact]
    public async Task E3_the_recovery_cli_needs_no_force_while_redeeming_works_and_rejects_unknown_arguments()
    {
        await using var world = await LocalAccountWorld.CreateAsync(policy: Killed());
        await world.AddUserAsync(LocalOps, AppRole.Admin);
        var output = new StringWriter();
        Assert.Equal(2, await LocalAccountRecoveryCli.RunAsync(world.Services, [LocalOps, "--forced"], output));
        Assert.True(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.Password));
        Assert.DoesNotContain("Warning", (await IssueOutputAsync(world, LocalOps)));
        Assert.DoesNotContain("forced", Assert.Single(world.Audit.Rows, r => r.Action == LocalAccountRecoveryCli.IssuedAction).Details);
    }

    [Fact]
    public async Task E3_the_recovery_cli_on_the_production_service_graph_reads_guardrails_json_itself_and_refuses()
    {
        // The registrations of the app (guardrail policy, gateway, scope guard) and no start-up initialiser, as in the
        // CLI process: IGuardrailStore is the scope guard's proxy over a store that never loaded. The CLI takes the
        // store behind the proxy, loads it read-only and refuses; the data directory stays byte-identical.
        var dir = Path.Combine(_dir, "production");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "guardrails.json"), "{ not json");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DataPathOptions(dir));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddGuardrailPolicy();
        services.AddMutationGateway();
        services.AddMutationScopeGuard();
        await using var provider = services.BuildServiceProvider();
        Assert.IsAssignableFrom<IMutationScopeProxy>(provider.GetRequiredService<IGuardrailStore>());
        Assert.False(provider.GetRequiredService<GuardrailStore>().IsLoaded);
        var before = Snapshot(dir);

        var output = new StringWriter();
        Assert.Equal(4, await LocalAccountRecoveryCli.RunAsync(provider, [LocalOps], output));

        Assert.Contains("cannot be read", output.ToString());
        Assert.True(provider.GetRequiredService<GuardrailStore>().IsLoaded);
        Assert.Equal(before, Snapshot(dir));   // no guardrails.json.unreadable-* copy, nothing saved
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E3_an_unchecked_guardrail_policy_counts_as_fail_closed_refused_without_force_issued_with_it(bool storeMissing)
    {
        // No guardrail store, or one that is not the real store: the policy cannot be checked, so it counts as
        // fail-closed (D4) — never as "allowed" — and, as for the fail-closed policy, --force still issues (A1.2).
        await using var world = await LocalAccountWorld.CreateAsync(policy: Killed());
        await world.AddUserAsync(LocalOps, AppRole.Admin);
        var services = new GuardrailsReplaced(world.Services,
            storeMissing ? null : new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault()));

        var refused = new StringWriter();
        Assert.Equal(4, await LocalAccountRecoveryCli.RunAsync(services, [LocalOps], refused));
        Assert.Contains("could not be checked", refused.ToString());
        Assert.Contains("put a valid guardrails.json into the data directory", refused.ToString());
        Assert.Contains(LocalAccountRecoveryCli.ForceFlag, refused.ToString());
        Assert.True(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.Password));
        Assert.Empty(world.Audit.Rows.Where(r => r.Action == LocalAccountRecoveryCli.IssuedAction));

        var forced = new StringWriter();
        Assert.Equal(0, await LocalAccountRecoveryCli.RunAsync(services, [LocalOps, LocalAccountRecoveryCli.ForceFlag], forced));
        Assert.Contains("Warning (--force)", forced.ToString());
        Assert.False(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.Password));
        var issued = Assert.Single(world.Audit.Rows, r => r.Action == LocalAccountRecoveryCli.IssuedAction);
        Assert.Contains("forced (--force)", issued.Details);
        Assert.Contains("policy unchecked", issued.Details);
    }

    /// <summary>The world's services with <see cref="IGuardrailStore"/> replaced (<c>null</c>: none registered).</summary>
    private sealed class GuardrailsReplaced(IServiceProvider inner, IGuardrailStore? store) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IGuardrailStore) ? store : inner.GetService(serviceType);
    }

    [Fact]
    public async Task E3_the_recovery_cli_loads_an_unloaded_store_read_only_a_broken_file_refuses_a_missing_one_writes_nothing()
    {
        // The store itself, unloaded (as behind the proxy above): the CLI loads it read-only, whatever Program.cs does
        // first, and leaves the data directory as it was (no copy, no save).
        var dir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "guardrails.json");
        await File.WriteAllTextAsync(path, "{ not json");
        var store = new GuardrailStore(filePath: path);
        Assert.False(store.IsLoaded);
        Assert.False(store.Current.ReadOnlyMode);   // unloaded: only the in-memory SafeDefault
        await using var world = await LocalAccountWorld.CreateAsync(guardrails: store);
        await world.AddUserAsync(LocalOps, AppRole.Admin);
        var before = Snapshot(dir);

        var output = new StringWriter();
        Assert.Equal(4, await LocalAccountRecoveryCli.RunAsync(world.Services, [LocalOps], output));

        Assert.True(store.IsLoaded);
        Assert.NotNull(store.LoadError);
        Assert.Contains("cannot be read", output.ToString());
        Assert.True(await world.PasswordWorksAsync(LocalOps, LocalAccountWorld.Password));
        Assert.Equal(before, Snapshot(dir));   // byte-identical, no guardrails.json.unreadable-* copy

        // A missing file with no record of an earlier policy (contract 0010) is the SafeDefault in memory: the code is
        // issued, and neither guardrails.json nor the record is written.
        File.Delete(path);
        var record = new InMemoryGuardrailPolicyMarker();
        await using var fresh = await LocalAccountWorld.CreateAsync(guardrails: new GuardrailStore(filePath: path, marker: record));
        await fresh.AddUserAsync(LocalOps, AppRole.Admin);
        Assert.Equal(0, await LocalAccountRecoveryCli.RunAsync(fresh.Services, [LocalOps], new StringWriter()));
        Assert.Empty(Directory.GetFiles(dir));
        Assert.Equal(0, record.Writes);

        // With the record present the missing file is fail-closed: the CLI refuses without --force (contract 0010 D5).
        var recorded = new InMemoryGuardrailPolicyMarker { RecordedAt = DateTime.UtcNow };
        await using var existed = await LocalAccountWorld.CreateAsync(guardrails: new GuardrailStore(filePath: path, marker: recorded));
        await existed.AddUserAsync(LocalOps, AppRole.Admin);
        Assert.Equal(4, await LocalAccountRecoveryCli.RunAsync(existed.Services, [LocalOps], new StringWriter()));
        Assert.Empty(Directory.GetFiles(dir));
        Assert.Equal(0, recorded.Writes);
    }

    /// <summary>Every file of <paramref name="dir"/> with its content.</summary>
    private static string Snapshot(string dir) => string.Join("\n", Directory.GetFiles(dir).Order(StringComparer.Ordinal)
        .Select(f => Path.GetFileName(f) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));

    private static async Task<string> IssueOutputAsync(LocalAccountWorld world, string account)
    {
        var output = new StringWriter();
        Assert.Equal(0, await LocalAccountRecoveryCli.RunAsync(world.Services, [account], output));
        return output.ToString();
    }

    /// <summary>The host CLI (E3) issues a recovery code for <paramref name="account"/> and prints it.</summary>
    private static async Task<string> IssueAsync(LocalAccountWorld world, string account, bool force = false)
    {
        var output = new StringWriter();
        Assert.Equal(0, await LocalAccountRecoveryCli.RunAsync(world.Services,
            force ? [account, LocalAccountRecoveryCli.ForceFlag] : [account], output));
        var code = Regex.Match(output.ToString(), "([2-9A-HJ-NP-Z]{4}-){5}[2-9A-HJ-NP-Z]{4}").Value;
        Assert.False(string.IsNullOrEmpty(code));
        return code;
    }

    // ------------------------------------------------------------------------------------------------ E3 + E6 bindings

    [Fact]
    public async Task E6_a_federated_binding_at_sign_in_works_under_the_kill_switch_unless_it_is_blocked()
    {
        await using (var world = await BindingWorld.CreateAsync(guardrails: new AgentToolTestHelpers.FixedGuardrailStore(Killed())))
        {
            var admitted = await world.Admission.AdmitAsync(OidcIdentityBindingTests.Google("g-ops", Ops));
            Assert.True(admitted.Admitted, admitted.Reason);
            Assert.True(admitted.BoundNow);
        }

        await using (var world = await BindingWorld.CreateAsync(
                         guardrails: new AgentToolTestHelpers.FixedGuardrailStore(Killed(KillSwitchExemptions.FederatedBindingAtSignIn))))
        {
            var refused = await world.Admission.AdmitAsync(OidcIdentityBindingTests.Google("g-ops", Ops));

            Assert.False(refused.Admitted);
            Assert.Equal(AuthAuditReasons.KillSwitch, refused.Reason);
            Assert.Empty(world.Store.Snapshot().Bindings);
            var row = Assert.Single(world.Audit.Rows, r => r.Action == AuthAuditLog.IdentityBoundAction);
            Assert.False(row.Success);
            Assert.Contains("rule=read-only-mode", row.Details);
            Assert.DoesNotContain("g-ops", row.Details);   // the subject is not recorded, its fingerprint is

            // The person is told why, not the general refusal.
            var context = OidcIdentityBindingTests.Ticket(world.Http(), "Google", new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "g-ops"), new Claim(ClaimTypes.Email, Ops),
                new Claim(AuthConstants.EmailVerifiedClaim, "True"),
            ], "Google"));
            await FederatedSignIn.WhitelistGateAsync(context, "");
            Assert.Equal("/login?error=kill_switch", context.Response.Headers.Location.ToString());
            Assert.Empty(world.Store.Snapshot().Bindings);
        }
    }

    [Fact]
    public async Task E3_E6_with_a_fail_closed_store_binding_at_sign_in_is_blocked_and_the_binding_cli_still_confirms()
    {
        await using var world = await BindingWorld.CreateAsync(trustOidc: false, guardrails: await FailClosedStoreAsync());

        var refused = await world.Admission.AdmitAsync(OidcIdentityBindingTests.Google("g-ops", Ops));
        Assert.Equal(AuthAuditReasons.KillSwitch, refused.Reason);
        Assert.Empty(world.Store.Snapshot().Bindings);
        var row = Assert.Single(world.Audit.Rows, r => r.Action == AuthAuditLog.IdentityBoundAction);
        Assert.False(row.Success);
        Assert.Contains("rule=read-only-mode", row.Details);

        // An untrusted provider's sign-in waits for a person; the host CLI (E3) confirms it.
        await world.Admission.AdmitAsync(OidcIdentityBindingTests.Oidc("o-ops", Ops));
        Assert.Equal(0, await IdentityBindingCli.RunAsync(world.CliServices(), [Ops], new StringWriter()));
        var bound = world.Store.Find(OidcIdentityBindingTests.OidcIssuer, "o-ops");
        Assert.NotNull(bound);
        Assert.Equal(IdentityBindingMethods.HostCli, bound.Method);
    }
    // ------------------------------------------------------------------------------------------------ A1.1 records

    [Fact]
    public async Task A1_1_a_pending_request_recorded_under_the_kill_switch_grants_nothing_until_it_is_confirmed()
    {
        // Contract 0009 A1.1: sign-in bookkeeping is a record, not a mutation, and keeps running under the switch. A
        // pending request takes effect only through E3 or a gateway confirmation, and the latter is refused.
        var policy = Killed();
        await using var world = await LocalAccountWorld.CreateAsync(policy: policy);
        const string person = "person@local.test";
        await world.Roles.SetRoleAsync(person, AppRole.Operator);
        var admission = new FederatedAdmission(world.Bindings, world.Roles, world.Whitelist, world.Configuration);
        var claims = new FederatedSignInClaims("oidc", "https://idp.local.test/", "o-person", person, "true",
            EmailIsStandardClaim: true);   // OIDC is not trusted by default: pending

        var first = await admission.AdmitAsync(claims);
        Assert.False(first.Admitted);
        Assert.Equal(AuthAuditReasons.PendingConfirmation, first.Reason);
        Assert.Single(world.Bindings.Snapshot().Pending);                    // recorded while the switch is on
        Assert.False((await admission.AdmitAsync(claims)).Admitted);         // and it grants nothing
        var request = Assert.Single(world.Bindings.Snapshot().Pending).Id;   // a new sign-in renews the request
        Assert.Empty(world.Bindings.Snapshot().Bindings);

        // An Admin's confirmation is a mutation: the kill switch refuses it, and nothing is bound.
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root));
        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => admin.Mutations.RunAsync(
            "identity.binding_confirm", new { binding = request }, (ResolvedTarget _, CancellationToken ct) => world.Bindings.ConfirmAsync(request, ct)));
        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Contains("denied:identity.binding_confirm:Denied", world.Sink.Events);
        Assert.Empty(world.Bindings.Snapshot().Bindings);
        Assert.Equal(request, Assert.Single(world.Bindings.Snapshot().Pending).Id);
        Assert.False((await admission.AdmitAsync(claims)).Admitted);
        request = Assert.Single(world.Bindings.Snapshot().Pending).Id;

        // Confirmed with the switch off, the binding signs in under the switch again and its sign-in day is recorded.
        policy.ReadOnlyMode = false;
        Assert.True((await admin.Mutations.RunAsync("identity.binding_confirm", new { binding = request },
            (ResolvedTarget _, CancellationToken ct) => world.Bindings.ConfirmAsync(request, ct))).Success);
        policy.ReadOnlyMode = true;
        Assert.True((await admission.AdmitAsync(claims)).Admitted);
        Assert.NotNull(Assert.Single(world.Bindings.Snapshot().Bindings).LastSignInDay);
    }
}
