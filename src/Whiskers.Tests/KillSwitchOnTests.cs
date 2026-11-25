using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0023 (D8): <c>guardrails.kill_switch_on</c> changes only <c>ReadOnlyMode</c>, and a
/// <c>guardrails.save</c> that started before the last kill-switch toggle keeps the stored value (a version
/// compare-and-set): a late — abandoned — save never releases the brake a toggle engaged after it.
/// </summary>
public sealed class KillSwitchOnTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-0023-d8-" + Guid.NewGuid().ToString("N"));

    private static readonly AgentPrincipal Admin =
        new(AgentPrincipalKind.WebUser, "admin@x", McpPermissionLevels.Admin, null, UserEmail: "admin@x");

    private static readonly AgentPrincipal Operator =
        new(AgentPrincipalKind.WebUser, "ops@x", McpPermissionLevels.Write, null, UserEmail: "ops@x");

    public KillSwitchOnTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<GuardrailStore> StoreAsync() =>
        await GuardrailStoreTestExtensions.FirstStartStore(Path.Combine(_dir, $"guardrails-{Guid.NewGuid():N}.json"))
            .StartAsync();

    private static Task SaveAsync(GuardrailStore store, Whiskers.Configuration.GuardrailConfig config) =>
        GatewayArrange.RunAsync("guardrails.save", () => store.SaveConfigAsync(config, Admin));

    private static Task OnAsync(GuardrailStore store) =>
        GatewayArrange.RunAsync("guardrails.kill_switch_on", () => store.EnableKillSwitchAsync(Admin));

    private static Task OffAsync(GuardrailStore store) =>
        GatewayArrange.RunAsync("guardrails.kill_switch_off", () => store.DisableKillSwitchAsync(Admin));

    [Fact]
    public async Task Switching_on_changes_only_the_kill_switch_and_counts_the_toggle()
    {
        var store = await StoreAsync();
        var before = store.Current;
        Assert.False(before.ReadOnlyMode);
        var version = store.KillSwitchVersion;

        await OnAsync(store);

        var after = store.Current;
        Assert.True(after.ReadOnlyMode);
        after.ReadOnlyMode = false;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(after));
        Assert.Equal(version + 1, store.KillSwitchVersion);

        // Already on: nothing written, nothing counted.
        await OnAsync(store);
        Assert.Equal(version + 1, store.KillSwitchVersion);
    }

    [Fact]
    public async Task A_save_started_before_switching_on_keeps_the_brake_on()
    {
        var store = await StoreAsync();
        var stale = store.Config;                 // read before the toggle: kill switch off
        stale.ActivePolicy().MaxActionsPerSession = 7;

        await OnAsync(store);                     // the brake is engaged meanwhile
        await SaveAsync(store, stale);            // the late save ends

        Assert.True(store.Current.ReadOnlyMode);
        Assert.Equal(7, store.Current.MaxActionsPerSession);   // the rest of the save is kept
        var reloaded = await new GuardrailStore(filePath: StorePath(store)).StartAsync();
        Assert.True(reloaded.Current.ReadOnlyMode);
    }

    [Fact]
    public async Task A_save_started_before_switching_off_does_not_switch_it_on_again()
    {
        var store = await StoreAsync();
        await OnAsync(store);
        var stale = store.Config;                 // kill switch on in this copy
        await OffAsync(store);

        await SaveAsync(store, stale);

        Assert.False(store.Current.ReadOnlyMode);
    }

    [Fact]
    public async Task A_save_started_after_the_last_toggle_sets_the_kill_switch()
    {
        var store = await StoreAsync();
        await OnAsync(store);
        await OffAsync(store);
        var fresh = store.Config;
        Assert.Equal(store.KillSwitchVersion, fresh.KillSwitchVersion);
        fresh.ActivePolicy().ReadOnlyMode = true;

        await SaveAsync(store, fresh);

        Assert.True(store.Current.ReadOnlyMode);
    }

    [Fact]
    public async Task The_version_stamp_is_never_written_to_the_file()
    {
        var store = await StoreAsync();
        await OnAsync(store);
        var config = store.Config;
        config.ActivePolicy().MaxActionsPerSession = 9;
        await SaveAsync(store, config);

        Assert.DoesNotContain("KillSwitchVersion", await File.ReadAllTextAsync(StorePath(store)));
    }

    [Fact]
    public async Task Only_an_admin_switches_it_on_and_a_store_that_could_not_load_refuses()
    {
        var store = await StoreAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            GatewayArrange.RunAsync("guardrails.kill_switch_on", () => store.EnableKillSwitchAsync(Operator)));
        Assert.False(store.Current.ReadOnlyMode);

        var broken = Path.Combine(_dir, "broken.json");
        await File.WriteAllTextAsync(broken, "{ not json");
        var failClosed = new GuardrailStore(filePath: broken);
        await failClosed.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GatewayArrange.RunAsync("guardrails.kill_switch_on", () => failClosed.EnableKillSwitchAsync(Admin)));
    }

    [Fact]
    public async Task The_read_back_is_the_stored_kill_switch()
    {
        var store = await StoreAsync();
        var services = new ServiceCollection().AddSingleton<IGuardrailStore>(store).BuildServiceProvider();
        var verifier = new KillSwitchOnVerifier(services);

        await Assert.ThrowsAsync<MutationVerificationFailedException>(() => verifier.VerifyAsync(null!, null, default));
        await OnAsync(store);
        Assert.Equal(VerificationStatus.Verified, await verifier.VerifyAsync(null!, null, default));
        Assert.Contains(MutationVerifiers.Factories, f => f(services).CapabilityId == "guardrails.kill_switch_on");
    }

    [Fact]
    public void It_is_an_admin_only_root_equivalent_ui_capability_on_whiskers_itself_with_a_read_back()
    {
        var capability = new CapabilityCatalog().Find("guardrails.kill_switch_on");
        Assert.NotNull(capability);
        Assert.Equal(McpPermissionLevels.Admin, capability.RequiredLevel);
        Assert.Null(capability.McpTool);                                   // UI only: not on MCP, not for the agent
        Assert.Equal(CapabilityRisk.RootEquivalent, capability.Risk);
        Assert.Contains("guardrails.kill_switch_on", CapabilityCatalog.Singletons);
        Assert.Contains("guardrails.kill_switch_on", MutationGateway.KillSwitchToggles);
        Assert.Equal(VerificationKind.ReadBack, VerificationContracts.Find("guardrails.kill_switch_on")!.Kind);
        Assert.Equal("guardrails.kill_switch_on", GuardrailMutationPolicy.KillSwitchOn);
    }

    [Fact]
    public async Task A_save_without_a_stamp_keeps_the_stored_kill_switch()
    {
        var store = await StoreAsync();
        await OnAsync(store);

        var unstamped = store.Config;
        unstamped.KillSwitchVersion = null;
        unstamped.ActivePolicy().ReadOnlyMode = false;
        unstamped.ActivePolicy().MaxActionsPerSession = 5;
        await SaveAsync(store, unstamped);
        Assert.True(store.Current.ReadOnlyMode);
        Assert.Equal(5, store.Current.MaxActionsPerSession);

        var policy = store.Current;
        policy.ReadOnlyMode = false;
        await GatewayArrange.RunAsync("guardrails.save", () => store.SaveAsync(policy, Admin));
        Assert.True(store.Current.ReadOnlyMode);

        await OffAsync(store);
        policy.ReadOnlyMode = true;
        await GatewayArrange.RunAsync("guardrails.save", () => store.SaveAsync(policy, Admin));
        Assert.True(store.Current.ReadOnlyMode);    // engaging is the safe side and stays possible
    }

    [Fact]
    public async Task A_save_that_engages_the_brake_counts_so_an_older_copy_cannot_release_it()
    {
        var store = await StoreAsync();
        var first = store.Config;
        var second = store.Config;                // both pages opened while the brake was off
        var version = store.KillSwitchVersion;

        first.ActivePolicy().ReadOnlyMode = true;
        await SaveAsync(store, first);
        Assert.Equal(version + 1, store.KillSwitchVersion);

        second.ActivePolicy().MaxActionsPerSession = 4;
        await SaveAsync(store, second);           // its copy still has the brake off

        Assert.True(store.Current.ReadOnlyMode);
        Assert.Equal(4, store.Current.MaxActionsPerSession);
    }

    [Fact]
    public async Task Switching_on_takes_effect_while_a_save_holds_the_store_and_is_written_after_it()
    {
        var marker = new InMemoryGuardrailPolicyMarker { WriteFails = new InvalidOperationException("db down") };
        var path = Path.Combine(_dir, "guardrails-held.json");
        var store = await new GuardrailStore(filePath: path, marker: marker).StartAsync();
        Assert.False(store.MarkerRecorded);       // so the next save writes the record, under the store's lock
        marker.WriteFails = null;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        marker.HoldWrites = hold;

        var stale = store.Config;
        stale.ActivePolicy().MaxActionsPerSession = 6;
        var save = SaveAsync(store, stale);       // hangs on the database while holding the store lock
        await marker.WriteHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            await GatewayArrange.RunAsync("guardrails.kill_switch_on",
                () => store.EnableKillSwitchAsync(Admin, deadline.Token));

        Assert.True(store.Current.ReadOnlyMode);  // enforced at once, not after the save
        Assert.True(store.Config.ActivePolicy().ReadOnlyMode);
        Assert.False(save.IsCompleted);
        // …but not stored yet: said so, and not verified as stored (a restart now would lose it).
        Assert.True(store.KillSwitchNotWritten);
        var services = new ServiceCollection().AddSingleton<IGuardrailStore>(store).BuildServiceProvider();
        await Assert.ThrowsAsync<MutationNotVerifiableException>(() =>
            new KillSwitchOnVerifier(services).VerifyAsync(null!, null, default));

        marker.HoldWrites = null;
        hold.SetResult();
        await save;
        Assert.True(store.Current.ReadOnlyMode);
        Assert.Equal(6, store.Current.MaxActionsPerSession);

        // The background write follows once the lock is free.
        var deadlineAt = DateTime.UtcNow.AddSeconds(10);
        bool written;
        do
        {
            written = (await new GuardrailStore(filePath: path).StartAsync()).Current.ReadOnlyMode;
            if (!written) await Task.Delay(50);
        }
        while (!written && DateTime.UtcNow < deadlineAt);
        Assert.True(written);
        Assert.False(store.KillSwitchNotWritten);
        Assert.Equal(VerificationStatus.Verified, await new KillSwitchOnVerifier(services).VerifyAsync(null!, null, default));
    }

    [Fact]
    public async Task Switching_off_while_a_switch_on_is_pending_releases_it_and_the_pending_write_does_not_return()
    {
        var marker = new InMemoryGuardrailPolicyMarker { WriteFails = new InvalidOperationException("db down") };
        var path = Path.Combine(_dir, "guardrails-pending.json");
        var store = await new GuardrailStore(filePath: path, marker: marker).StartAsync();
        marker.WriteFails = null;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        marker.HoldWrites = hold;
        var save = SaveAsync(store, store.Config);
        await marker.WriteHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await GatewayArrange.RunAsync("guardrails.kill_switch_on",
                () => store.EnableKillSwitchAsync(Admin, deadline.Token));
        Assert.True(store.Current.ReadOnlyMode);

        var off = OffAsync(store);                // waits for the store lock like the pending write
        marker.HoldWrites = null;
        hold.SetResult();
        await save;
        await off;

        await Task.Delay(200);                    // room for the background write, which must find nothing to do
        Assert.False(store.Current.ReadOnlyMode);
        Assert.False((await new GuardrailStore(filePath: path).StartAsync()).Current.ReadOnlyMode);
    }

    private string StorePath(GuardrailStore _) =>
        Directory.GetFiles(_dir, "guardrails-*.json").Single();
}
