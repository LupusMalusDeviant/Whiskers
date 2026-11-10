using System.Reflection;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0012 (D10): no execution acts on an id of its own or looks its target up again.
/// <list type="number">
/// <item>In every lambda handed to a <see cref="MutationEntryPointAttribute"/> runner — and every helper of its owner it
/// hands the object on to — what reaches a <see cref="MutationTargetAttribute"/> parameter or a <c>serverId</c> parameter
/// of a <see cref="MutatingOperationAttribute"/> method comes from the lambda's <see cref="ResolvedTarget"/>.</item>
/// <item>No call to an <see cref="InventoryLookupAttribute"/> method inside such a lambda, except the pinned ones.</item>
/// <item>Every <see cref="MutatingOperationAttribute"/> method of an <see cref="TargetResolutionKind.Object"/> capability
/// has exactly one <see cref="MutationTargetAttribute"/> parameter, or is in the pinned list with a reason.</item>
/// <item>Negative fixtures compiled into this assembly are found; a <see cref="ResolvedTarget"/> cannot be built
/// outside the resolver.</item>
/// </list>
/// Since contract 0012 step 7 no lambda is untyped (the old <c>Func&lt;CancellationToken, …&gt;</c> overloads are gone)
/// and the pending lists are empty and deleted.
/// </summary>
public sealed class ResolvedTargetFlowTests
{
    private static readonly Assembly App = typeof(Program).Assembly;

    /// <summary>D10 item 2: inventory reads that stay inside an execution by design, with their reason.</summary>
    private static readonly IReadOnlySet<string> LookupExceptions = new HashSet<string>(StringComparer.Ordinal)
    {
        // An entry names "<site key>: <Type>.<Method>" and why. (The compose re-check runs through
        // ComposePlan.EnsureDecided and verifiers read back outside the lambda: neither needs one.)
        // Not the target: after the server record is saved, whether an old cloud key / kubeconfig is still named by
        // any server decides only if the vault entry may be tidied up (R1-29).
        "Whiskers.Components.Pages.Servers.PersistServerAsync -> server.save: IServerConfigService.GetServers",
    };

    private static IReadOnlyList<ResolvedTargetFlowScanner.Site> AppSites() =>
        ResolvedTargetFlowScanner.Sites(ResolvedTargetFlowScanner.SafeTypes(App));

    [Fact]
    public void Every_typed_lambda_acts_only_on_the_object_the_gateway_resolved()
    {
        var findings = AppSites().Where(s => s.Typed)
            .SelectMany(s => ResolvedTargetFlowScanner.Findings(s, LookupExceptions))
            .Distinct().Order(StringComparer.Ordinal).ToList();

        Assert.True(findings.Count == 0, "Acts on something other than the resolved object:\n" + string.Join("\n", findings));
    }

    [Fact]
    public void Every_lambda_receives_the_resolved_object()
    {
        // Step 7 deleted the old Func<CancellationToken, …> overloads: no site can hand over an untyped lambda.
        var untyped = AppSites().Where(s => !s.Typed).Select(s => s.Key).Order(StringComparer.Ordinal).ToList();

        Assert.True(untyped.Count == 0, "Lambdas that do not receive the resolved object:\n" + string.Join("\n", untyped));
    }

    // D4 b / D10 item 3: object operations without a [MutationTarget] parameter, each with its reason.
    private const string WholeSection =
        "Saves a whole settings object; the decided target is its config section, which the call does not name.";
    private const string StoreKey =
        "Names the store's section key (the file's key), not the closed-list section the decision resolved; " +
        "ConfigSectionsTests pins the sections the pages pass.";
    private const string SectionEntry =
        "Names an entry inside the decided config section (an e-mail, a tool, a container's preferences), not the section.";
    private const string OwnAccount = "Acts on the signed-in account itself; the call names no account.";
    private const string ProviderTransport =
        "Provider transport below ICloudControlService, which marks the decided cloud server; it takes the provider's " +
        "server record or numeric id.";
    private const string HetznerId =
        "Hetzner-only operation on the provider's numeric server or image id; the decided target is the Whiskers server.";
    private const string Entity =
        "Takes the stored entity (or the open request), not an id: the delegate passes the object read for the decision.";
    private const string RollbackRecord = "Takes the numeric rollback record; the decided target is the container.";
    private const string DerivedSecret =
        "Composed by server, registry and Git-app removal for a key derived from the decided record, not a decided name.";

    private static readonly IReadOnlyDictionary<string, string> WithoutTargetParameter = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Whiskers.Services.Agent.IAgentSettingsStore.SaveAsync (config.agent)"] = WholeSection,
        ["Whiskers.Services.Auth.ILocalAccountService.ChangeOwnPasswordAsync (account.password_change)"] = OwnAccount,
        ["Whiskers.Services.Auth.IRoleService.RemoveRoleAsync (config.roles)"] = SectionEntry,
        ["Whiskers.Services.Auth.IRoleService.SaveRoleDataAsync (config.roles)"] = WholeSection,
        ["Whiskers.Services.Auth.IRoleService.SetRoleAsync (config.roles)"] = SectionEntry,
        ["Whiskers.Services.Auth.IWhitelistService.SaveWhitelistAsync (config.whitelist)"] = WholeSection,
        ["Whiskers.Services.AutoUpdate.IAutoUpdateService.RollbackAsync (container.rollback)"] = RollbackRecord,
        ["Whiskers.Services.AutoUpdate.IAutoUpdateService.SetPolicyAsync (autoupdate.policy_set)"] = Entity,
        ["Whiskers.Services.Cloud.Providers.ICloudProvider.CreateSnapshotAsync (cloud.snapshot_create)"] = ProviderTransport,
        ["Whiskers.Services.Cloud.Providers.ICloudProvider.HardResetAsync (cloud.hard_reset)"] = ProviderTransport,
        ["Whiskers.Services.Cloud.Providers.ICloudProvider.PowerOnAsync (cloud.power_on)"] = ProviderTransport,
        ["Whiskers.Services.Cloud.Providers.ICloudProvider.RebootAsync (cloud.reboot)"] = ProviderTransport,
        ["Whiskers.Services.Cloud.Providers.ICloudProvider.ShutdownAsync (cloud.shutdown)"] = ProviderTransport,
        ["Whiskers.Services.Cloud.Providers.IHetznerExtensions.ChangeServerTypeAsync (hetzner.server_type_change)"] = HetznerId,
        ["Whiskers.Services.Cloud.Providers.IHetznerExtensions.DeleteImageAsync (hetzner.snapshot_delete)"] = HetznerId,
        ["Whiskers.Services.Cloud.Providers.IHetznerExtensions.DisableBackupsAsync (hetzner.backups_disable)"] = HetznerId,
        ["Whiskers.Services.Cloud.Providers.IHetznerExtensions.DisableRescueAsync (hetzner.rescue_disable)"] = HetznerId,
        ["Whiskers.Services.Cloud.Providers.IHetznerExtensions.EnableBackupsAsync (hetzner.backups_enable)"] = HetznerId,
        ["Whiskers.Services.Cloud.Providers.IHetznerExtensions.EnableRescueAsync (hetzner.rescue_enable)"] = HetznerId,
        ["Whiskers.Services.GitDeploy.IGitDeployService.DeployAsync (gitdeploy.deploy)"] = Entity,
        ["Whiskers.Services.Guardrails.IGuardrailStore.SaveAsync (guardrails.save)"] = WholeSection,
        ["Whiskers.Services.Guardrails.IGuardrailStore.SaveConfigAsync (guardrails.save)"] = WholeSection,
        ["Whiskers.Services.Hetzner.IHetznerService.ChangeServerTypeAsync (hetzner.server_type_change)"] = HetznerId,
        ["Whiskers.Services.Hetzner.IHetznerService.CreateSnapshotAsync (cloud.snapshot_create)"] = ProviderTransport,
        ["Whiskers.Services.Hetzner.IHetznerService.DeleteImageAsync (hetzner.snapshot_delete)"] = HetznerId,
        ["Whiskers.Services.Hetzner.IHetznerService.DisableBackupsAsync (hetzner.backups_disable)"] = HetznerId,
        ["Whiskers.Services.Hetzner.IHetznerService.DisableRescueAsync (hetzner.rescue_disable)"] = HetznerId,
        ["Whiskers.Services.Hetzner.IHetznerService.EnableBackupsAsync (hetzner.backups_enable)"] = HetznerId,
        ["Whiskers.Services.Hetzner.IHetznerService.EnableRescueAsync (hetzner.rescue_enable)"] = HetznerId,
        ["Whiskers.Services.Hetzner.IHetznerService.PowerOnAsync (cloud.power_on)"] = ProviderTransport,
        ["Whiskers.Services.Hetzner.IHetznerService.RebootAsync (cloud.reboot)"] = ProviderTransport,
        ["Whiskers.Services.Hetzner.IHetznerService.ResetAsync (cloud.hard_reset)"] = ProviderTransport,
        ["Whiskers.Services.Hetzner.IHetznerService.ShutdownAsync (cloud.shutdown)"] = ProviderTransport,
        ["Whiskers.Services.Hostinger.IHostingerService.CreateSnapshotAsync (cloud.snapshot_create)"] = ProviderTransport,
        ["Whiskers.Services.Hostinger.IHostingerService.RestartAsync (cloud.reboot)"] = ProviderTransport,
        ["Whiskers.Services.Hostinger.IHostingerService.StartAsync (cloud.power_on)"] = ProviderTransport,
        ["Whiskers.Services.Hostinger.IHostingerService.StopAsync (cloud.shutdown)"] = ProviderTransport,
        ["Whiskers.Services.Mcp.IMcpPermissionService.SavePermissionDataAsync (config.mcp_permissions)"] = WholeSection,
        ["Whiskers.Services.Mcp.IMcpPermissionService.SetToolEnabledAsync (config.mcp_permissions)"] = SectionEntry,
        ["Whiskers.Services.Metrics.DiskHealth.IDiskHealthSettingsService.SaveAsync (config.save)"] = WholeSection,
        ["Whiskers.Services.Metrics.IMetricRetentionService.SaveAsync (config.save)"] = WholeSection,
        ["Whiskers.Services.Notifications.IContainerNotificationPrefsService.SavePrefsAsync (config.save)"] = SectionEntry,
        ["Whiskers.Services.Notifications.IOutboundExceptionService.SaveAsync (config.save)"] = WholeSection,
        ["Whiskers.Services.Notifications.IQuietHoursService.SaveAsync (config.save)"] = WholeSection,
        ["Whiskers.Services.Persistence.IAppSettingsStore.SaveSectionAsync (config.save)"] = StoreKey,
        ["Whiskers.Services.Persistence.IAppSettingsStore.SaveSectionsAsync (config.save)"] = WholeSection,
        ["Whiskers.Services.Persistence.IAppSettingsStore.UpdateSectionAsync (config.save)"] = StoreKey,
        ["Whiskers.Services.Persistence.IAppSettingsStore.UpdateSectionsAsync (config.save)"] = WholeSection,
        ["Whiskers.Services.Scheduler.ISchedulerService.UpdateTaskAsync (scheduler.update)"] = Entity,
        ["Whiskers.Services.Terminal.ITerminalSessionManager.OpenContainerShellAsync (terminal.container_open)"] = Entity,
        ["Whiskers.Services.Vault.IVaultService.DeleteSecretAsync (vault.secret_delete)"] = DerivedSecret,
    };

    [Fact]
    public void Every_object_operation_marks_its_target_or_has_a_reason()
    {
        var catalog = new CapabilityCatalog();
        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var iface in ResolvedTargetFlowScanner.SafeTypes(App).Where(t => t.IsInterface))
            foreach (var (method, capabilityId) in MutatingMethods.Of(iface))
            {
                if (catalog.Find(capabilityId) is not { Resolution: TargetResolutionKind.Object }) continue;
                var marked = method.GetParameters().Count(p => p.IsDefined(typeof(MutationTargetAttribute), false));
                if (marked != 1) actual.Add($"{iface.FullName}.{method.Name} ({capabilityId})");
            }
        var pinned = WithoutTargetParameter.Keys.ToHashSet(StringComparer.Ordinal);

        var added = actual.Except(pinned).ToList();
        var gone = pinned.Except(actual).Order(StringComparer.Ordinal).ToList();
        Assert.True(added.Count == 0, "Object operations without exactly one [MutationTarget]:\n" + string.Join("\n", added));
        Assert.True(gone.Count == 0,
            "Marked or removed — delete these entries from WithoutTargetParameter:\n" + string.Join("\n", gone));
        Assert.All(WithoutTargetParameter.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    // ---------------------------------------------------------------- the scanner finds what it must (D10 item 4)

    private static IReadOnlyList<string> FixtureFindings(string handler) =>
        ResolvedTargetFlowScanner.Sites([typeof(ResolvedTargetFlowFixtures)])
            .Where(s => s.Handler == handler)
            .SelectMany(s => s.Typed ? ResolvedTargetFlowScanner.Findings(s, new HashSet<string>()) : [$"{s.Key}: untyped"])
            .ToList();

    [Fact]
    public void A_lambda_on_the_resolved_object_has_no_finding()
    {
        Assert.Empty(FixtureFindings(nameof(ResolvedTargetFlowFixtures.Clean)));
        Assert.Empty(FixtureFindings(nameof(ResolvedTargetFlowFixtures.CleanAsync)));
        Assert.Empty(FixtureFindings(nameof(ResolvedTargetFlowFixtures.CleanThroughHelper)));
    }

    [Fact]
    public void A_captured_page_id_is_found()
    {
        var finding = Assert.Single(FixtureFindings(nameof(ResolvedTargetFlowFixtures.CapturedId)));
        Assert.Contains("StartContainerAsync(containerId) gets a captured value", finding);
    }

    [Fact]
    public void A_captured_id_inside_an_async_lambda_is_found()
    {
        var finding = Assert.Single(FixtureFindings(nameof(ResolvedTargetFlowFixtures.CapturedIdAsync)));
        Assert.Contains("StopContainerAsync(containerId) gets a captured value", finding);
    }

    [Fact]
    public void A_literal_server_is_found()
    {
        var finding = Assert.Single(FixtureFindings(nameof(ResolvedTargetFlowFixtures.LiteralServer)));
        Assert.Contains("RestartContainerAsync(serverId) gets a literal value", finding);
    }

    [Fact]
    public void A_fallback_server_is_found()
    {
        var finding = Assert.Single(FixtureFindings(nameof(ResolvedTargetFlowFixtures.FallbackServer)));
        Assert.Contains("RestartContainerAsync(serverId) gets a literal value", finding);
    }

    [Fact]
    public void A_re_lookup_in_the_delegate_is_found()
    {
        var findings = FixtureFindings(nameof(ResolvedTargetFlowFixtures.ReLookup));
        Assert.Contains(findings, f => f.Contains("looks its target up again (IDockerService.ListContainersAsync)"));
        Assert.Contains(findings, f => f.Contains("StartContainerAsync(containerId) gets a other value"));
    }

    [Fact]
    public void A_helper_that_acts_on_its_own_id_is_found()
    {
        var finding = Assert.Single(FixtureFindings(nameof(ResolvedTargetFlowFixtures.HelperWithOwnId)));
        Assert.Contains("StartContainerAsync(containerId) gets a", finding);
    }

    [Fact]
    public void A_resolved_target_cannot_be_built_outside_the_resolver()
    {
        // Every constructor of every ResolvedTarget type needs the resolver's token …
        var types = ResolvedTargetFlowScanner.SafeTypes(App)
            .Where(t => typeof(ResolvedTarget).IsAssignableFrom(t)).ToList();
        Assert.Contains(typeof(ResolvedContainer), types);
        foreach (var type in types)
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var parameters = ctor.GetParameters();
                // The record's copy constructor (for `with`) takes an instance that already exists.
                if (parameters is [var only] && only.ParameterType == type) { Assert.True(ctor.IsFamily || ctor.IsPrivate); continue; }
                Assert.Equal(typeof(MutationTargetResolver.ResolverToken), parameters[0].ParameterType);
            }

        // … and nothing outside the resolver can obtain one: no accessible constructor, no accessible instance.
        var token = typeof(MutationTargetResolver.ResolverToken);
        Assert.All(token.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            c => Assert.True(c.IsPrivate));
        Assert.DoesNotContain(token.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            f => !f.IsPrivate);
        Assert.DoesNotContain(token.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            p => p.GetMethod is { IsPrivate: false });
        var issued = typeof(MutationTargetResolver).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m is FieldInfo f && f.FieldType == token || m is PropertyInfo p && p.PropertyType == token).ToList();
        Assert.NotEmpty(issued);
        Assert.All(issued, m => Assert.True(m is FieldInfo { IsPrivate: true } || m is PropertyInfo { GetMethod.IsPrivate: true }));
    }
}

/// <summary>Negative (and positive) cases for <see cref="ResolvedTargetFlowTests"/>; never run.</summary>
internal sealed class ResolvedTargetFlowFixtures(IUiMutationRunner runner, IDockerService docker)
{
    private readonly string _pageServer = "local";

    public Task Clean() =>
        runner.RunAsync("container.start", new { containerId = "web" },
            (ResolvedContainer c, CancellationToken _) => docker.StartContainerAsync(c.Id, c.ServerId));

    public Task CleanAsync() =>
        runner.RunAsync("container.start", new { containerId = "web" }, async (ResolvedContainer c, CancellationToken _) =>
        {
            await Task.Yield();
            var id = c.Id;
            await docker.StartContainerAsync(id, c.ServerId);
        });

    public Task CleanThroughHelper() =>
        runner.RunAsync("container.start", new { containerId = "web" },
            (ResolvedContainer c, CancellationToken _) => StartIt(c));

    private Task StartIt(ResolvedContainer c) => docker.StartContainerAsync(c.Id, c.ServerId);

    public Task CapturedId(string containerId) =>
        runner.RunAsync("container.start", new { containerId },
            (ResolvedContainer c, CancellationToken _) => docker.StartContainerAsync(containerId, c.ServerId));

    public Task CapturedIdAsync(string containerId) =>
        runner.RunAsync("container.stop", new { containerId }, async (ResolvedContainer c, CancellationToken _) =>
        {
            await Task.Yield();
            await docker.StopContainerAsync(containerId, c.ServerId);
        });

    public Task LiteralServer() =>
        runner.RunAsync("container.restart", new { containerId = "web" },
            (ResolvedContainer c, CancellationToken _) => docker.RestartContainerAsync(c.Id, "local"));

    public Task FallbackServer() =>
        runner.RunAsync("container.restart", new { containerId = "web" },
            (ResolvedContainer c, CancellationToken _) => docker.RestartContainerAsync(c.Id, c.ServerId ?? "local"));

    public Task ReLookup() =>
        runner.RunAsync("container.start", new { containerId = "web" }, async (ResolvedContainer c, CancellationToken ct) =>
        {
            var again = (await docker.ListContainersAsync(true, c.ServerId, ct)).First(x => x.Name == "web");
            await docker.StartContainerAsync(again.Id, c.ServerId);
        });

    public Task HelperWithOwnId(string containerId) =>
        runner.RunAsync("container.start", new { containerId },
            (ResolvedContainer c, CancellationToken _) => StartOther(c, containerId));

    private Task StartOther(ResolvedContainer c, string id) => docker.StartContainerAsync(id, c.ServerId);

}
