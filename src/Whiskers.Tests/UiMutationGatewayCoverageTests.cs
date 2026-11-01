using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Registries;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;
using Whiskers.Services.Workloads.Kubernetes;

namespace Whiskers.Tests;

/// <summary>
/// R1-11: every security configuration write (settings sections, whitelist, roles, MCP permissions and keys,
/// guardrails, vault secrets, registries) goes through the Mutation Gateway as an Admin-only capability. A Viewer or
/// demoted user is refused whatever the page renders, the audit intent is written before the store is touched, the
/// kill switch refuses all of them except switching itself off, and a failed write leaves the live configuration as
/// it was.
/// </summary>
public class UiMutationGatewayCoverageTests : IDisposable
{
    /// <summary>Every security configuration write, by the interface method that performs it.</summary>
    public static readonly (Type Service, string Method, string Capability)[] Writes =
    [
        (typeof(IAppSettingsStore), nameof(IAppSettingsStore.SaveSectionAsync), "config.save"),
        (typeof(IWhitelistService), nameof(IWhitelistService.SaveWhitelistAsync), "config.whitelist"),
        (typeof(IRoleService), nameof(IRoleService.SaveRoleDataAsync), "config.roles"),
        (typeof(IMcpPermissionService), nameof(IMcpPermissionService.SavePermissionDataAsync), "config.mcp_permissions"),
        (typeof(IMcpPermissionService), nameof(IMcpPermissionService.AddApiKeyAsync), "mcp.key_create"),
        (typeof(Whiskers.Services.Agent.IAgentSettingsStore), nameof(Whiskers.Services.Agent.IAgentSettingsStore.SaveAsync), "config.agent"),
        (typeof(IMcpPermissionService), nameof(IMcpPermissionService.RemoveApiKeyAsync), "mcp.key_delete"),
        (typeof(IGuardrailStore), nameof(IGuardrailStore.SaveConfigAsync), "guardrails.save"),
        (typeof(IGuardrailStore), nameof(IGuardrailStore.SaveAsync), "guardrails.save"),
        (typeof(IGuardrailStore), nameof(IGuardrailStore.DisableKillSwitchAsync), "guardrails.kill_switch_off"),
        (typeof(IVaultService), nameof(IVaultService.SetSecretAsync), "vault.secret_set"),
        (typeof(IVaultService), nameof(IVaultService.DeleteSecretAsync), "vault.secret_delete"),
        (typeof(IRegistryConfigService), nameof(IRegistryConfigService.SaveRegistryAsync), "registry.save"),
        (typeof(IRegistryConfigService), nameof(IRegistryConfigService.DeleteRegistryAsync), "registry.delete"),
    ];

    public static TheoryData<string> Capabilities()
    {
        var data = new TheoryData<string>();
        foreach (var id in Writes.Select(w => w.Capability).Distinct()) data.Add(id);
        return data;
    }

    public static TheoryData<string> CapabilitiesTheKillSwitchStops()
    {
        var data = new TheoryData<string>();
        foreach (var id in Writes.Select(w => w.Capability).Distinct().Where(id => id != GuardrailMutationPolicy.KillSwitchOff))
            data.Add(id);
        return data;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-r111-" + Guid.NewGuid().ToString("N"));

    public UiMutationGatewayCoverageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- the inventory

    [Fact]
    public void Every_security_configuration_write_is_marked_with_an_admin_only_capability()
    {
        var catalog = new CapabilityCatalog();
        foreach (var (service, method, capability) in Writes)
        {
            var methods = service.GetMethods().Where(m => m.Name == method).ToList();
            Assert.NotEmpty(methods);
            foreach (var m in methods)
                Assert.Equal(capability, m.GetCustomAttribute<MutatingOperationAttribute>()?.CapabilityId);

            var definition = catalog.Find(capability);
            Assert.NotNull(definition);
            Assert.Equal(McpPermissionLevels.Admin, definition.RequiredLevel);
            Assert.Equal("config", definition.TargetKind);
            Assert.Null(definition.McpTool);   // the UI is the only way in; no tool and no agent reaches it
        }

        // …and the catalog holds no configuration capability this list does not know.
        Assert.Equal(
            Writes.Select(w => w.Capability).Distinct().Order(),
            catalog.All.Where(c => c.TargetKind == "config").Select(c => c.Id).Order());
    }

    [Fact]
    public void No_page_or_component_writes_configuration_past_the_gateway()
    {
        var baseline = File.ReadAllLines(Path.Combine(McpToolCatalogSnapshotTests.RepositoryRoot(), "docs", "v1",
            "mutation-bypass-baseline.txt"));
        var capabilities = Writes.Select(w => w.Capability).ToHashSet();
        var bypasses = baseline
            .Where(l => !l.StartsWith('#') && l.Contains(" -> "))
            .Where(l => capabilities.Contains(l[(l.IndexOf(" -> ", StringComparison.Ordinal) + 4)..].Trim()))
            .ToList();

        // The first-run wizard alone: it creates the first admin, before anyone exists the gateway could authorize.
        Assert.Equal(["Whiskers.Services.Setup.SetupStateService -> config.whitelist"], bypasses);
    }

    /// <summary>Every UI handler that writes security configuration, with the capability it runs as.</summary>
    public static readonly (Type Component, string Handler, string Capability)[] Handlers =
    [
        (typeof(Whiskers.Components.Pages.Settings), "SaveHealth", "config.save"),
        (typeof(Whiskers.Components.Pages.Settings), "SaveMetricAlert", "config.save"),
        (typeof(Whiskers.Components.Pages.Settings), "SaveCve", "config.save"),
        (typeof(Whiskers.Components.Pages.Settings), "SaveImageUpdate", "config.save"),
        (typeof(Whiskers.Components.Pages.Settings), "SaveTerminal", "config.save"),
        (typeof(Whiskers.Components.Pages.Settings), "SaveDns", "config.save"),
        (typeof(Whiskers.Components.Pages.Settings), "SaveDns", "vault.secret_set"),
        (typeof(Whiskers.Components.Pages.Settings), "RemoveUnusedDnsTokenAsync", "vault.secret_delete"),
        (typeof(Whiskers.Components.Pages.Settings), "ClearDnsToken", "config.save"),
        (typeof(Whiskers.Components.Pages.Settings), "AddApiKey", "mcp.key_create"),
        (typeof(Whiskers.Components.Pages.Settings), "RemoveApiKey", "mcp.key_delete"),
        (typeof(Whiskers.Components.Pages.Settings), "SaveMcpPermissions", "config.mcp_permissions"),
        (typeof(Whiskers.Components.Pages.Settings), "SaveWhitelist", "config.whitelist"),
        (typeof(Whiskers.Components.Pages.Settings), "SaveRoles", "config.roles"),
        (typeof(Whiskers.Components.Pages.Settings), "AddVaultSecret", "vault.secret_set"),
        (typeof(Whiskers.Components.Pages.Settings), "DeleteVaultSecret", "vault.secret_delete"),
        // R1-30: the notification, AI chat and agent editors save through the shared vault-backed save.
        (typeof(Whiskers.Services.Vault.VaultBackedSettingsSave), "SaveAppSettingsAsync", "config.save"),
        (typeof(Whiskers.Services.Vault.VaultBackedSettingsSave), "SaveAgentAsync", "config.agent"),
        (typeof(Whiskers.Services.Vault.VaultBackedSettingsSave), "StageAsync", "vault.secret_set"),
        (typeof(Whiskers.Services.Vault.VaultBackedSettingsSave), "RemoveAsync", "vault.secret_delete"),
        (typeof(Whiskers.Components.Pages.GuardrailsView), "SaveAll", "guardrails.save"),
        (typeof(Whiskers.Components.Pages.GuardrailsView), "DisableKillSwitch", "guardrails.kill_switch_off"),
        (typeof(Whiskers.Components.Pages.AiOperationsView), "CreateKey", "mcp.key_create"),
        (typeof(Whiskers.Components.Pages.AiOperationsView), "ActivatePreset", "guardrails.save"),
        (typeof(Whiskers.Components.Pages.Servers), "PersistServerRecordAsync", "vault.secret_set"),
        (typeof(Whiskers.Components.Pages.Servers), "RemoveUnusedKubeconfigAsync", "vault.secret_delete"),
        (typeof(Whiskers.Components.Pages.Servers), "RemoveUnusedCloudKeyAsync", "vault.secret_delete"),
        (typeof(Whiskers.Components.Shared.RegistriesPanel), "Save", "registry.save"),
        (typeof(Whiskers.Components.Shared.RegistriesPanel), "Delete", "registry.delete"),
    ];

    [Fact]
    public void Every_configuration_handler_runs_its_write_inside_the_gateway_as_its_own_capability()
    {
        var capabilities = Writes.Select(w => w.Capability).ToHashSet();
        foreach (var component in Handlers.Select(h => h.Component).Distinct())
        {
            var calls = MutationCallScanner.CallsIn(component).Where(c => capabilities.Contains(c.Capability)).ToList();

            // Exactly the listed handlers write configuration — a new one must be added here …
            Assert.Equal(
                Handlers.Where(h => h.Component == component).Select(h => $"{h.Handler} -> {h.Capability}").Order(),
                calls.Select(c => $"{c.Handler} -> {c.Capability}").Distinct().Order());
            // … and every such call sits in a runner lambda for that very capability, so the gateway authorizes,
            // decides and audits it before the store is touched.
            Assert.All(calls, c => Assert.Equal(c.Capability, c.GatedAs));
        }
    }

    // ---------------------------------------------------------------- authorization and audit order

    [Theory]
    [MemberData(nameof(Capabilities))]
    public async Task A_viewer_or_operator_is_refused_whatever_the_page_shows(string capability)
    {
        foreach (var role in new[] { AppRole.Viewer, AppRole.Operator })
        {
            var (runner, audit) = Runner(role);
            var ran = false;

            var refused = await Assert.ThrowsAsync<MutationRefusedException>(() =>
                runner.RunAsync(capability, new { section = "x" }, _ => { ran = true; return Task.CompletedTask; }));

            Assert.False(ran);
            Assert.Equal(MutationStatus.Denied, refused.Status);
            Assert.Contains("denied:Denied", audit.Events);
        }
    }

    [Fact]
    public async Task A_demoted_admin_is_refused_on_the_next_click()
    {
        var user = new MutableUser(AppRole.Admin);
        var (runner, _) = Runner(user);
        await runner.RunAsync("config.roles", new { section = "roles" }, _ => Task.CompletedTask);

        user.Role = AppRole.Operator;   // the page is still open, its buttons still rendered
        var ran = false;
        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.RunAsync("config.roles", new { section = "roles" }, _ => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);
    }

    [Theory]
    [MemberData(nameof(Capabilities))]
    public async Task An_admin_write_runs_after_its_audit_intent(string capability)
    {
        var (runner, audit) = Runner(AppRole.Admin);
        var intentFirst = false;

        await runner.RunAsync(capability, new { section = "x" }, _ =>
        {
            intentFirst = audit.Events.Contains($"begin:{capability}:");
            return Task.CompletedTask;
        });

        Assert.True(intentFirst);
        Assert.Contains("complete:Executed", audit.Events);
    }

    [Fact]
    public async Task No_audit_intent_no_write()
    {
        var (runner, audit) = Runner(AppRole.Admin);
        audit.FailNextBegin = true;
        var ran = false;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            runner.RunAsync("config.whitelist", new { section = "whitelist" }, _ => { ran = true; return Task.CompletedTask; }));

        Assert.False(ran);
    }

    // ---------------------------------------------------------------- kill switch

    [Theory]
    [MemberData(nameof(CapabilitiesTheKillSwitchStops))]
    public async Task The_kill_switch_refuses_every_configuration_write(string capability)
    {
        var (runner, _) = Runner(AppRole.Admin, KillSwitchOn());
        var ran = false;

        var refused = await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.RunAsync(capability, new { section = "x" }, _ => { ran = true; return Task.CompletedTask; }));

        Assert.False(ran);
        Assert.Contains("Read-only", refused.Message);
    }

    [Fact]
    public async Task Switching_the_kill_switch_off_is_the_one_write_it_lets_through_and_it_is_audited()
    {
        var (runner, audit) = Runner(AppRole.Admin, KillSwitchOn());

        await runner.RunAsync(GuardrailMutationPolicy.KillSwitchOff, new { section = "guardrails" }, _ => Task.CompletedTask);

        Assert.Contains($"begin:{GuardrailMutationPolicy.KillSwitchOff}:", audit.Events);
        Assert.Contains("complete:Executed", audit.Events);
    }

    [Fact]
    public async Task Only_an_admin_may_switch_the_kill_switch_off()
    {
        var (runner, _) = Runner(AppRole.Operator, KillSwitchOn());

        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            runner.RunAsync(GuardrailMutationPolicy.KillSwitchOff, new { section = "guardrails" }, _ => Task.CompletedTask));
    }

    [Theory]
    [InlineData(MutationSurface.Agent, MutationPrincipalKind.Agent)]
    [InlineData(MutationSurface.Mcp, MutationPrincipalKind.ApiKey)]
    [InlineData(MutationSurface.Automation, MutationPrincipalKind.Automation)]
    public void Nobody_but_a_person_in_the_ui_gets_past_the_kill_switch(MutationSurface surface, MutationPrincipalKind kind)
    {
        var store = new Store(KillSwitchOn());
        var policy = AgentToolTestHelpers.Policy(store);
        var capability = new CapabilityCatalog().Find(GuardrailMutationPolicy.KillSwitchOff)!;
        var principal = new MutationPrincipal(kind, "p", "p", McpPermissionLevels.Admin,
            OnBehalfOf: kind == MutationPrincipalKind.Agent
                ? new MutationPrincipal(MutationPrincipalKind.Human, "user:a@x", "a@x", McpPermissionLevels.Admin)
                : null);

        var decision = policy.Evaluate(new MutationRequest(capability.Id, principal, surface,
            new MutationTarget("config", "guardrails"), new Dictionary<string, JsonElement>()), capability);

        Assert.Equal(MutationVerdict.Deny, decision.Verdict);
    }

    // ---------------------------------------------------------------- fail-closed repair

    [Fact]
    public async Task A_fail_closed_store_refuses_every_configuration_write_its_own_repair_included()
    {
        // User decision (R1-11): an unreadable guardrails.json is repaired on the host, not through the UI.
        var broken = new Store(GuardrailPolicy.FailClosed()) { Error = "guardrails.json is invalid" };
        var (admin, _) = Runner(AppRole.Admin, store: broken);

        foreach (var capability in Writes.Select(w => w.Capability).Distinct())
        {
            var ran = false;
            await Assert.ThrowsAsync<MutationRefusedException>(() =>
                admin.RunAsync(capability, new { section = "guardrails" }, _ => { ran = true; return Task.CompletedTask; }));
            Assert.False(ran);
        }
    }

    // ---------------------------------------------------------------- the stores

    [Fact]
    public async Task Disabling_the_kill_switch_changes_nothing_but_the_kill_switch()
    {
        var store = new GuardrailStore(filePath: Path.Combine(_dir, "guardrails.json"));
        await store.InitializeAsync();
        var config = store.Config;
        var active = config.Presets.First(p => p.Name == config.ActivePreset);
        active.Policy.ReadOnlyMode = true;
        active.Policy.MaxActionsPerSession = 7;
        await store.SaveConfigAsync(config, Admin);

        await Assert.ThrowsAnyAsync<Exception>(() => store.DisableKillSwitchAsync(Operator));
        Assert.True(store.Current.ReadOnlyMode);

        await store.DisableKillSwitchAsync(Admin);

        Assert.False(store.Current.ReadOnlyMode);
        Assert.Equal(7, store.Current.MaxActionsPerSession);
        var reloaded = new GuardrailStore(filePath: Path.Combine(_dir, "guardrails.json"));
        await reloaded.InitializeAsync();
        Assert.False(reloaded.Current.ReadOnlyMode);
    }

    [Fact]
    public async Task A_kill_switch_that_cannot_be_written_stays_on()
    {
        var path = Path.Combine(_dir, "guardrails.json");
        var store = new GuardrailStore(filePath: path);
        await store.InitializeAsync();
        var config = store.Config;
        config.Presets.First(p => p.Name == config.ActivePreset).Policy.ReadOnlyMode = true;
        await store.SaveConfigAsync(config, Admin);

        Directory.CreateDirectory(path + ".tmp");   // the next write fails
        await Assert.ThrowsAnyAsync<Exception>(() => store.DisableKillSwitchAsync(Admin));

        Assert.True(store.Current.ReadOnlyMode);
    }

    [Fact]
    public async Task A_failed_permission_write_leaves_the_live_permissions_unchanged()
    {
        var paths = new DataPathOptions(_dir);
        var svc = new McpPermissionService(NullLogger<McpPermissionService>.Instance, paths);
        await svc.InitializeAsync();
        var key = await svc.AddApiKeyAsync("ci", McpPermissionLevels.Read);
        var edit = svc.GetPermissionData();
        edit.ApiKeys.Single(k => k.Id == key.Id).Enabled = false;

        Directory.CreateDirectory(paths.McpPermissionsJson + ".tmp");
        await Assert.ThrowsAnyAsync<Exception>(() => svc.SavePermissionDataAsync(edit));

        Assert.True(svc.GetPermissionData().ApiKeys.Single(k => k.Id == key.Id).Enabled);
    }

    [Fact]
    public async Task Saved_permissions_do_not_alias_the_callers_edit_buffer()
    {
        var svc = new McpPermissionService(NullLogger<McpPermissionService>.Instance, new DataPathOptions(_dir));
        await svc.InitializeAsync();
        var key = await svc.AddApiKeyAsync("ci", McpPermissionLevels.Read);
        var edit = svc.GetPermissionData();
        await svc.SavePermissionDataAsync(edit);

        // The page keeps editing its buffer after the save; nothing reaches the live permissions until it saves again.
        edit.ApiKeys.Single(k => k.Id == key.Id).Enabled = false;
        edit.ApiKeys.Single(k => k.Id == key.Id).PermissionLevel = McpPermissionLevels.Admin;

        var live = svc.GetPermissionData().ApiKeys.Single(k => k.Id == key.Id);
        Assert.True(live.Enabled);
        Assert.Equal(McpPermissionLevels.Read, live.PermissionLevel);
    }

    [Fact]
    public async Task A_failed_vault_write_leaves_the_live_vault_unchanged()
    {
        var path = Path.Combine(_dir, "vault.json");
        var vault = new VaultService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY"] = "test-passphrase" }).Build(),
            NullLogger<VaultService>.Instance, path);
        await vault.InitializeAsync();
        await vault.SetSecretAsync("kept", "one");

        Directory.CreateDirectory(path + ".tmp");
        await Assert.ThrowsAnyAsync<Exception>(() => vault.SetSecretAsync("kept", "two"));
        await Assert.ThrowsAnyAsync<Exception>(() => vault.SetSecretAsync("new", "three"));
        await Assert.ThrowsAnyAsync<Exception>(() => vault.DeleteSecretAsync("kept"));

        Assert.Equal("one", vault.GetSecret("kept"));
        Assert.Null(vault.GetSecret("new"));
        Assert.Equal(["kept"], vault.ListSecrets().Select(e => e.Key));
    }

    [Fact]
    public async Task A_failed_registry_save_leaves_the_registry_and_its_credential_as_they_were()
    {
        var (vault, registries, path) = await RegistriesAsync();
        var saved = await registries.SaveRegistryAsync(new RegistryConfig { Name = "ghcr", Host = "ghcr.io", Username = "me" }, "old");

        Directory.CreateDirectory(path + ".tmp");
        var edit = new RegistryConfig { Id = saved.Id, Name = "renamed", Host = "other.io", Username = "you" };
        await Assert.ThrowsAnyAsync<Exception>(() => registries.SaveRegistryAsync(edit, "new"));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            registries.SaveRegistryAsync(new RegistryConfig { Name = "new", Host = "new.io" }, "secret"));

        var live = Assert.Single(await registries.GetRegistriesAsync());
        Assert.Equal(("ghcr", "ghcr.io", "me"), (live.Name, live.Host, live.Username));
        Assert.Equal(("me", "old", "ghcr.io"), registries.GetCredentialForImage("ghcr.io/x/y:1"));
        // The new credential went to a key no registry names, so it never took effect; at most an orphan is left.
        Assert.Equal("old", vault.GetSecret(Assert.Single(await registries.GetRegistriesAsync()).CredentialRef!));
    }

    [Fact]
    public async Task A_new_credential_takes_effect_only_with_the_entry_that_names_it_and_the_old_key_goes()
    {
        var (vault, registries, _) = await RegistriesAsync();
        var saved = await registries.SaveRegistryAsync(new RegistryConfig { Name = "ghcr", Host = "ghcr.io", Username = "me" }, "one");
        var firstRef = Assert.Single(await registries.GetRegistriesAsync()).CredentialRef;

        await registries.SaveRegistryAsync(new RegistryConfig { Id = saved.Id, Name = "ghcr", Host = "ghcr.io", Username = "me" }, "two");

        var secondRef = Assert.Single(await registries.GetRegistriesAsync()).CredentialRef;
        Assert.NotEqual(firstRef, secondRef);
        Assert.Equal(("me", "two", "ghcr.io"), registries.GetCredentialForImage("ghcr.io/x/y:1"));
        Assert.Equal([secondRef], vault.ListSecrets().Select(e => e.Key));
    }

    [Fact]
    public async Task Saving_without_a_credential_keeps_the_stored_one()
    {
        var (_, registries, _) = await RegistriesAsync();
        var saved = await registries.SaveRegistryAsync(new RegistryConfig { Name = "ghcr", Host = "ghcr.io", Username = "me" }, "pw");

        // The page's form does not carry the reference; the stored entry decides.
        await registries.SaveRegistryAsync(new RegistryConfig { Id = saved.Id, Name = "renamed", Host = "ghcr.io", Username = "me" }, null);

        Assert.Equal(("me", "pw", "ghcr.io"), registries.GetCredentialForImage("ghcr.io/x/y:1"));
    }

    [Fact]
    public async Task A_registry_from_before_versioned_keys_still_resolves_and_is_cleaned_up()
    {
        var (vault, registries, path) = await RegistriesAsync();
        await vault.SetSecretAsync("registry-cred:legacy1", "pw");
        await File.WriteAllTextAsync(path,
            """{"Registries":[{"Id":"legacy1","Name":"old","Host":"ghcr.io","Username":"me","HasCredential":true}]}""");
        var reloaded = new RegistryConfigService(vault, path);

        Assert.Equal(("me", "pw", "ghcr.io"), reloaded.GetCredentialForImage("ghcr.io/x/y:1"));
        await reloaded.SaveRegistryAsync(new RegistryConfig { Id = "legacy1", Name = "old", Host = "ghcr.io", Username = "me" }, "new");

        Assert.Equal(("me", "new", "ghcr.io"), reloaded.GetCredentialForImage("ghcr.io/x/y:1"));
        Assert.DoesNotContain(vault.ListSecrets(), e => e.Key == "registry-cred:legacy1");
    }

    [Fact]
    public async Task A_delete_never_removes_the_credential_of_a_later_save_of_the_same_registry()
    {
        var (_, registries, _) = await RegistriesAsync();
        var saved = await registries.SaveRegistryAsync(new RegistryConfig { Name = "ghcr", Host = "ghcr.io", Username = "me" }, "one");

        // Another dialog deletes while a third saves the same registry again.
        await Task.WhenAll(
            registries.DeleteRegistryAsync(saved.Id),
            registries.SaveRegistryAsync(new RegistryConfig { Id = saved.Id, Name = "ghcr", Host = "ghcr.io", Username = "me" }, "two"));

        if ((await registries.GetRegistriesAsync()).Count == 1)
            Assert.Equal(("me", "two", "ghcr.io"), registries.GetCredentialForImage("ghcr.io/x/y:1"));
        else
            Assert.Null(registries.GetCredentialForImage("ghcr.io/x/y:1"));
    }

    [Fact]
    public async Task A_pull_during_a_credential_rotation_gets_the_new_credential_not_none()
    {
        var vault = new MemoryVault();
        var path = Path.Combine(_dir, "registries.json");
        var registries = new RegistryConfigService(vault, path);
        var saved = await registries.SaveRegistryAsync(new RegistryConfig { Name = "ghcr", Host = "ghcr.io", Username = "me" }, "one");
        var oldRef = Assert.Single(await registries.GetRegistriesAsync()).CredentialRef!;

        // The pull captured the old entry; the rotation publishes and removes the old key before the pull reads it.
        vault.BeforeRead = key =>
        {
            if (key != oldRef) return;
            vault.BeforeRead = null;
            registries.SaveRegistryAsync(new RegistryConfig { Id = saved.Id, Name = "ghcr", Host = "ghcr.io", Username = "me" }, "two")
                .GetAwaiter().GetResult();
        };

        Assert.Equal(("me", "two", "ghcr.io"), registries.GetCredentialForImage("ghcr.io/x/y:1"));
    }

    [Fact]
    public async Task A_server_that_cannot_be_removed_keeps_its_kubeconfig()
    {
        var vault = new MemoryVault();
        var path = Path.Combine(_dir, "servers.json");
        var (servers, server) = await KubernetesServerAsync(path, vault);

        Directory.CreateDirectory(path + ".tmp");   // writing servers.json fails
        await Assert.ThrowsAnyAsync<Exception>(() => ServerRemoval.RemoveAsync(servers, vault, server));

        Assert.Contains(servers.GetServers(), s => s.Id == server.Id);
        Assert.Equal("kubeconfig-yaml", vault.GetSecret(KubernetesClientCache.KubeconfigVaultKey(server.Id)));
    }

    [Fact]
    public async Task A_removed_server_takes_its_kubeconfig_along_and_a_failed_cleanup_does_not_undo_the_removal()
    {
        var vault = new MemoryVault();
        var (servers, server) = await KubernetesServerAsync(Path.Combine(_dir, "servers.json"), vault);
        Assert.True(await ServerRemoval.RemoveAsync(servers, vault, server));
        Assert.Null(vault.GetSecret(KubernetesClientCache.KubeconfigVaultKey(server.Id)));

        var (servers2, server2) = await KubernetesServerAsync(Path.Combine(_dir, "servers2.json"), vault);
        vault.FailDeletes = true;
        Assert.False(await ServerRemoval.RemoveAsync(servers2, vault, server2));
        Assert.DoesNotContain(servers2.GetServers(), s => s.Id == server2.Id);
    }

    [Fact]
    public async Task A_kubeconfig_staged_for_a_failed_server_save_never_takes_effect()
    {
        var vault = new MemoryVault();
        var path = Path.Combine(_dir, "servers.json");
        var (servers, server) = await KubernetesServerAsync(path, vault);
        Assert.Equal("kubeconfig-yaml", vault.GetSecret(KubernetesClientCache.KubeconfigKeyOf(servers.GetServer(server.Id)!)));

        // What the Servers page does on save: the new kubeconfig under a new key, then the record naming it.
        var staged = $"{KubernetesClientCache.KubeconfigVaultKey(server.Id)}:{Guid.NewGuid():N}";
        await vault.SetSecretAsync(staged, "new-yaml");
        var edit = servers.GetServer(server.Id)!.Clone();
        edit.KubeconfigRef = staged;
        Directory.CreateDirectory(path + ".tmp");
        await Assert.ThrowsAnyAsync<Exception>(() => servers.UpdateServerAsync(edit));

        // The live record still names the old key, so every client built now reads the old kubeconfig.
        Assert.Equal("kubeconfig-yaml", vault.GetSecret(KubernetesClientCache.KubeconfigKeyOf(servers.GetServer(server.Id)!)));
    }

    [Fact]
    public void A_server_without_a_kubeconfig_reference_reads_the_key_from_before()
    {
        var server = new Models.ServerConfig { Id = "abc", ConnectionType = ConnectionType.Kubernetes };
        Assert.Equal("kubeconfig:abc", KubernetesClientCache.KubeconfigKeyOf(server));
        server.KubeconfigRef = "kubeconfig:abc:1";
        Assert.Equal("kubeconfig:abc:1", KubernetesClientCache.KubeconfigKeyOf(server));
    }

    [Fact]
    public void Removing_a_server_is_one_decision_whose_execution_owns_the_kubeconfig_cleanup()
    {
        Assert.True(typeof(ServerRemoval).IsDefined(typeof(MutationHandlerAttribute), false));
        Assert.DoesNotContain(MutationCallScanner.CallsIn(typeof(Whiskers.Components.Pages.Servers)),
            c => c.Handler == "DeleteServer" && c.Capability.StartsWith("vault.", StringComparison.Ordinal));
    }

    private static async Task<(ServerConfigService Servers, Models.ServerConfig Server)> KubernetesServerAsync(
        string path, MemoryVault vault)
    {
        var servers = new ServerConfigService(Microsoft.Extensions.Options.Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, storePath: path);
        await servers.InitializeAsync();
        var server = new Models.ServerConfig { Name = "k8s", ConnectionType = ConnectionType.Kubernetes };
        await servers.AddServerAsync(server);
        await vault.SetSecretAsync(KubernetesClientCache.KubeconfigVaultKey(server.Id), "kubeconfig-yaml");
        return (servers, server);
    }

    /// <summary>An in-memory vault that can fail its deletes and run code before a read.</summary>
    private sealed class MemoryVault : IVaultService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _secrets = new();
        public Action<string>? BeforeRead { get; set; }
        public bool FailDeletes { get; set; }
        public bool IsEnabled => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<VaultEntry> ListSecrets() => _secrets.Keys.Select(k => new VaultEntry { Key = k }).ToList();
        public Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null,
            int? rotateAfterDays = null) { _secrets[key] = plainValue; return Task.CompletedTask; }
        public string? GetSecret(string key)
        {
            BeforeRead?.Invoke(key);
            return _secrets.TryGetValue(key, out var v) ? v : null;
        }
        public Task DeleteSecretAsync(string key)
        {
            if (FailDeletes) throw new IOException("vault.json not writable");
            _secrets.TryRemove(key, out _);
            return Task.CompletedTask;
        }
        public List<VaultEntry> GetExpiringSecrets() => [];
    }

    [Fact]
    public async Task A_failed_settings_save_of_several_sections_changes_none_of_them()
    {
        var path = Path.Combine(_dir, "app-settings.json");
        var store = new AppSettingsStore(path);
        await store.SaveSectionsAsync(new Dictionary<string, object> { ["A"] = new { V = 1 }, ["B"] = new { V = 1 } });
        var before = await File.ReadAllTextAsync(path);

        Directory.CreateDirectory(path + ".tmp");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.SaveSectionsAsync(new Dictionary<string, object> { ["A"] = new { V = 2 }, ["B"] = new { V = 2 } }));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_failed_registry_delete_keeps_the_registry_and_its_credential()
    {
        var (_, registries, path) = await RegistriesAsync();
        var saved = await registries.SaveRegistryAsync(new RegistryConfig { Name = "ghcr", Host = "ghcr.io", Username = "me" }, "pw");

        Directory.CreateDirectory(path + ".tmp");
        await Assert.ThrowsAnyAsync<Exception>(() => registries.DeleteRegistryAsync(saved.Id));

        Assert.Single(await registries.GetRegistriesAsync());
        Assert.Equal(("me", "pw", "ghcr.io"), registries.GetCredentialForImage("ghcr.io/x/y:1"));
    }

    [Fact]
    public async Task The_stored_registry_does_not_alias_the_pages_form()
    {
        var (_, registries, _) = await RegistriesAsync();
        var form = new RegistryConfig { Name = "ghcr", Host = "ghcr.io" };
        await registries.SaveRegistryAsync(form, null);

        form.Host = "evil.io";   // the dialog is still open

        Assert.Equal("ghcr.io", Assert.Single(await registries.GetRegistriesAsync()).Host);
    }

    private async Task<(VaultService Vault, RegistryConfigService Registries, string Path)> RegistriesAsync()
    {
        var vault = new VaultService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY"] = "test-passphrase" }).Build(),
            NullLogger<VaultService>.Instance, Path.Combine(_dir, "vault.json"));
        await vault.InitializeAsync();
        var path = Path.Combine(_dir, "registries.json");
        return (vault, new RegistryConfigService(vault, path), path);
    }

    // ---------------------------------------------------------------- helpers

    private static readonly AgentPrincipal Admin =
        new(AgentPrincipalKind.WebUser, "admin@x", McpPermissionLevels.Admin, null, UserEmail: "admin@x");

    private static readonly AgentPrincipal Operator =
        new(AgentPrincipalKind.WebUser, "op@x", McpPermissionLevels.Write, null, UserEmail: "op@x");

    private static GuardrailPolicy KillSwitchOn()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;
        return policy;
    }

    private static (UiMutationRunner Runner, AgentToolTestHelpers.NullMutationAudit Audit) Runner(
        AppRole role, GuardrailPolicy? policy = null, Store? store = null) =>
        Runner(new MutableUser(role), policy, store);

    private static (UiMutationRunner Runner, AgentToolTestHelpers.NullMutationAudit Audit) Runner(
        ICurrentUserService user, GuardrailPolicy? policy = null, Store? store = null)
    {
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        store ??= new Store(policy ?? GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), store));
        return (new UiMutationRunner(gateway, new CapabilityCatalog(), user), audit);
    }

    private sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("ops@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }

    /// <summary>A guardrail store with a fixed policy that can also report a load error (fail-closed).</summary>
    private sealed class Store(GuardrailPolicy policy) : IGuardrailStore
    {
        public string? Error { get; init; }
        public GuardrailPolicy Current => policy;
        public GuardrailConfig Config { get; } = new();
        public string? LoadError => Error;
        public Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy p, AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }
}
