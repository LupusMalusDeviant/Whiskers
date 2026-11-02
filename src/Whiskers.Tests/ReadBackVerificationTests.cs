using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Models.Dns;
using Whiskers.Services.Auth;
using Whiskers.Services.Cloud;
using Whiskers.Services.Dns;
using Whiskers.Services.Docker;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

// R1-37 (INV-43): every capability declares its verification; a declared read-back runs after the call and a state it
// does not show fails the outcome. A capability without a read-back is recorded as not verifiable, with its reason —
// never as verified.
public class ReadBackVerificationTests
{
    /// <summary>A fake of any interface: a call the test scripted answers, every other call is unsupported.</summary>
    public class Scripted<T> : DispatchProxy where T : class
    {
        public Dictionary<string, Func<object?[], object?>> On { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];

        public static (T Service, Scripted<T> Script) Create()
        {
            var service = Create<T, Scripted<T>>();
            return (service, (Scripted<T>)(object)service);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            return On.TryGetValue(method.Name, out var handler) ? handler(args ?? []) : throw new NotSupportedException(method.Name);
        }
    }

    private static MutationGateway Gateway(IServiceProvider sp, params IMutationVerifier[] overrides)
    {
        var replaced = overrides.Select(v => v.CapabilityId).ToHashSet();
        var verifiers = MutationVerifiers.Factories.Select(f => f(sp)).Where(v => !replaced.Contains(v.CapabilityId)).Concat(overrides);
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.RequireConfirmationForWrites = false;
        return new MutationGateway(new CapabilityCatalog(),
            AgentToolTestHelpers.Policy(new AgentToolTestHelpers.FixedGuardrailStore(policy)),
            new AgentToolTestHelpers.NullMutationAudit(), verifiers.ToList(), NullLogger<MutationGateway>.Instance);
    }

    private static MutationRequest Request(string capability, object args, MutationTarget target) =>
        new(capability, new MutationPrincipal(MutationPrincipalKind.Human, "admin@example.com", "admin@example.com", McpPermissionLevels.Admin),
            MutationSurface.Ui, target,
            JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));

    private static MutationTarget Host(string kind = "server") => new(kind, "srv1", null, "srv1");

    private static IServiceProvider HostReads(params (string Command, int Exit, string Output)[] answers)
    {
        var (executor, script) = Scripted<IHostCommandExecutor>.Create();
        script.On[nameof(IHostCommandExecutor.ExecuteAsync)] = args =>
        {
            var command = (string)args[1]!;
            var (_, exit, output) = answers.FirstOrDefault(a => a.Command == command);
            return Task.FromResult(output is null
                ? new CommandResult { ExitCode = -1, Error = $"unscripted: {command}" }
                : new CommandResult { ExitCode = exit, Output = output });
        };
        return new ServiceCollection().AddSingleton(executor).BuildServiceProvider();
    }

    private static void AssertFailed<T>(MutationOutcome<T> outcome, bool stateUnknown = false)
    {
        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Equal(VerificationStatus.Failed, outcome.Verification);
        Assert.Equal(stateUnknown, Assert.IsType<MutationVerificationFailedException>(outcome.Error).StateUnknown);
    }

    // ---- the contract ----

    [Fact]
    public void Every_capability_declares_its_verification_contract()
    {
        var catalog = new CapabilityCatalog();
        var verified = MutationVerifiers.Factories.Select(f => f(new ServiceCollection().BuildServiceProvider()).CapabilityId).ToList();

        Assert.Equal(verified.Count, verified.Distinct().Count());
        Assert.All(catalog.All, c =>
        {
            var contract = VerificationContracts.Find(c.Id);
            Assert.True(contract is not null, $"{c.Id} declares no verification contract");
            Assert.False(string.IsNullOrWhiteSpace(contract!.Reason), $"{c.Id} gives no reason");
            if (contract.Kind == VerificationKind.ReadBack)
                Assert.True(verified.Contains(c.Id), $"{c.Id} declares a read-back but no verifier is registered");
            else
                Assert.False(verified.Contains(c.Id), $"{c.Id} is declared not verifiable but has a verifier");
        });
        Assert.All(VerificationContracts.All.Keys, id => Assert.NotNull(catalog.Find(id)));
    }

    [Fact]
    public async Task A_capability_without_a_read_back_is_recorded_as_not_verifiable_with_its_reason()
    {
        var gateway = Gateway(new ServiceCollection().BuildServiceProvider());
        var reason = VerificationContracts.Find("host.exec")!.Reason;

        var outcome = await gateway.ExecuteAsync(Request("host.exec", new { command = "uptime" }, Host()),
            (_, _) => Task.FromResult("up 3 days"));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(VerificationStatus.NotVerifiable, outcome.Verification);
        Assert.Equal(reason, outcome.VerificationNote);
    }

    [Fact]
    public async Task A_call_the_verifier_cannot_read_back_is_not_verifiable_never_verified()
    {
        var gateway = Gateway(HostReads());

        var outcome = await gateway.ExecuteAsync(Request("firewall.set_status", new { serverId = "srv1" }, Host()),
            (_, _) => Task.FromResult("ok"));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(VerificationStatus.NotVerifiable, outcome.Verification);
        Assert.Contains("enable or disable", outcome.VerificationNote);
    }

    // ---- hosts ----

    [Theory]
    [InlineData("ufw allow 8080/tcp", true)]
    [InlineData("ufw allow 8080/udp", false)]
    [InlineData("ufw deny 8080/tcp", false)]
    [InlineData("ufw allow 80/tcp", false)]
    public async Task A_firewall_rule_must_be_listed_by_ufw(string listed, bool verified)
    {
        await AssertFirewallRule(listed, "tcp", verified);
    }

    [Theory]
    [InlineData("ufw allow 8080/any", true)]
    [InlineData("ufw allow 8080", true)]
    [InlineData("ufw allow 8080/tcp", false)]
    public async Task The_tools_both_protocol_is_read_back_as_any(string listed, bool verified) =>
        await AssertFirewallRule(listed, "both", verified);

    private static async Task AssertFirewallRule(string listed, string protocol, bool verified)
    {
        var gateway = Gateway(HostReads(("ufw show added", 0, $"Added user rules (see 'ufw status' for running firewall):\n{listed}\n")));

        var outcome = await gateway.ExecuteAsync(Request("firewall.rule_add",
            new { serverId = "srv1", port = "8080", protocol, action = "allow" }, Host()), (_, _) => Task.FromResult("Rule added"));

        if (verified) Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        else AssertFailed(outcome);
    }

    [Theory]
    [InlineData("ufw allow from 10.0.0.1 to any port 22 proto tcp", "10.0.0.1", "tcp", true)]
    [InlineData("ufw allow from 10.0.0.2 to any port 22 proto tcp", "10.0.0.1", "tcp", false)]
    [InlineData("ufw allow from 10.0.0.1 to any port 22", "10.0.0.1", "any", true)]
    [InlineData("ufw allow from 10.0.0.1 to any port 22 proto any", "10.0.0.1", "any", true)]
    [InlineData("ufw allow from 10.0.0.1 to any port 22 proto udp", "10.0.0.1", "any", false)]
    [InlineData("ufw allow 22", null, "any", true)]
    [InlineData("ufw allow 22/any", null, "any", true)]
    [InlineData("ufw allow 22/tcp", null, "any", false)]
    public void A_ufw_rule_line_matches_only_the_rule_asked_for(string line, string? from, string protocol, bool matches) =>
        Assert.Equal(matches, FirewallRuleAddVerifier.Matches(line, "allow", "22", protocol, from));

    [Theory]
    [InlineData(true, "Status: active\n", true)]
    [InlineData(true, "Status: inactive\n", false)]
    [InlineData(false, "Status: inactive\n", true)]
    public async Task The_firewall_status_must_be_the_one_asked_for(bool enable, string status, bool verified)
    {
        var gateway = Gateway(HostReads(("ufw status", 0, status)));

        var outcome = await gateway.ExecuteAsync(Request("firewall.set_status", new { serverId = "srv1", enable }, Host()),
            (_, _) => Task.FromResult("done"));

        if (verified) Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        else AssertFailed(outcome);
    }

    [Fact]
    public void Inactive_is_not_read_as_active() => Assert.False(FirewallService.Parse("Status: inactive\n").Active);

    [Theory]
    [InlineData("stop", "inactive", true)]
    [InlineData("stop", "active", false)]
    [InlineData("restart", "active", true)]
    [InlineData("restart", "failed", false)]
    public async Task A_systemd_unit_must_show_the_state_its_action_leaves(string action, string state, bool verified)
    {
        var gateway = Gateway(HostReads(("systemctl is-active 'nginx'", state == "active" ? 0 : 3, state + "\n")));

        var outcome = await gateway.ExecuteAsync(Request("systemd.manage",
            new { serverId = "srv1", serviceName = "nginx", action }, Host()), (_, _) => Task.FromResult("done"));

        if (verified) Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        else AssertFailed(outcome);
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 0, false)]
    public async Task An_nginx_site_link_must_be_there_or_gone(bool enable, int exit, bool verified)
    {
        var gateway = Gateway(HostReads(("test -L '/etc/nginx/sites-enabled/blog'", exit, "")));

        var outcome = await gateway.ExecuteAsync(Request(enable ? "nginx.site_enable" : "nginx.site_disable",
            new { serverId = "srv1", siteName = "blog" }, Host()), (_, _) => Task.FromResult("done"));

        if (verified) Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        else AssertFailed(outcome);
    }

    [Fact]
    public async Task A_host_that_cannot_be_read_fails_the_outcome_with_the_state_unknown()
    {
        var gateway = Gateway(HostReads());   // every read: exit -1, no connection

        var outcome = await gateway.ExecuteAsync(Request("firewall.set_status", new { serverId = "srv1", enable = true }, Host()),
            (_, _) => Task.FromResult("done"));

        AssertFailed(outcome, stateUnknown: true);
    }

    // ---- DNS ----

    private static IServiceProvider Dns(Func<List<DnsRecord>> list)
    {
        var (dns, script) = Scripted<IDnsRecordService>.Create();
        script.On[nameof(IDnsRecordService.ListAsync)] = _ =>
        {
            try { return Task.FromResult(list()); }
            catch (Exception ex) { return Task.FromException<List<DnsRecord>>(ex); }
        };
        return new ServiceCollection().AddSingleton(dns).BuildServiceProvider();
    }

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("203.0.113.8", false)]
    public async Task A_set_record_must_hold_the_written_value(string listed, bool verified)
    {
        var gateway = Gateway(Dns(() => [new DnsRecord("1", "blog", "A", listed, 300)]));

        var outcome = await gateway.ExecuteAsync(Request("dns.record_set",
            new { zone = "example.org", name = "blog", type = "A", value = "203.0.113.7" }, new MutationTarget("dns-zone", "example.org")),
            (_, _) => Task.FromResult("Angelegt"));

        if (verified) Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        else AssertFailed(outcome);
    }

    [Fact]
    public async Task A_deleted_record_still_listed_fails_the_outcome()
    {
        var gateway = Gateway(Dns(() => [new DnsRecord("1", "blog", "A", "203.0.113.7", 300)]));

        var outcome = await gateway.ExecuteAsync(Request("dns.record_delete",
            new { zone = "example.org", name = "blog", type = "A" }, new MutationTarget("dns-zone", "example.org")),
            (_, _) => Task.FromResult("Gelöscht"));

        AssertFailed(outcome);
    }

    [Fact]
    public async Task A_zone_the_provider_cannot_list_fails_the_outcome_with_the_state_unknown()
    {
        var gateway = Gateway(Dns(() => throw new DnsProviderException("503 Service Unavailable", 503)));

        var outcome = await gateway.ExecuteAsync(Request("dns.record_delete",
            new { zone = "example.org", name = "blog", type = "A" }, new MutationTarget("dns-zone", "example.org")),
            (_, _) => Task.FromResult("Gelöscht"));

        AssertFailed(outcome, stateUnknown: true);
    }

    // ---- cloud ----

    private static IServiceProvider Cloud(Func<string> status)
    {
        var (cloud, script) = Scripted<ICloudControlService>.Create();
        script.On[nameof(ICloudControlService.ResolveWhiskers)] = _ => new ServerConfig { Id = "web", Name = "web" };
        script.On[nameof(ICloudControlService.ResolveAsync)] = _ =>
            Task.FromResult<Whiskers.Models.Cloud.CloudServerInfo?>(new() { Status = status() });
        return new ServiceCollection().AddSingleton(cloud).BuildServiceProvider();
    }

    [Fact]
    public async Task A_power_on_is_verified_once_the_provider_reports_the_server_running()
    {
        var reads = 0;
        var sp = Cloud(() => ++reads < 3 ? "starting" : "running");
        var gateway = Gateway(sp, new CloudPowerVerifier(sp, on: true, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1)));

        var outcome = await gateway.ExecuteAsync(Request("cloud.power_on", new { server = "web" }, new MutationTarget("server", "web")),
            (_, _) => Task.FromResult("Power on accepted"));

        Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task A_shutdown_the_server_never_reached_fails_the_outcome()
    {
        var sp = Cloud(() => "running");
        var gateway = Gateway(sp, new CloudPowerVerifier(sp, on: false, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1)));

        var outcome = await gateway.ExecuteAsync(Request("cloud.shutdown", new { server = "web" }, new MutationTarget("server", "web")),
            (_, _) => Task.FromResult("Shutdown accepted"));

        AssertFailed(outcome);
        Assert.Contains("running", outcome.Error!.Message);
    }

    [Fact]
    public async Task A_cloud_verifier_without_the_cloud_module_is_not_verifiable()
    {
        var gateway = Gateway(new ServiceCollection().BuildServiceProvider());

        var outcome = await gateway.ExecuteAsync(Request("cloud.power_on", new { server = "web" }, new MutationTarget("server", "web")),
            (_, _) => Task.FromResult("Power on accepted"));

        Assert.Equal(VerificationStatus.NotVerifiable, outcome.Verification);
        Assert.Contains("not enabled", outcome.VerificationNote);
    }

    // ---- networks ----

    private static IServiceProvider Networks(params NetworkInfo[] networks)
    {
        var (docker, script) = Scripted<IDockerService>.Create();
        script.On[nameof(IDockerService.ListNetworksAsync)] = _ => Task.FromResult<IList<NetworkInfo>>(networks.ToList());
        return new ServiceCollection().AddSingleton(docker).BuildServiceProvider();
    }

    [Theory]
    [InlineData("bridge", true)]
    [InlineData("overlay", false)]
    public async Task A_created_network_must_be_listed_with_its_driver(string driver, bool verified)
    {
        var gateway = Gateway(Networks(new NetworkInfo { Id = "n1", Name = "backend", Driver = driver }));

        var outcome = await gateway.ExecuteAsync(Request("network.create", new { serverId = "srv1", name = "backend" },
            new MutationTarget("network", "", "backend", "srv1")), (_, _) => Task.FromResult("created"));

        if (verified) Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        else AssertFailed(outcome);
    }

    [Fact]
    public async Task A_container_still_attached_after_a_disconnect_fails_the_outcome()
    {
        var gateway = Gateway(Networks(new NetworkInfo
        {
            Id = "n1", Name = "backend", Containers = [new NetworkContainer { ContainerId = "aaaaaaaaaaaa", Name = "web" }],
        }));

        var outcome = await gateway.ExecuteAsync(Request("network.disconnect",
            new { serverId = "srv1", networkId = "n1", containerId = "aaaaaaaaaaaa" }, new MutationTarget("network", "n1", "backend", "srv1")),
            (_, _) => Task.FromResult("disconnected"));

        AssertFailed(outcome);
    }

    // ---- backups ----

    private static IServiceProvider VolumeBackups(bool recorded, int fileTestExit, string command)
    {
        var (executor, hostScript) = Scripted<IHostCommandExecutor>.Create();
        hostScript.On[nameof(IHostCommandExecutor.ExecuteAsync)] = args => Task.FromResult((string)args[1]! == command
            ? new CommandResult { ExitCode = fileTestExit }
            : new CommandResult { ExitCode = -1, Error = "unscripted" });
        var (backups, script) = Scripted<Whiskers.Services.Backup.IVolumeBackupService>.Create();
        script.On[nameof(Whiskers.Services.Backup.IVolumeBackupService.ListBackupsAsync)] = _ => Task.FromResult(recorded
            ? new List<VolumeBackupEntity> { new() { BackupId = "b1", FileName = "data_1.tar.gz", VolumeName = "data", ServerId = "srv1" } }
            : new List<VolumeBackupEntity>());
        return new ServiceCollection().AddSingleton(executor).AddSingleton(backups).AddSingleton(DataPathOptions.Default)
            .BuildServiceProvider();
    }

    [Theory]
    [InlineData(false, 1, true, false)]
    [InlineData(true, 1, false, false)]    // still recorded
    [InlineData(false, 0, false, false)]   // still on the host
    [InlineData(false, 2, false, true)]    // the host could not check
    public async Task A_deleted_volume_backup_must_be_gone_from_the_record_and_the_host(bool recorded, int fileTestExit,
        bool verified, bool stateUnknown)
    {
        var path = $"{DataPathOptions.Default.BackupsDir}/data_1.tar.gz";
        var gateway = Gateway(VolumeBackups(recorded, fileTestExit, $"test -e '{path}'"));

        var outcome = await gateway.ExecuteAsync(Request("volume.backup_delete",
                new { volumeName = "data", backupId = "b1", fileName = "data_1.tar.gz", serverId = "srv1" },
                new MutationTarget("volume", "data", null, "srv1")),
            (_, _) => Task.FromResult(true));

        if (verified) Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        else AssertFailed(outcome, stateUnknown);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task A_volume_backup_must_be_recorded_and_on_the_host(int fileTestExit, bool verified)
    {
        var path = $"{DataPathOptions.Default.BackupsDir}/data_1.tar.gz";
        var gateway = Gateway(VolumeBackups(recorded: true, fileTestExit, $"test -s '{path}'"));
        var backup = new VolumeBackupEntity { BackupId = "b1", FileName = "data_1.tar.gz", VolumeName = "data", ServerId = "srv1" };

        var outcome = await gateway.ExecuteAsync(Request("volume.backup", new { volumeName = "data", serverId = "srv1" },
            new MutationTarget("volume", "data", null, "srv1")), (_, _) => Task.FromResult(backup));

        if (verified) Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        else AssertFailed(outcome);
    }

    [Fact]
    public async Task A_run_now_summary_of_a_backup_is_not_verifiable_never_verified()
    {
        var gateway = Gateway(VolumeBackups(recorded: true, 0, ""));

        var outcome = await gateway.ExecuteAsync(Request("volume.backup", new { volumeName = "data", serverId = "srv1" },
            new MutationTarget("volume", "data", null, "srv1")), (_, _) => Task.FromResult("Backup done."));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(VerificationStatus.NotVerifiable, outcome.Verification);
    }

    // ---- tools that answer with an error text ----

    private static IHttpContextAccessor Admin() => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthConstants.McpLevelClaim, McpPermissionLevels.Admin)], AuthConstants.AgentSyntheticScheme)),
        },
    };

    [Fact]
    public async Task A_provider_error_a_tool_answers_as_text_is_a_failed_outcome()
    {
        var (dns, script) = Scripted<IDnsRecordService>.Create();
        script.On["get_" + nameof(IDnsRecordService.IsConfigured)] = _ => true;
        script.On[nameof(IDnsRecordService.SetAsync)] = _ =>
            Task.FromException<DnsSetResult>(new DnsProviderException("quota exceeded", 429));
        var gateway = Gateway(new ServiceCollection().BuildServiceProvider());

        var outcome = await gateway.ExecuteAsync(Request("dns.record_set",
                new { zone = "example.org", name = "blog", type = "A", value = "203.0.113.7" }, new MutationTarget("dns-zone", "example.org")),
            (_, _) => DnsTools.SetDnsRecord(Admin(), null!, dns, null!, "example.org", "blog", "A", "203.0.113.7"));

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Equal(VerificationStatus.NotRun, outcome.Verification);
        Assert.Contains("quota exceeded", outcome.Value);
    }

    [Fact]
    public async Task A_refused_tool_call_is_a_failed_outcome()
    {
        var (dns, _) = Scripted<IDnsRecordService>.Create();
        var gateway = Gateway(new ServiceCollection().BuildServiceProvider());

        var outcome = await gateway.ExecuteAsync(Request("dns.record_delete",
                new { zone = "example.org", name = "blog", type = "A" }, new MutationTarget("dns-zone", "example.org")),
            (_, _) => DnsTools.DeleteDnsRecord(new HttpContextAccessor(), null!, dns, null!, "example.org", "blog", "A"));

        Assert.Equal(MutationStatus.Failed, outcome.Status);
    }
}
