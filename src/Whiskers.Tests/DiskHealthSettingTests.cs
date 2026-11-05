using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Metrics.DiskHealth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-58 (contract 0005): disk health is off by default and switching it is a gated configuration write (R1-11) with
/// audit (R1-22) — through the real UI runner and Mutation Gateway against the real store: Viewer, Operator and a demoted
/// Admin are refused, the kill switch and an audit failure refuse, and a failed save leaves it off.
/// </summary>
public sealed class DiskHealthSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-r158-set-{Guid.NewGuid():N}");
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly MutableUser _user = new(AppRole.Admin);

    public DiskHealthSettingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string SettingsFile => Path.Combine(_dir, "app-settings.json");

    private DiskHealthSettingsService Service(string? settingsFile = null)
    {
        var file = settingsFile ?? SettingsFile;
        var builder = new ConfigurationBuilder();
        if (File.Exists(file)) builder.AddJsonFile(file, optional: false, reloadOnChange: false);
        return new DiskHealthSettingsService(builder.Build(), new AppSettingsStore(file), NullLogger<DiskHealthSettingsService>.Instance);
    }

    private UiMutationRunner Runner(GuardrailPolicy? policy = null)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), store));
        return new UiMutationRunner(gateway, new CapabilityCatalog(), _user);
    }

    // As the Settings panel runs it.
    private static Task Save(UiMutationRunner runner, IDiskHealthSettingsService service, bool enabled) =>
        runner.RunAsync("config.save", new { section = "diskhealth" }, ct => service.SaveAsync(enabled, ct));

    [Fact]
    public void It_is_off_by_default()
    {
        Assert.False(Service().Enabled);
        Assert.False(new DiskHealthSettings().Enabled);
        Assert.Equal("DiskHealth", DiskHealthSettings.SectionName);
    }

    [Fact]
    public void A_section_that_does_not_bind_keeps_it_off_and_never_throws()
    {
        File.WriteAllText(SettingsFile, """{ "DiskHealth": { "Enabled": "sometimes" } }""");
        Assert.False(Service().Enabled);
    }

    [Fact]
    public async Task An_admin_save_is_audited_first_then_written_then_published_and_survives_a_restart()
    {
        var service = Service();
        var intentFirst = false;

        await Runner().RunAsync("config.save", new { section = "diskhealth" }, async ct =>
        {
            intentFirst = _audit.Events.Contains("begin:config.save:");
            await service.SaveAsync(true, ct);
        });

        Assert.True(intentFirst);
        Assert.Contains("complete:Executed", _audit.Events);
        Assert.True(service.Enabled);
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(SettingsFile))!["DiskHealth"]!["Enabled"]!.GetValue<bool>());
        Assert.True(Service().Enabled);              // read back at the next start
    }

    [Fact]
    public async Task A_save_replaces_another_spelling_of_the_key()
    {
        await File.WriteAllTextAsync(SettingsFile, """{ "DiskHealth": { "enabled": true } }""");
        var service = Service();
        Assert.True(service.Enabled);

        await Save(Runner(), service, false);

        var section = JsonNode.Parse(await File.ReadAllTextAsync(SettingsFile))!["DiskHealth"]!.AsObject();
        Assert.Single(section, p => string.Equals(p.Key, "Enabled", StringComparison.OrdinalIgnoreCase));
        Assert.False(Service().Enabled);
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task A_viewer_or_operator_is_refused_and_it_stays_off(AppRole role)
    {
        var service = Service();
        _user.Role = role;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Runner(), service, true));

        Assert.False(service.Enabled);
        Assert.False(File.Exists(SettingsFile));
        Assert.Contains("denied:Denied", _audit.Events);
    }

    [Fact]
    public async Task An_admin_demoted_while_the_page_is_open_is_refused_on_the_next_save()
    {
        var service = Service();
        var runner = Runner();
        await Save(runner, service, true);

        _user.Role = AppRole.Operator;
        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(runner, service, false));

        Assert.True(service.Enabled);
    }

    [Fact]
    public async Task The_kill_switch_refuses_and_it_stays_off()
    {
        var service = Service();
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Runner(policy), service, true));

        Assert.False(service.Enabled);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task An_audit_failure_refuses_before_the_setting_is_published()
    {
        var service = Service();
        _audit.FailNextBegin = true;

        await Assert.ThrowsAnyAsync<Exception>(() => Save(Runner(), service, true));

        Assert.False(service.Enabled);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_failed_save_leaves_it_off()
    {
        // The settings file's directory is a file: the write cannot succeed.
        var blocker = Path.Combine(_dir, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");
        var service = Service(settingsFile: Path.Combine(blocker, "app-settings.json"));

        await Assert.ThrowsAnyAsync<Exception>(() => Save(Runner(), service, true));

        Assert.False(service.Enabled);
    }

    private sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
