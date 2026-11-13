using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Backup;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0012, step 1 (D1, D2, D5, D6, D12): every capability declares how its target resolves and which
/// <see cref="ResolvedTarget"/> its execution receives; the resolver has a branch for every kind — volumes, volume
/// backups, log-alert rules and Whiskers' own records included — and refuses an unknown or ambiguous object before
/// policy. Only the resolver can build a <see cref="ResolvedTarget"/>.
/// </summary>
public sealed partial class TargetResolutionTests
{
    // ---------------------------------------------------------------- the catalog (D2)

    [Fact]
    public void Every_capability_declares_its_resolution_and_a_target_type_that_fits_its_kind()
    {
        var problems = new List<string>();
        foreach (var capability in new CapabilityCatalog().All)
        {
            var type = capability.TargetType;
            if (!typeof(ResolvedTarget).IsAssignableFrom(type))
                problems.Add($"{capability.Id}: {type.Name} is not a ResolvedTarget");
            var upsert = CapabilityCatalog.Upserts.Contains(capability.Id);
            if (type == typeof(ResolvedTarget) != upsert)
                problems.Add($"{capability.Id}: only an upsert is typed as the base ResolvedTarget");
            if (type != typeof(ResolvedTarget) && !type.IsSealed)
                problems.Add($"{capability.Id}: {type.Name} is not one sealed kind");
            var expected = capability.Resolution switch
            {
                TargetResolutionKind.Creates => upsert ? typeof(ResolvedTarget) : typeof(ResolvedNew),
                TargetResolutionKind.Singleton => typeof(ResolvedRecord),
                TargetResolutionKind.Server => typeof(ResolvedHostObject),
                _ => KindType(capability),
            };
            if (type != expected) problems.Add($"{capability.Id} ({capability.TargetKind}, {capability.Resolution}): {type.Name}, expected {expected.Name}");
            // A singleton is Whiskers itself; a server-wide operation is a host operation.
            if (capability.Resolution == TargetResolutionKind.Singleton && capability.TargetKind is not ("whiskers" or "fleet" or "config"))
                problems.Add($"{capability.Id}: a singleton must be Whiskers-wide");
            if (capability.Resolution == TargetResolutionKind.Server && capability.TargetKind != "host")
                problems.Add($"{capability.Id}: only host operations act on their server as a whole");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static Type KindType(Capability capability) => capability.TargetKind switch
    {
        "container" => typeof(ResolvedContainer),
        "workload" => typeof(ResolvedWorkload),
        "network" => typeof(ResolvedNetwork),
        // D11: an nginx site, a systemd unit or a firewall rule on its server.
        "host" when CapabilityCatalog.HostObjects.Contains(capability.Id) => typeof(ResolvedHostObject),
        "server" when capability.Id.StartsWith("endpointcheck.", StringComparison.Ordinal) => typeof(ResolvedRecord),
        "server" => typeof(ResolvedServer),
        "cloud-server" => typeof(ResolvedCloudServer),
        "scheduled-task" => typeof(ResolvedScheduledTask),
        "dns-zone" => typeof(ResolvedDnsZone),
        "git-deploy-app" => typeof(ResolvedGitApp),
        "webhook" => typeof(ResolvedWebhook),
        "volume" when capability.Id == "volume.backup" => typeof(ResolvedVolume),
        "volume" => typeof(ResolvedVolumeBackup),
        "log-alert" => typeof(ResolvedLogAlertRule),
        "config" or "account" or "identity" or "whiskers" or "fleet" => typeof(ResolvedRecord),
        _ => typeof(void),   // a kind without a type: refused above
    };

    [Fact]
    public async Task Every_capability_has_a_resolver_branch()
    {
        // With no inventory at all, every branch refuses for its own reason (fail closed) — none for want of a branch.
        var resolver = new MutationTargetResolver(new TestServices());
        foreach (var capability in new CapabilityCatalog().All)
        {
            var request = new MutationRequest(capability.Id, Admin, MutationSurface.Ui,
                new MutationTarget(capability.TargetKind, "x"), new Dictionary<string, JsonElement>());
            var resolution = await resolver.ResolveAsync(request, capability, CancellationToken.None);
            Assert.DoesNotContain("which Whiskers cannot resolve", resolution.Error ?? "");
        }
        var unknownKind = new Capability("test.unknown", McpPermissionLevels.Write, "gizmo", CapabilityRisk.Reversible, null,
            TimeSpan.FromMinutes(1));
        var refused = await resolver.ResolveAsync(new MutationRequest(unknownKind.Id, Admin, MutationSurface.Ui,
            new MutationTarget("gizmo", "x"), new Dictionary<string, JsonElement>()), unknownKind, CancellationToken.None);
        Assert.Null(refused.Target);
    }

    [Fact]
    public async Task A_resolved_target_carries_the_object_of_its_capabilitys_type()
    {
        var resolution = await ResolveAsync("container.restart", new { containerId = "web", serverId = "local" });

        var container = Assert.IsType<ResolvedContainer>(resolution.Object);
        Assert.Equal("aaa111local", container.Id);
        Assert.Equal("local", container.ServerId);
        Assert.Same(resolution.Target, container.Record);
        Assert.False(string.IsNullOrEmpty(container.Fingerprint));
    }

    // ---------------------------------------------------------------- only the resolver builds one (D4 a)

    [Fact]
    public void Every_resolved_target_constructor_needs_the_resolvers_token()
    {
        var types = typeof(ResolvedTarget).Assembly.GetTypes()
            .Where(t => typeof(ResolvedTarget).IsAssignableFrom(t)).ToList();
        Assert.True(types.Count >= 16, string.Join(", ", types.Select(t => t.Name)));
        foreach (var type in types)
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var parameters = ctor.GetParameters();
                // The record's own copy constructor copies an object that already exists.
                if (parameters.Length == 1 && parameters[0].ParameterType == type) continue;
                Assert.True(parameters.Length > 0 && parameters[0].ParameterType == typeof(MutationTargetResolver.ResolverToken),
                    $"{type.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))}) does not take the resolver token");
            }
        // The token itself cannot be made: its one constructor is private.
        Assert.All(typeof(MutationTargetResolver.ResolverToken).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            c => Assert.True(c.IsPrivate));
        Assert.Empty(typeof(MutationTargetResolver.ResolverToken).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly));
        // Nothing outside the resolver can write a resolved object's properties.
        foreach (var type in types)
            foreach (var property in type.GetProperties())
                Assert.True(property.SetMethod is null, $"{type.Name}.{property.Name} has a setter");
    }

    // ---------------------------------------------------------------- volumes (D5)

    [Fact]
    public async Task A_volume_backup_without_a_server_is_decided_on_the_default_server()
    {
        var resolution = await ResolveAsync("volume.backup", new { volumeName = "pgdata", containerName = "db" });

        var volume = Assert.IsType<ResolvedVolume>(resolution.Object);
        Assert.Equal("edge01", volume.ServerId);
        Assert.Equal("pgdata", volume.Id);
        Assert.Equal("local", volume.Driver);
    }

    [Theory]
    [InlineData("pgdat")]      // a prefix is not a name
    [InlineData("PGDATA")]     // names are exact
    [InlineData("missing")]
    public async Task An_unknown_volume_is_refused_before_policy(string name)
    {
        var (outcome, evaluated) = await RefusedBeforePolicyAsync("volume.backup", new { volumeName = name, serverId = "edge01" });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetUnknown, outcome.ReasonCode);
        Assert.Equal(0, evaluated);
    }

    [Fact]
    public async Task A_volume_listing_that_fails_refuses()
    {
        _volumes.Fail = true;

        // The read throws; the gateway refuses before policy and keeps the detail in the log.
        var (outcome, evaluated) = await RefusedBeforePolicyAsync("volume.backup", new { volumeName = "pgdata" });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetNotResolved, outcome.ReasonCode);
        Assert.Equal(0, evaluated);
    }

    [Fact]
    public async Task A_backup_is_resolved_by_its_id_on_its_stored_server()
    {
        var resolution = await ResolveAsync("volume.backup_delete",
            new { volumeName = "something-else", backupId = "b-edge", serverId = "" });

        var backup = Assert.IsType<ResolvedVolumeBackup>(resolution.Object);
        Assert.Equal("b-edge", backup.Id);
        Assert.Equal("edge01", backup.ServerId);
        Assert.Equal("pgdata", backup.Name);
        Assert.Contains("pgdata_1.tar.gz", backup.Aliases);
    }

    [Fact]
    public async Task A_backup_of_a_removed_server_is_refused()
    {
        var (outcome, evaluated) = await RefusedBeforePolicyAsync("volume.restore", new { backupId = "b-gone" });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetUnknown, outcome.ReasonCode);
        Assert.Equal(0, evaluated);
    }

    [Fact]
    public async Task An_unknown_backup_is_refused_before_policy()
    {
        var (outcome, evaluated) = await RefusedBeforePolicyAsync("volume.backup_delete", new { backupId = "nope" });

        Assert.Equal(MutationReasonCodes.TargetUnknown, outcome.ReasonCode);
        Assert.Equal(0, evaluated);
    }

    [Fact]
    public async Task A_restore_into_an_existing_volume_touches_it_and_into_a_new_name_creates_it()
    {
        var into = Assert.IsType<ResolvedVolumeBackup>((await ResolveAsync("volume.restore", new { backupId = "b-edge" })).Object);
        Assert.True(into.RestoreIntoExists);
        Assert.Equal("pgdata", into.RestoreInto);
        Assert.Contains("pgdata", into.Related);

        var fresh = Assert.IsType<ResolvedVolumeBackup>(
            (await ResolveAsync("volume.restore", new { backupId = "b-edge", targetVolume = "pgdata-copy" })).Object);
        Assert.False(fresh.RestoreIntoExists);
        Assert.Equal("pgdata-copy", fresh.RestoreInto);
    }

    [Fact]
    public void The_volume_listing_is_read_from_dockers_json_lines()
    {
        var volumes = VolumeBackupService.ParseVolumeList(
            "{\"Driver\":\"local\",\"Labels\":\"com.docker.compose.project=app,x=y\",\"Name\":\"app_data\"}\n" +
            "{\"Driver\":\"nfs\",\"Labels\":\"\",\"Name\":\"shared\"}\n");

        Assert.Equal(["app_data", "shared"], volumes.Select(v => v.Name));
        Assert.Equal("nfs", volumes[1].Driver);
        Assert.Equal("app", volumes[0].Labels["com.docker.compose.project"]);
        Assert.ThrowsAny<Exception>(() => VolumeBackupService.ParseVolumeList("not json"));
    }

    // ---------------------------------------------------------------- log alerts (D6)

    [Fact]
    public async Task A_log_rule_is_resolved_by_its_id_and_is_whiskers_wide()
    {
        var resolution = await ResolveAsync("logalert.toggle", new { name = "renamed", ruleId = "r-1", enabled = false });

        var rule = Assert.IsType<ResolvedLogAlertRule>(resolution.Object);
        Assert.Equal("r-1", rule.Id);
        Assert.Equal("errors", rule.Name);
        Assert.Null(rule.ServerId);
    }

    [Fact]
    public async Task A_log_rule_named_twice_is_ambiguous_and_an_unknown_one_unknown()
    {
        var (ambiguous, evaluated) = await RefusedBeforePolicyAsync("logalert.delete", new { name = "dup" });
        Assert.Equal(MutationReasonCodes.TargetAmbiguous, ambiguous.ReasonCode);
        Assert.Equal(0, evaluated);

        var (unknown, _) = await RefusedBeforePolicyAsync("logalert.delete", new { name = "nothing" });
        Assert.Equal(MutationReasonCodes.TargetUnknown, unknown.ReasonCode);

        var single = Assert.IsType<ResolvedLogAlertRule>((await ResolveAsync("logalert.delete", new { name = "errors" })).Object);
        Assert.Equal("r-1", single.Id);
    }

    [Fact]
    public async Task A_new_log_rule_is_a_creation()
    {
        var resolution = await ResolveAsync("logalert.create", new { name = "new", containerName = "web" });

        Assert.IsType<ResolvedNew>(resolution.Object);
    }

    // ---------------------------------------------------------------- Whiskers' own records (D12)

    [Theory]
    [InlineData("config.save", "healthmonitor")]
    [InlineData("config.roles", "roles")]
    [InlineData("guardrails.save", "guardrails")]
    public async Task A_settings_section_of_the_closed_list_is_its_record(string capability, string section)
    {
        var record = Assert.IsType<ResolvedRecord>((await ResolveAsync(capability, new { section })).Object);

        Assert.Equal(section, record.Id);
        Assert.Equal("config-section", record.Store);
        Assert.Null(record.ServerId);
    }

    [Fact]
    public async Task An_unknown_settings_section_is_refused_before_policy()
    {
        var (outcome, evaluated) = await RefusedBeforePolicyAsync("config.save", new { section = "made-up" });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TargetUnknown, outcome.ReasonCode);
        Assert.Equal(0, evaluated);
    }

    [Fact]
    public async Task An_account_is_its_record_and_an_unknown_one_is_refused()
    {
        var record = Assert.IsType<ResolvedRecord>((await ResolveAsync("account.delete", new { account = "OPS@example.com" })).Object);
        Assert.Equal("ops@example.com", record.Id);

        var (outcome, evaluated) = await RefusedBeforePolicyAsync("account.password_reset", new { account = "ghost@example.com" });
        Assert.Equal(MutationReasonCodes.TargetUnknown, outcome.ReasonCode);
        Assert.Equal(0, evaluated);

        Assert.IsType<ResolvedNew>((await ResolveAsync("account.create", new { account = "new@example.com" })).Object);
    }

    [Fact]
    public async Task A_singleton_is_whiskers_itself()
    {
        var record = Assert.IsType<ResolvedRecord>((await ResolveAsync("notifications.clear", new { })).Object);
        Assert.Equal("whiskers", record.Store);
        Assert.Null(record.ServerId);

        Assert.Equal("fleet", Assert.IsType<ResolvedRecord>((await ResolveAsync("cve.scan", new { })).Object).Store);
    }

    [Fact]
    public async Task An_automation_feature_is_one_of_the_closed_list()
    {
        Assert.IsType<ResolvedRecord>((await ResolveAsync("automation.owner_set", new { feature = "autopatch", owner = "x" })).Object);

        var (outcome, _) = await RefusedBeforePolicyAsync("automation.owner_set", new { feature = "autoeverything" });
        Assert.Equal(MutationReasonCodes.TargetUnknown, outcome.ReasonCode);
    }

    [Fact]
    public void Every_literal_settings_section_a_page_names_is_on_the_closed_list()
    {
        var root = Path.Combine(McpToolCatalogSnapshotTests.RepositoryRoot(), "src", "Whiskers");
        var pattern = new Regex("RunAsync\\(\"(config|guardrails|mcp|vault|registry)\\.[a-z_]+\",\\s*new \\{ section = \"([a-z-]+)\"");
        var sections = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(f => pattern.Matches(File.ReadAllText(f)).Select(m => m.Groups[2].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.Contains(s, ConfigSections.All));
    }

    // ---------------------------------------------------------------- helpers

    private readonly Volumes _volumes = new();

    private MutationTargetResolver ObjectResolver() => new(new TestServices(
        _docker, _servers, _volumes, new LogRules(), new Accounts()));

    private Task<TargetResolution> ResolveAsync(string capabilityId, object arguments)
    {
        var capability = new CapabilityCatalog().Find(capabilityId)!;
        var args = Args(arguments);
        return ObjectResolver().ResolveAsync(new MutationRequest(capabilityId, Admin, MutationSurface.Ui,
            MutationTargets.FromArguments(capability, args), args), capability, CancellationToken.None);
    }

    /// <summary>Runs the call through a gateway with the object resolver; how often the policy was asked.</summary>
    private async Task<(MutationOutcome<int> Outcome, int Evaluated)> RefusedBeforePolicyAsync(string capabilityId, object arguments)
    {
        var catalog = new CapabilityCatalog();
        var capability = catalog.Find(capabilityId)!;
        var args = Args(arguments);
        var policy = new CountingPolicy();
        var gateway = new MutationGateway(catalog, policy, new AgentToolTestHelpers.NullMutationAudit(), [],
            NullLogger<MutationGateway>.Instance, targets: ObjectResolver());
        var outcome = await gateway.ExecuteAsync(
            new MutationRequest(capabilityId, Admin, MutationSurface.Ui, MutationTargets.FromArguments(capability, args), args),
            (_, _) => Task.FromResult(1));
        return (outcome, policy.Evaluated);
    }

    private sealed class CountingPolicy : IMutationPolicy
    {
        public int Evaluated;

        public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability)
        {
            Evaluated++;
            return new MutationPolicyDecision(MutationVerdict.Allow, "test", []);
        }
    }

    /// <summary>The volume "pgdata" on both servers; backups on edge01 and on a server that was removed.</summary>
    private sealed class Volumes : IVolumeBackupService
    {
        public bool Fail;

        public Task<IReadOnlyList<VolumeInfo>> ListVolumeInfoAsync(string? serverId, CancellationToken ct = default) =>
            Fail ? throw new InvalidOperationException("listing failed")
                : Task.FromResult<IReadOnlyList<VolumeInfo>>(serverId is "edge01" or "local"
                    ? [new VolumeInfo("pgdata", "local", new Dictionary<string, string>())]
                    : []);

        public Task<List<VolumeBackupEntity>> ListBackupsAsync(string? serverId = null, string? volumeName = null, CancellationToken ct = default) =>
            Task.FromResult(new List<VolumeBackupEntity>
            {
                new() { BackupId = "b-edge", VolumeName = "pgdata", ServerId = "edge01", FileName = "pgdata_1.tar.gz" },
                new() { BackupId = "b-gone", VolumeName = "pgdata", ServerId = "removed-host", FileName = "pgdata_2.tar.gz" },
            });

        public Task<VolumeBackupEntity> BackupVolumeAsync(string volumeName, string containerName, string? serverId = null,
            string? notes = null) => throw new NotSupportedException();
        public Task RestoreVolumeAsync(string backupId, string? targetVolume = null) => throw new NotSupportedException();
        public Task DeleteBackupAsync(string backupId) => throw new NotSupportedException();
        public Task<List<string>> ListVolumesAsync(string? serverId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class LogRules : ILogMonitorService
    {
        public IReadOnlyList<SuspendedContainer> SuspendedContainers() => [];

        public Task<List<LogAlertRuleEntity>> GetRulesAsync() => Task.FromResult(new List<LogAlertRuleEntity>
        {
            new() { RuleId = "r-1", Name = "errors" },
            new() { RuleId = "r-2", Name = "dup" },
            new() { RuleId = "r-3", Name = "dup" },
        });

        public Task<LogAlertRuleEntity> CreateRuleAsync(LogAlertRuleEntity rule) => throw new NotSupportedException();
        public Task DeleteRuleAsync(string ruleId) => throw new NotSupportedException();
        public Task ToggleRuleAsync(string ruleId, bool enabled) => throw new NotSupportedException();
    }

    private sealed class Accounts : ILocalAccountDirectory
    {
        public Task<string?> FindAsync(string email, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(email, "ops@example.com", StringComparison.OrdinalIgnoreCase) ? "ops@example.com" : null);
    }
}
