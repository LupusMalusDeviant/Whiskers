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
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-56: the Admin-only quiet-hours panel saves its rules through the gateway (audited config.save), refuses an
/// invalid time zone without changing the live rules, and shows a non-admin nothing. The test host has no resource
/// assembly, so texts render as their keys.</summary>
public class QuietHoursPanelTests : BunitContext, IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sw-r156-panel-{Guid.NewGuid():N}.json");
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly User _user = new();
    private readonly QuietHoursService _quietHours;

    public QuietHoursPanelTests()
    {
        _quietHours = new QuietHoursService(new ConfigurationBuilder().Build(), new AppSettingsStore(_file),
            NullLogger<QuietHoursService>.Instance);

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IQuietHoursService>(_quietHours);
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

    private static T Field<T>(IRenderedComponent<QuietHoursPanel> cut, string name) =>
        (T)typeof(QuietHoursPanel).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cut.Instance)!;

    private static Task Invoke(IRenderedComponent<QuietHoursPanel> cut, string method) =>
        cut.InvokeAsync(() =>
        {
            var result = typeof(QuietHoursPanel).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(cut.Instance, null);
            return result as Task ?? Task.CompletedTask;
        });

    [Fact]
    public async Task A_rule_added_on_the_panel_is_saved_through_the_gateway()
    {
        var cut = Render<QuietHoursPanel>();
        Assert.Contains("QuietHours_None", cut.Markup);

        var rules = Field<List<QuietHoursRule>>(cut, "_rules");
        rules.Add(new QuietHoursRule { Name = "Night", TimeZone = "Europe/Berlin", Start = "22:00", End = "07:00" });
        await Invoke(cut, "Save");

        Assert.Contains("begin:config.save:", _audit.Events);
        Assert.Equal("Night", Assert.Single(_quietHours.Rules).Name);
    }

    [Fact]
    public async Task An_invalid_time_zone_is_refused_and_the_live_rules_stay()
    {
        var cut = Render<QuietHoursPanel>();
        var rules = Field<List<QuietHoursRule>>(cut, "_rules");
        rules.Add(new QuietHoursRule { Name = "Night", TimeZone = "Somewhere/Else", Start = "22:00", End = "07:00" });

        await Invoke(cut, "Save");

        Assert.Empty(_quietHours.Rules);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void A_non_admin_sees_nothing()
    {
        _user.Role = AppRole.Operator;

        var cut = Render<QuietHoursPanel>();

        Assert.Empty(cut.FindAll("#quiet-hours-rules"));
    }

    private sealed class User : ICurrentUserService
    {
        public AppRole Role { get; set; } = AppRole.Admin;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
