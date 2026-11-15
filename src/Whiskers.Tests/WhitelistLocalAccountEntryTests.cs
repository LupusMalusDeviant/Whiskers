using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0017 H2/H3: the whitelist entry of a local account an Admin creates and deletes. <c>CreateAsync</c> lists the
/// address in an enabled whitelist (through the access-control gate, inside the decided <c>account.create</c>) and
/// records that it did; <c>DeleteAsync</c> removes only such an entry. A disabled whitelist is never touched. Real
/// Identity store, real roles/whitelist stores, real session validator and gateway (<see cref="LocalAccountWorld"/>).
/// </summary>
[Collection("WebAppBoot")]
public sealed class WhitelistLocalAccountEntryTests
{
    private const string New = "new@local.test";
    private const string Listed = "listed@local.test";

    private static async Task<LocalAccountResult> CreateAsync(LocalAccountWorld world, string email, AppRole role = AppRole.Operator,
        IWhitelistService? whitelist = null)
    {
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root), whitelist: whitelist);
        return await admin.Mutations.RunAsync("account.create", new { account = email, role = role.ToString() },
            (ResolvedTarget _, CancellationToken ct) => admin.Accounts.CreateAsync(email, role, ct));
    }

    private static async Task<LocalAccountResult> DeleteAsync(LocalAccountWorld world, string email, IWhitelistService? whitelist = null)
    {
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root), whitelist: whitelist);
        return await admin.Mutations.RunAsync("account.delete", new { account = email },
            (ResolvedTarget _, CancellationToken ct) => admin.Accounts.DeleteAsync(email, ct));
    }

    private static async Task<string?> AccountIdAsync(LocalAccountWorld world, string email)
    {
        using var scope = world.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email))?.Id;
    }

    /// <summary>The completion line of the last <paramref name="capabilityId"/> event in the gateway's sink.</summary>
    private static string CompletionOf(LocalAccountWorld world, string capabilityId)
    {
        var lines = world.Sink.Text.Split('\n');
        var events = world.Sink.Events;
        var begin = events.LastIndexOf($"begin:{capabilityId}");
        Assert.True(begin >= 0, $"no {capabilityId} event");
        var complete = events.FindIndex(begin + 1, e => e.StartsWith("complete:", StringComparison.Ordinal));
        Assert.True(complete > begin, $"the {capabilityId} event was not completed");
        return lines[complete];
    }

    /// <summary>INV-46: the whitelist write is recorded as the config.version entry of that operation's event.</summary>
    private static void AssertWhitelistVersionIn(LocalAccountWorld world, string capabilityId)
    {
        var completion = CompletionOf(world, capabilityId);
        Assert.Contains($"Action = {ConfigChange.VersionAction}, TargetType = config, TargetId = whitelist", completion);
    }

    private static async Task<LocalAccountWorld> EnabledAsync(params string[] extra)
    {
        var world = await LocalAccountWorld.CreateAsync();
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root, .. extra] });
        return world;
    }

    // ------------------------------------------------------------------------------------------------ H2

    [Fact]
    public async Task Create_adds_the_address_to_an_enabled_whitelist_and_records_the_account()
    {
        await using var world = await EnabledAsync();

        Assert.True((await CreateAsync(world, New)).Success);

        var data = world.Whitelist.GetWhitelist();
        Assert.True(data.Enabled);
        Assert.Equal([LocalAccountWorld.Root, New], data.Emails);
        Assert.Equal(await AccountIdAsync(world, New), Assert.Single(data.AddedForAccounts!).Value);
        // The same audited operation: the whitelist's version entry is part of the account.create event.
        AssertWhitelistVersionIn(world, "account.create");
        // Emails stays a list of strings; the record is a sibling map.
        var json = await File.ReadAllTextAsync(world.WhitelistPath);
        Assert.Contains("\"Emails\":[", json.Replace(" ", "").Replace("\n", "").Replace("\r", ""));
        Assert.Contains("AddedForAccounts", json);
    }

    [Fact]
    public async Task A_disabled_whitelist_is_byte_identical_after_create()
    {
        await using var world = await LocalAccountWorld.CreateAsync();
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = false, Emails = ["someone@local.test"] });
        var before = await File.ReadAllBytesAsync(world.WhitelistPath);

        Assert.True((await CreateAsync(world, New)).Success);

        Assert.Equal(before, await File.ReadAllBytesAsync(world.WhitelistPath));
        Assert.Equal(["someone@local.test"], world.Whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task A_refused_create_adds_nothing()
    {
        await using var world = await EnabledAsync("viewer@local.test");
        await world.AddUserAsync(New, AppRole.Viewer);   // exists already
        await world.AddUserAsync("viewer@local.test", AppRole.Viewer);
        var before = await File.ReadAllBytesAsync(world.WhitelistPath);

        Assert.Equal(LocalAccountErrors.AlreadyExists, (await CreateAsync(world, New)).Error);
        // A caller who is no Admin: the service itself refuses (whatever a page or the gateway decided).
        using (var notAdmin = world.As(await world.SessionOfAsync("viewer@local.test")))
            Assert.Equal(LocalAccountErrors.NotAuthorized,
                (await notAdmin.Accounts.CreateAsync("other@local.test", AppRole.Viewer)).Error);

        Assert.Equal(before, await File.ReadAllBytesAsync(world.WhitelistPath));
    }

    [Fact]
    public async Task An_address_listed_before_gets_no_record()
    {
        await using var world = await EnabledAsync(Listed);

        Assert.True((await CreateAsync(world, Listed)).Success);

        Assert.Equal([LocalAccountWorld.Root, Listed], world.Whitelist.GetWhitelist().Emails);
        Assert.Null(world.Whitelist.GetWhitelist().AddedForAccounts);
    }

    [Fact]
    public async Task A_failed_whitelist_write_undoes_the_account_and_its_role()
    {
        await using var world = await EnabledAsync();
        var failing = new FailingWhitelist(world.Whitelist) { FailAdd = true };

        await Assert.ThrowsAsync<IOException>(() => CreateAsync(world, New, whitelist: failing));

        Assert.Null(await AccountIdAsync(world, New));
        Assert.False(world.Roles.HasExplicitRole(New));
        Assert.Equal([LocalAccountWorld.Root], world.Whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task An_unlisted_address_is_refused_on_local_and_federated_sign_in()
    {
        // Unchanged enforcement: an account that did not come through CreateAsync (the whitelist was enabled after it
        // existed) has no entry — the local session and a federated sign-in with its address are both refused.
        await using var world = await EnabledAsync();
        const string person = "person@local.test";
        await world.AddUserAsync(person, AppRole.Operator);

        Assert.False((await world.Validator.CheckAsync(await world.SessionOfAsync(person))).Valid);
        var admission = new FederatedAdmission(world.Bindings, world.Roles, world.Whitelist, world.Configuration);
        var decision = await admission.AdmitAsync(new FederatedSignInClaims("google", AuthConstants.GoogleIssuer, "g-person",
            person, "true", EmailIsStandardClaim: true));
        Assert.False(decision.Admitted);
        Assert.Equal(AuthAuditReasons.NotWhitelisted, decision.Reason);
    }

    // ------------------------------------------------------------------------------------------------ H3

    [Fact]
    public async Task Delete_removes_only_its_own_entry()
    {
        await using var world = await EnabledAsync(Listed);
        Assert.True((await CreateAsync(world, New)).Success);
        Assert.True((await CreateAsync(world, Listed)).Success);

        Assert.True((await DeleteAsync(world, New)).Success);
        AssertWhitelistVersionIn(world, "account.delete");   // the removal is part of the account.delete event
        Assert.True((await DeleteAsync(world, Listed)).Success);

        // The entry H2 added is gone; the address listed before the account was created stays.
        Assert.Equal([LocalAccountWorld.Root, Listed], world.Whitelist.GetWhitelist().Emails);
        Assert.Null(world.Whitelist.GetWhitelist().AddedForAccounts);
    }

    [Fact]
    public async Task An_unrelated_whitelist_save_then_delete_still_removes_the_entry()
    {
        await using var world = await EnabledAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        // The Settings page saves a new list without the record (it never passes one).
        var current = world.Whitelist.GetWhitelist();
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [.. current.Emails, "other@local.test"] });
        Assert.NotNull(world.Whitelist.GetWhitelist().AddedForAccounts);

        Assert.True((await DeleteAsync(world, New)).Success);

        Assert.Equal([LocalAccountWorld.Root, "other@local.test"], world.Whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task An_entry_removed_and_re_added_by_hand_stays_after_delete()
    {
        await using var world = await EnabledAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root] });
        Assert.Null(world.Whitelist.GetWhitelist().AddedForAccounts);   // the removal cleared the record
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root, New] });

        Assert.True((await DeleteAsync(world, New)).Success);

        Assert.Equal([LocalAccountWorld.Root, New], world.Whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task A_record_a_caller_passes_is_ignored()
    {
        await using var world = await EnabledAsync();
        await world.AddUserAsync(Listed, AppRole.Viewer);
        var id = await AccountIdAsync(world, Listed);
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData
        {
            Enabled = true, Emails = [LocalAccountWorld.Root, Listed], AddedForAccounts = new() { [Listed] = id! }
        });
        Assert.Null(world.Whitelist.GetWhitelist().AddedForAccounts);

        Assert.True((await DeleteAsync(world, Listed)).Success);
        Assert.Contains(Listed, world.Whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task A_failed_whitelist_removal_undoes_the_deletion()
    {
        await using var world = await EnabledAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        var id = await AccountIdAsync(world, New);
        var failing = new FailingWhitelist(world.Whitelist) { FailRemove = true };

        await Assert.ThrowsAsync<IOException>(() => DeleteAsync(world, New, whitelist: failing));

        Assert.Equal(id, await AccountIdAsync(world, New));
        Assert.Equal(AppRole.Operator, world.Roles.GetRole(New));
        Assert.True(world.Roles.HasExplicitRole(New));
        Assert.Contains(New, world.Whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task A_failed_account_deletion_puts_the_entry_back()
    {
        await using var world = await EnabledAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        var id = await AccountIdAsync(world, New);

        // The Identity store refuses the deletion after the whitelist entry and the role went: both come back.
        using var admin = world.As(await world.SessionOfAsync(LocalAccountWorld.Root),
            users: sp => new RefusingDeleteUsers(sp));
        var result = await admin.Mutations.RunAsync("account.delete", new { account = New },
            (ResolvedTarget _, CancellationToken ct) => admin.Accounts.DeleteAsync(New, ct));

        Assert.False(result.Success);
        Assert.Equal(id, await AccountIdAsync(world, New));
        Assert.True(world.Roles.HasExplicitRole(New));
        Assert.Contains(New, world.Whitelist.GetWhitelist().Emails);
        Assert.Equal(id, Assert.Single(world.Whitelist.GetWhitelist().AddedForAccounts!).Value);
        // Removal and restore are both recorded in the account.delete event (two whitelist version entries).
        var completion = CompletionOf(world, "account.delete");
        Assert.Equal(2, completion.Split($"Action = {ConfigChange.VersionAction}, TargetType = config, TargetId = whitelist").Length - 1);
    }

    [Fact]
    public async Task A_create_that_fails_after_its_whitelist_write_takes_the_entry_back()
    {
        await using var world = await EnabledAsync();
        var failing = new FailingWhitelist(world.Whitelist) { FailAfterAdd = true };

        await Assert.ThrowsAsync<IOException>(() => CreateAsync(world, New, whitelist: failing));

        Assert.Null(await AccountIdAsync(world, New));
        Assert.False(world.Roles.HasExplicitRole(New));
        Assert.Equal([LocalAccountWorld.Root], world.Whitelist.GetWhitelist().Emails);
        Assert.Null(world.Whitelist.GetWhitelist().AddedForAccounts);
    }

    [Fact]
    public async Task A_delete_that_fails_after_its_whitelist_removal_puts_the_entry_back()
    {
        await using var world = await EnabledAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        var id = await AccountIdAsync(world, New);
        var failing = new FailingWhitelist(world.Whitelist) { FailAfterRemove = true };

        await Assert.ThrowsAsync<IOException>(() => DeleteAsync(world, New, whitelist: failing));

        Assert.Equal(id, await AccountIdAsync(world, New));
        Assert.True(world.Roles.HasExplicitRole(New));
        Assert.Equal([LocalAccountWorld.Root, New], world.Whitelist.GetWhitelist().Emails);
        Assert.Equal(id, Assert.Single(world.Whitelist.GetWhitelist().AddedForAccounts!).Value);
    }

    [Fact]
    public async Task A_failed_removal_never_lists_an_address_the_account_had_no_entry_for()
    {
        // Created while the whitelist was disabled (no entry, no record), then the whitelist was enabled without it.
        await using var world = await LocalAccountWorld.CreateAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        await world.Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [LocalAccountWorld.Root] });
        var failing = new FailingWhitelist(world.Whitelist) { FailRemove = true };

        await Assert.ThrowsAsync<IOException>(() => DeleteAsync(world, New, whitelist: failing));

        Assert.Equal([LocalAccountWorld.Root], world.Whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task A_removal_cancelled_before_the_gate_never_re_lists_an_address_removed_by_hand_meanwhile()
    {
        // Review M1: the removal never wrote (cancelled while it waited for the gate); another Admin removed the entry by
        // hand in the meantime. The undo must not put the address back — only a persisted removal is undone.
        await using var world = await EnabledAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        var id = await AccountIdAsync(world, New);
        var racing = new FailingWhitelist(world.Whitelist) { HandRemovalThenCancel = true };

        await Assert.ThrowsAnyAsync<Exception>(() => DeleteAsync(world, New, whitelist: racing));

        Assert.Equal([LocalAccountWorld.Root], world.Whitelist.GetWhitelist().Emails);
        Assert.False(world.Whitelist.IsEmailAllowed(New));
        Assert.Equal(id, await AccountIdAsync(world, New));   // the deletion was undone
        Assert.True(world.Roles.HasExplicitRole(New));
    }

    [Fact]
    public async Task The_receipt_says_whether_a_removal_was_persisted()
    {
        await using var world = await EnabledAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        var id = (await AccountIdAsync(world, New))!;

        var cancelledReceipt = new WhitelistWriteReceipt();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GatewayArrange.RunAsync("account.delete",
            () => world.Whitelist.RemoveLocalAccountEntryAsync(New, id, cancelled.Token, cancelledReceipt)));
        Assert.False(cancelledReceipt.Persisted);

        var receipt = new WhitelistWriteReceipt();
        Assert.True(await GatewayArrange.RunAsync("account.delete",
            () => world.Whitelist.RemoveLocalAccountEntryAsync(New, id, receipt: receipt)));
        Assert.True(receipt.Persisted);
    }

    [Fact]
    public async Task A_whitelist_carrying_the_record_round_trips_through_a_restart()
    {
        await using var world = await EnabledAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        var id = await AccountIdAsync(world, New);

        var restarted = new WhitelistService(world.Configuration, world.Roles,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WhitelistService>.Instance,
            new Whiskers.Configuration.DataPathOptions(Path.GetDirectoryName(world.WhitelistPath)!));
        await restarted.InitializeAsync();

        var data = restarted.GetWhitelist();
        Assert.Equal([LocalAccountWorld.Root, New], data.Emails);
        var record = Assert.Single(data.AddedForAccounts!);
        Assert.Equal(New, record.Key);
        Assert.Equal(id, record.Value);
        // And H3 works on what was loaded.
        Assert.True(await GatewayArrange.RunAsync("account.delete", () => restarted.RemoveLocalAccountEntryAsync(New, id!)));
        Assert.Equal([LocalAccountWorld.Root], restarted.GetWhitelist().Emails);
    }

    [Fact]
    public async Task A_cancelled_whitelist_write_writes_nothing()
    {
        await using var world = await EnabledAsync();
        var before = await File.ReadAllBytesAsync(world.WhitelistPath);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GatewayArrange.RunAsync("account.create",
            () => world.Whitelist.AddLocalAccountEntryAsync(New, "id-1", cancelled.Token)));

        Assert.Equal(before, await File.ReadAllBytesAsync(world.WhitelistPath));
    }

    [Fact]
    public async Task A_local_account_whitelist_write_outside_its_decision_is_refused()
    {
        // INV-46: no whitelist write without the event of a decided account operation.
        await using var world = await EnabledAsync();
        Assert.True((await CreateAsync(world, New)).Success);
        var id = (await AccountIdAsync(world, New))!;
        var before = await File.ReadAllBytesAsync(world.WhitelistPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Whitelist.AddLocalAccountEntryAsync("x@local.test", "id-x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Whitelist.RemoveLocalAccountEntryAsync(New, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Whitelist.RestoreLocalAccountEntryAsync("y@local.test", "id-y"));
        // Nor inside another decision (a whitelist save is not an account operation).
        await Assert.ThrowsAsync<InvalidOperationException>(() => GatewayArrange.RunAsync("config.whitelist",
            () => world.Whitelist.AddLocalAccountEntryAsync("x@local.test", "id-x")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GatewayArrange.RunAsync("account.create",
            () => world.Whitelist.RestoreLocalAccountEntryAsync("y@local.test", "id-y")));

        Assert.Equal(before, await File.ReadAllBytesAsync(world.WhitelistPath));
    }

    /// <summary>The world's whitelist, with the local-account writes failing on demand — before or after the write.</summary>
    private sealed class FailingWhitelist(IWhitelistService inner) : IWhitelistService
    {
        public bool FailAdd { get; init; }
        public bool FailRemove { get; init; }
        public bool FailAfterAdd { get; init; }
        public bool FailAfterRemove { get; init; }
        public bool HandRemovalThenCancel { get; init; }
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
        public bool IsEmailAllowed(string? email) => inner.IsEmailAllowed(email);
        public WhitelistData GetWhitelist() => inner.GetWhitelist();
        public Task SaveWhitelistAsync(WhitelistData data) => inner.SaveWhitelistAsync(data);
        public Task AddFirstAdminAsync(string email, AccessControlWriteGate.Lease lease) => inner.AddFirstAdminAsync(email, lease);
        public async Task<bool> AddLocalAccountEntryAsync(string email, string accountId, CancellationToken ct = default)
        {
            if (FailAdd) throw new IOException("whitelist.json could not be written");
            var added = await inner.AddLocalAccountEntryAsync(email, accountId, ct);
            if (FailAfterAdd) throw new IOException("failed after the write");
            return added;
        }

        public async Task<bool> RemoveLocalAccountEntryAsync(string email, string accountId, CancellationToken ct = default, WhitelistWriteReceipt? receipt = null)
        {
            if (FailRemove) throw new IOException("whitelist.json could not be written");
            if (HandRemovalThenCancel)
            {
                // While this removal waits for the gate, another Admin removes the entry by hand; then the wait is cancelled.
                var now = inner.GetWhitelist();
                await inner.SaveWhitelistAsync(new WhitelistData
                {
                    Enabled = now.Enabled, Emails = now.Emails.Where(e => !string.Equals(e, email, StringComparison.OrdinalIgnoreCase)).ToList()
                });
                throw new OperationCanceledException("cancelled while waiting for the gate");
            }
            var removed = await inner.RemoveLocalAccountEntryAsync(email, accountId, ct, receipt);
            if (FailAfterRemove) throw new IOException("failed after the write");
            return removed;
        }
        public Task RestoreLocalAccountEntryAsync(string email, string accountId, CancellationToken ct = default) =>
            inner.RestoreLocalAccountEntryAsync(email, accountId, ct);
    }

    /// <summary>A user manager whose deletion fails (the store answers with an error).</summary>
    private sealed class RefusingDeleteUsers(IServiceProvider sp) : UserManager<AppUser>(
        sp.GetRequiredService<IUserStore<AppUser>>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>(),
        sp.GetRequiredService<IPasswordHasher<AppUser>>(),
        sp.GetServices<IUserValidator<AppUser>>(), sp.GetServices<IPasswordValidator<AppUser>>(),
        sp.GetRequiredService<ILookupNormalizer>(), sp.GetRequiredService<IdentityErrorDescriber>(), sp,
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UserManager<AppUser>>>())
    {
        public override Task<IdentityResult> DeleteAsync(AppUser user) =>
            Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "Refused", Description = "refused by the test" }));
    }
}
