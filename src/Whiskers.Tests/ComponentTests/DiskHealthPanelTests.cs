using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Shared;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Metrics.DiskHealth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using static Whiskers.Tests.DiskHealthFixtures;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// R1-58 (contract 0005): a Viewer sees the dashboard's "Disks" block — overall state and one line per item — and no
/// Admin-only control; nothing is shown while the feature is off. The settings switch is Admin-only and saves through the
/// gateway. The test host has no resource assembly, so texts render as their keys.
/// </summary>
public class DiskHealthPanelTests : BunitContext, IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sw-r158-panel-{Guid.NewGuid():N}.json");
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly User _user = new();
    private readonly DiskHealthSettingsService _settings;
    private readonly DiskHealthService _readings = new();

    public DiskHealthPanelTests()
    {
        _settings = new DiskHealthSettingsService(new ConfigurationBuilder().Build(), new AppSettingsStore(_file),
            NullLogger<DiskHealthSettingsService>.Instance);

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IDiskHealthSettingsService>(_settings);
        Services.AddSingleton<IDiskHealthService>(_readings);
        Services.AddSingleton<ICurrentUserService>(_user);
        var store = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(), approvals: new ApprovalGate(new ApprovalStore(), store));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        try { File.Delete(_file); } catch { /* best effort */ }
    }

    private async Task SwitchOn()
    {
        await _settings.SaveAsync(true);
        Assert.True(_settings.Enabled);
    }

    [Fact]
    public async Task A_viewer_sees_the_block_with_the_overall_state_and_one_line_per_item_and_no_admin_control()
    {
        await SwitchOn();
        _readings.Set(Read(Queries(smart: SmartFailed)));
        _user.Role = AppRole.Viewer;

        var cut = Render<DiskHealthBlock>(p => p.Add(b => b.ServerId, "badwolf"));

        Assert.NotNull(cut.Find("#disk-health-badwolf"));
        Assert.Contains("DiskHealth_State_DegradedOrFailing", cut.Find("#disk-health-overall-badwolf").TextContent);
        Assert.Equal(4, cut.FindAll(".sw-disk-health-items tr").Count);
        Assert.Contains("/dev/sda", cut.Markup);
        Assert.Contains("SMART overall-health failed", cut.Markup);
        // Read-only: no switch, no button, no input of any kind.
        Assert.Empty(cut.FindAll("input"));
        Assert.Empty(cut.FindAll("button"));
        Assert.Empty(cut.FindAll("#disk-health-enabled"));
    }

    [Fact]
    public async Task A_name_from_the_host_is_rendered_as_text()
    {
        await SwitchOn();
        var hostile = MdHealthy.Replace("\"device\":\"md0\"", "\"device\":\"<img src=x onerror=alert(1)>\"");
        _readings.Set(Read(Queries(md: hostile)));
        _user.Role = AppRole.Viewer;

        var cut = Render<DiskHealthBlock>(p => p.Add(b => b.ServerId, "badwolf"));

        Assert.Empty(cut.FindAll("img"));
        Assert.Contains("&lt;img", cut.Markup);
    }

    [Fact]
    public void Nothing_is_shown_while_the_feature_is_off()
    {
        _readings.Set(HealthyReading());

        var cut = Render<DiskHealthBlock>(p => p.Add(b => b.ServerId, "badwolf"));

        Assert.Empty(cut.FindAll("#disk-health-badwolf"));
        Assert.True(string.IsNullOrWhiteSpace(cut.Markup));
    }

    [Fact]
    public async Task An_Unsupported_host_and_one_not_read_yet_say_so()
    {
        await SwitchOn();
        _readings.Set(Read(DiskHealthQueries.WithoutEndpoint()));

        Assert.Contains("DiskHealth_Block_Unsupported", Render<DiskHealthBlock>(p => p.Add(b => b.ServerId, "badwolf")).Markup);
        Assert.Contains("DiskHealth_Block_NotReadYet", Render<DiskHealthBlock>(p => p.Add(b => b.ServerId, "other")).Markup);
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public void The_settings_switch_is_not_shown_to_a_non_admin(AppRole role)
    {
        _user.Role = role;

        var cut = Render<DiskHealthPanel>();

        Assert.Empty(cut.FindAll("#disk-health-enabled"));
        Assert.Empty(cut.FindAll("input"));
    }

    [Fact]
    public async Task The_admin_switch_saves_through_the_gateway_and_links_the_operations_guide()
    {
        var cut = Render<DiskHealthPanel>();
        Assert.NotEmpty(cut.FindAll("#disk-health-enabled"));
        Assert.Contains("docs/operations/disk-health.md", cut.Find("#disk-health-guide").GetAttribute("href"));

        var toggle = typeof(DiskHealthPanel).GetMethod("Toggle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)toggle.Invoke(cut.Instance, [true])!);

        Assert.Contains("begin:config.save:", _audit.Events);
        Assert.True(_settings.Enabled);
    }

    [Fact]
    public async Task A_refused_save_shows_the_setting_unchanged()
    {
        var cut = Render<DiskHealthPanel>();
        _user.Role = AppRole.Operator;                     // demoted while the page is open

        var toggle = typeof(DiskHealthPanel).GetMethod("Toggle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)toggle.Invoke(cut.Instance, [true])!);

        Assert.False(_settings.Enabled);
        var shown = (bool)typeof(DiskHealthPanel).GetField("_enabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.False(shown);
    }

    [Fact]
    public async Task A_reading_older_than_two_cycles_is_shown_as_Unknown_with_its_age()
    {
        await SwitchOn();
        var old = DateTime.UtcNow.AddHours(-3);
        _readings.Set(HealthyReading() with { ReadUtc = old });
        _user.Role = AppRole.Viewer;

        var cut = Render<DiskHealthBlock>(p => p.Add(b => b.ServerId, "badwolf"));

        Assert.Contains("DiskHealth_State_Unknown", cut.Find("#disk-health-overall-badwolf").TextContent);
        Assert.Contains("DiskHealth_Block_Stale", cut.Markup);
    }

    [Fact]
    public void A_value_set_by_the_environment_is_shown_read_only_with_its_source()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DiskHealth:Enabled"] = "true" }).Build();
        Services.AddSingleton<IDiskHealthSettingsService>(new DiskHealthSettingsService(configuration, new AppSettingsStore(_file),
            NullLogger<DiskHealthSettingsService>.Instance, _file));

        var cut = Render<DiskHealthPanel>();

        Assert.NotEmpty(cut.FindAll("#disk-health-readonly"));
        Assert.Contains("DiskHealth_ReadOnly", cut.Markup);
        Assert.Contains(cut.FindAll("input"), i => i.HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_first_read_that_failed_says_not_read_yet_with_the_reason()
    {
        await SwitchOn();
        _readings.Set(Read(DiskHealthQueries.AllFailed("the metrics endpoint could not be reached (ConnectionError)")));

        var cut = Render<DiskHealthBlock>(p => p.Add(b => b.ServerId, "badwolf"));

        Assert.Contains("DiskHealth_Block_NotReadYetFailed", cut.Markup);
        Assert.DoesNotContain("DiskHealth_Block_Unsupported", cut.Markup);
        Assert.Empty(cut.FindAll("#disk-health-overall-badwolf"));
    }

    [Fact]
    public async Task A_fresh_reading_says_when_it_was_made()
    {
        await SwitchOn();
        _readings.Set(HealthyReading() with { ReadUtc = DateTime.UtcNow.AddMinutes(-2) });

        var cut = Render<DiskHealthBlock>(p => p.Add(b => b.ServerId, "badwolf"));

        Assert.Contains("DiskHealth_State_Healthy", cut.Find("#disk-health-overall-badwolf").TextContent);
        Assert.Contains("DiskHealth_Block_ReadAgo", cut.Find("#disk-health-read-badwolf").TextContent);
        Assert.DoesNotContain("DiskHealth_Block_Stale", cut.Markup);
    }

    private sealed class User : ICurrentUserService
    {
        public AppRole Role { get; set; } = AppRole.Admin;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
