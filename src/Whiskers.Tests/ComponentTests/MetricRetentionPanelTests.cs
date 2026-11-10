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
using Whiskers.Services.Metrics;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-55: the Admin-only retention panel shows a value set by the environment as read-only with its source, and
/// saves the editable ones through the gateway. The test host has no resource assembly, so texts render as their keys.</summary>
public class MetricRetentionPanelTests : BunitContext, IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sw-r155-panel-{Guid.NewGuid():N}.json");
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly User _user = new();
    private readonly MetricRetentionService _retention;

    public MetricRetentionPanelTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Metrics:RetentionDays"] = "14" }).Build();
        _retention = new MetricRetentionService(configuration, new AppSettingsStore(_file), NullLogger<MetricRetentionService>.Instance, _file);

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IMetricRetentionService>(_retention);
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

    [Fact]
    public void A_value_from_the_environment_is_shown_read_only_with_its_source()
    {
        var cut = Render<MetricRetentionPanel>();

        Assert.True(cut.Find("#metric-retention-RawDays").HasAttribute("disabled"));
        Assert.False(cut.Find("#metric-retention-HourDays").HasAttribute("disabled"));
        Assert.Contains("MetricRetention_ReadOnly", cut.Markup);
    }

    [Fact]
    public async Task A_save_on_the_panel_goes_through_the_gateway()
    {
        var cut = Render<MetricRetentionPanel>();
        var values = (Dictionary<string, int>)cut.Instance.GetType()
            .GetField("_values", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cut.Instance)!;
        values[nameof(MetricRetention.HourDays)] = 365;

        var save = typeof(MetricRetentionPanel).GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)save.Invoke(cut.Instance, null)!);

        Assert.Contains("begin:config.save:", _audit.Events);
        Assert.Equal(365, _retention.Current.HourDays);
        Assert.Equal(14, _retention.Current.RawDays);
    }

    [Fact]
    public void A_non_admin_sees_nothing()
    {
        _user.Role = AppRole.Operator;

        var cut = Render<MetricRetentionPanel>();

        Assert.Empty(cut.FindAll("#metric-retention-RawDays"));
    }

    private sealed class User : ICurrentUserService
    {
        public AppRole Role { get; set; } = AppRole.Admin;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
