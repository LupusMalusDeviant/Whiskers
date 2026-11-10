using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Metrics;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-55: the metric retention's write path. A save is a gated configuration write (R1-11) with audit (R1-22) — through
/// the real UI runner and Mutation Gateway against the real store: Viewer, Operator and a demoted Admin are refused, the
/// kill switch refuses, an audit failure refuses before anything is published, a failed save leaves the live values
/// unchanged. A value set by the environment or appsettings.json stays read-only and is shown as such; a value that does
/// not parse never throws.
/// </summary>
public sealed class MetricRetentionSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-r155-set-{Guid.NewGuid():N}");
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly MutableUser _user = new(AppRole.Admin);

    public MetricRetentionSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string SettingsFile => Path.Combine(_dir, "app-settings.json");

    /// <summary>As the app layers it: the "environment" first, the UI-written app-settings.json last.</summary>
    private MetricRetentionService Service(Dictionary<string, string?>? environment = null, string? settingsFile = null)
    {
        var file = settingsFile ?? SettingsFile;
        var builder = new ConfigurationBuilder().AddInMemoryCollection(environment ?? []);
        if (File.Exists(file)) builder.AddJsonFile(file, optional: false, reloadOnChange: false);
        return new MetricRetentionService(builder.Build(), new AppSettingsStore(file), NullLogger<MetricRetentionService>.Instance, file);
    }

    private UiMutationRunner Runner(GuardrailPolicy? policy = null)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(), approvals: new ApprovalGate(new ApprovalStore(), store));
        return new UiMutationRunner(gateway, new CapabilityCatalog(), _user);
    }

    // As the Settings panel runs it.
    private static Task Save(UiMutationRunner runner, IMetricRetentionService service, MetricRetention values) =>
        runner.RunAsync("config.save", new { section = "metrics-retention" }, (ResolvedTarget _, CancellationToken ct) => service.SaveAsync(values, ct));

    private static readonly MetricRetention Changed = MetricRetention.Default with { RawDays = 3, HourDays = 400, AlertHistoryDays = 90 };

    [Fact]
    public void The_defaults_apply_when_nothing_is_set()
    {
        var service = Service();
        Assert.Equal(MetricRetention.Default, service.Current);
        Assert.All(service.Settings, s => Assert.False(s.ReadOnly));
        Assert.Equal(new MetricRetention(7, 30, 180, 730, 30), MetricRetention.Default);
    }

    [Fact]
    public async Task An_admin_save_is_audited_first_then_written_then_published_and_survives_a_restart()
    {
        var service = Service();
        var intentFirst = false;

        await Runner().RunAsync("config.save", new { section = "metrics-retention" }, async (ResolvedTarget _, CancellationToken ct) =>
        {
            intentFirst = _audit.Events.Contains("begin:config.save:");
            await service.SaveAsync(Changed, ct);
        });

        Assert.True(intentFirst);
        Assert.Contains("complete:Executed", _audit.Events);
        Assert.Equal(Changed, service.Current);
        var section = JsonNode.Parse(await File.ReadAllTextAsync(SettingsFile))!["Metrics"]!;
        Assert.Equal(3, section["RetentionDays"]!.GetValue<int>());
        Assert.Equal(400, section["Rollup1hRetentionDays"]!.GetValue<int>());
        Assert.Equal(Changed, Service().Current);           // read back at the next start
    }

    [Fact]
    public async Task A_save_keeps_the_other_keys_of_the_metrics_section()
    {
        await File.WriteAllTextAsync(SettingsFile, """{ "Metrics": { "CollectionIntervalSeconds": 60, "retentiondays": 9 } }""");
        var service = Service();
        Assert.Equal(9, service.Current.RawDays);

        await Save(Runner(), service, Changed);

        var section = JsonNode.Parse(await File.ReadAllTextAsync(SettingsFile))!["Metrics"]!.AsObject();
        Assert.Equal(60, section["CollectionIntervalSeconds"]!.GetValue<int>());
        Assert.Single(section, p => string.Equals(p.Key, "RetentionDays", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task A_viewer_or_operator_is_refused_and_nothing_changes(AppRole role)
    {
        var service = Service();
        _user.Role = role;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Runner(), service, Changed));

        Assert.Equal(MetricRetention.Default, service.Current);
        Assert.False(File.Exists(SettingsFile));
        Assert.Contains("denied:Denied", _audit.Events);
    }

    [Fact]
    public async Task An_admin_demoted_while_the_page_is_open_is_refused_on_the_next_save()
    {
        var service = Service();
        var runner = Runner();
        await Save(runner, service, Changed);

        _user.Role = AppRole.Operator;
        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(runner, service, MetricRetention.Default));

        Assert.Equal(Changed, service.Current);
    }

    [Fact]
    public async Task The_kill_switch_refuses_a_retention_change()
    {
        var service = Service();
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Runner(policy), service, Changed));

        Assert.Equal(MetricRetention.Default, service.Current);
    }

    [Fact]
    public async Task An_audit_failure_refuses_before_the_setting_is_published()
    {
        var service = Service();
        _audit.FailNextBegin = true;

        await Assert.ThrowsAnyAsync<Exception>(() => Save(Runner(), service, Changed));

        Assert.Equal(MetricRetention.Default, service.Current);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_failed_save_leaves_the_live_settings_unchanged()
    {
        // The settings file's directory is a file: the write cannot succeed.
        var blocker = Path.Combine(_dir, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");
        var service = Service(settingsFile: Path.Combine(blocker, "app-settings.json"));

        await Assert.ThrowsAnyAsync<Exception>(() => Save(Runner(), service, Changed));

        Assert.Equal(MetricRetention.Default, service.Current);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(366)]
    public async Task An_out_of_range_value_is_refused_before_anything_is_written(int rawDays)
    {
        var service = Service();

        await Assert.ThrowsAsync<ArgumentException>(() => Save(Runner(), service, Changed with { RawDays = rawDays }));

        Assert.Equal(MetricRetention.Default, service.Current);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_value_from_the_environment_is_read_only_and_wins_over_app_settings()
    {
        await File.WriteAllTextAsync(SettingsFile, """{ "Metrics": { "RetentionDays": 2 } }""");
        var service = Service(new() { ["Metrics:RetentionDays"] = "14" });

        Assert.Equal(14, service.Current.RawDays);
        var raw = service.Settings.Single(s => s.Field.Key == "Metrics:RetentionDays");
        Assert.True(raw.ReadOnly);
        Assert.NotNull(raw.Source);
        Assert.False(service.Settings.Single(s => s.Field.Key == "Metrics:Rollup1hRetentionDays").ReadOnly);

        // Changing it is refused; saving the others with it unchanged works and does not write it.
        await Assert.ThrowsAsync<ArgumentException>(() => Save(Runner(), service, Changed));
        await Save(Runner(), service, Changed with { RawDays = 14 });

        Assert.Equal(Changed with { RawDays = 14 }, service.Current);
        Assert.Equal(2, JsonNode.Parse(await File.ReadAllTextAsync(SettingsFile))!["Metrics"]!["RetentionDays"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("7d", 7)]
    [InlineData("0", 1)]
    [InlineData("100000", MetricRetention.MaxRawDays)]
    public void A_value_that_does_not_parse_or_is_out_of_range_never_throws(string value, int used)
    {
        File.WriteAllText(SettingsFile, $$"""{ "Metrics": { "RetentionDays": "{{value}}" } }""");

        Assert.Equal(used, Service().Current.RawDays);
    }

    private sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
