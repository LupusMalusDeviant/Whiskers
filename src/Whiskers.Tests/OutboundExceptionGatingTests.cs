using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-54 (INV-60): an exception change is a gated configuration write (R1-11) with audit (R1-22) — through the real UI
/// runner and Mutation Gateway against the real store. Viewer, Operator and a demoted Admin are refused, the kill switch
/// refuses, an audit failure refuses before the list is published, a failed save leaves the live list as it was; a save
/// survives a restart. The start-up listing names each refused channel by scheme, host and port only and never holds up
/// the start.
/// </summary>
public sealed class OutboundExceptionGatingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-r154-{Guid.NewGuid():N}");
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly MutableUser _user = new(AppRole.Admin);

    private static readonly OutboundExceptionEntry Ntfy = new() { Host = "ntfy.lan", Port = 443, Addresses = ["10.0.0.5"] };

    public OutboundExceptionGatingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string SettingsFile => Path.Combine(_dir, "app-settings.json");

    private OutboundExceptionService Service(string? settingsFile = null)
    {
        var file = settingsFile ?? SettingsFile;
        var builder = new ConfigurationBuilder();
        if (File.Exists(file)) builder.AddJsonFile(file, optional: false, reloadOnChange: false);
        var configuration = builder.Build();
        return new OutboundExceptionService(configuration, new AppSettingsStore(file), NullLogger<OutboundExceptionService>.Instance);
    }

    private UiMutationRunner Runner(GuardrailPolicy? policy = null)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(), approvals: new ApprovalGate(new ApprovalStore(), store));
        return new UiMutationRunner(gateway, new CapabilityCatalog(), _user);
    }

    // As the Settings panel runs it.
    private static Task Save(UiMutationRunner runner, IOutboundExceptionService service, params OutboundExceptionEntry[] entries) =>
        runner.RunAsync("config.save", new { section = "outbound-exceptions" }, (ResolvedTarget _, CancellationToken ct) => service.SaveAsync(entries, ct));

    [Fact]
    public async Task An_admin_save_is_audited_first_then_written_then_published_and_survives_a_restart()
    {
        var service = Service();
        var runner = Runner();
        var before = service.Current.Version;
        var intentFirst = false;

        await runner.RunAsync("config.save", new { section = "outbound-exceptions" }, async (ResolvedTarget _, CancellationToken ct) =>
        {
            intentFirst = _audit.Events.Contains("begin:config.save:");
            await service.SaveAsync([Ntfy], ct);
        });

        Assert.True(intentFirst);
        Assert.Contains("complete:Executed", _audit.Events);
        Assert.Equal(before + 1, service.Current.Version);
        Assert.Equal("ntfy.lan:443 → 10.0.0.5", Assert.Single(service.Current.Entries).ToString());
        Assert.Equal("ntfy.lan", JsonNode.Parse(await File.ReadAllTextAsync(SettingsFile))!["OutboundGuard"]!["Exceptions"]![0]!["Host"]!.GetValue<string>());
        Assert.Single(Service().Current.Entries);       // read back at the next start
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task A_viewer_or_operator_is_refused_and_nothing_changes(AppRole role)
    {
        var service = Service();
        _user.Role = role;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Runner(), service, Ntfy));

        Assert.Empty(service.Current.Entries);
        Assert.False(File.Exists(SettingsFile));
        Assert.Contains("denied:Denied", _audit.Events);
    }

    [Fact]
    public async Task An_admin_demoted_while_the_page_is_open_is_refused_on_the_next_save()
    {
        var service = Service();
        var runner = Runner();
        await Save(runner, service, Ntfy);

        _user.Role = AppRole.Operator;
        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(runner, service));

        Assert.Single(service.Current.Entries);         // the removal did not happen
    }

    [Fact]
    public async Task The_kill_switch_refuses_an_exception_change()
    {
        var service = Service();
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Runner(policy), service, Ntfy));

        Assert.Empty(service.Current.Entries);
    }

    [Fact]
    public async Task An_audit_failure_refuses_before_the_list_is_published()
    {
        var service = Service();
        _audit.FailNextBegin = true;

        await Assert.ThrowsAnyAsync<Exception>(() => Save(Runner(), service, Ntfy));

        Assert.Empty(service.Current.Entries);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_failed_write_leaves_the_live_list_unchanged()
    {
        // The settings file's directory is a file: the write cannot succeed.
        var blocker = Path.Combine(_dir, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");
        var service = Service(Path.Combine(blocker, "app-settings.json"));

        await Assert.ThrowsAnyAsync<Exception>(() => Save(Runner(), service, Ntfy));

        Assert.Empty(service.Current.Entries);
        Assert.Equal(1, service.Current.Version);       // nothing was published
    }

    [Fact]
    public async Task An_invalid_entry_is_refused_before_anything_is_written()
    {
        var service = Service();
        await Save(Runner(), service, Ntfy);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Save(Runner(), service, Ntfy, new OutboundExceptionEntry { Host = "10.0.0.0/8", Port = 443 }));

        Assert.Single(service.Current.Entries);
    }

    [Fact]
    public void A_stored_entry_that_does_not_validate_excepts_nothing()
    {
        File.WriteAllText(SettingsFile, """
            { "OutboundGuard": { "Exceptions": [
              { "Host": "*.lan", "Port": 443 },
              { "Host": "10.0.0.5", "Port": 8080 } ] } }
            """);

        var entry = Assert.Single(Service().Current.Entries);
        Assert.Equal("10.0.0.5", entry.Host);
    }

    // ---------------------------------------------------------------- the start-up listing

    [Fact]
    public async Task The_startup_listing_names_each_refused_channel_by_scheme_host_and_port_and_never_delays_the_start()
    {
        var resolver = new BlockingResolver();
        resolver.Answers["ntfy.lan"] = [System.Net.IPAddress.Parse("10.0.0.5")];
        resolver.Answers["hooks.slack.com"] = [System.Net.IPAddress.Parse("93.184.215.14")];
        var guard = new OutboundTargetGuard(new OutboundTestDoubles.Exceptions(), resolver);
        var channels = new INotificationChannel[]
        {
            new Channel("Ntfy", new OutboundEndpoint("http", "ntfy.lan", 8080)),
            new Channel("Slack", OutboundEndpoint.FromUrl("https://hooks.slack.com/services/T0/B0/PATHSECRET")!),
            new Channel("Webhook", OutboundEndpoint.FromUrl("https://user:pw@gone.invalid:8443/hook?token=QUERYSECRET")!),
            new Channel("Email", new OutboundEndpoint("smtp", "169.254.169.254", 25)),
        };
        var logs = new NotificationDeliveryTests.ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var check = new OutboundTargetStartupCheck(new OutboundTargetReport(channels, guard),
            factory.CreateLogger<OutboundTargetStartupCheck>());

        // The resolver hangs: the start goes on regardless.
        await check.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(check.Completion.IsCompleted);
        resolver.Release.SetResult();
        await check.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var text = string.Join("\n", logs.Lines);
        Assert.Contains(logs.Lines, l => l.Contains("Ntfy") && l.Contains("http://ntfy.lan:8080") && l.Contains("10.0.0.5"));
        Assert.Contains(logs.Lines, l => l.Contains("Webhook") && l.Contains("https://gone.invalid:8443") && l.Contains("unresolved"));
        Assert.Contains(logs.Lines, l => l.Contains("Email") && l.Contains("smtp://169.254.169.254:25") && l.Contains("link-local"));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("Slack"));      // public: nothing to say
        Assert.DoesNotContain("PATHSECRET", text);
        Assert.DoesNotContain("QUERYSECRET", text);
        Assert.DoesNotContain("user:pw", text);
        await check.StopAsync(CancellationToken.None);
    }

    private sealed class Channel(string name, OutboundEndpoint target) : INotificationChannel
    {
        public string Name => name;
        public IReadOnlyList<OutboundEndpoint> Targets => [target];
        public Task<bool> DeliverAsync(NotificationEvent evt, CancellationToken ct) => Task.FromResult(false);
        public Task SendTestAsync() => Task.CompletedTask;
    }

    private sealed class BlockingResolver : IOutboundDnsResolver
    {
        public Dictionary<string, System.Net.IPAddress[]> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            await Release.Task.WaitAsync(ct);
            return Answers.TryGetValue(host, out var a) ? a : throw new System.Net.Sockets.SocketException(11001);
        }
    }

    private sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
