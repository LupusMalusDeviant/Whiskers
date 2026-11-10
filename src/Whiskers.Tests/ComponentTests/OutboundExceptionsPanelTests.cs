using Bunit;
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

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-54: the Admin-only notification settings list each configured channel the outbound guard refuses, with
/// the refused address — scheme, host and port only, never a URL path or query — and save the exceptions through the
/// gateway. The test host has no resource assembly, so texts render as their keys.</summary>
public class OutboundExceptionsPanelTests : BunitContext, IAsyncLifetime
{
    private readonly OutboundTestDoubles.Exceptions _exceptions = new();
    private readonly OutboundTestDoubles.Resolver _resolver = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly User _user = new();

    public OutboundExceptionsPanelTests()
    {
        _resolver.Answer("ntfy.lan", "10.0.0.5").Answer("hooks.slack.com", "93.184.215.14");
        var guard = new OutboundTargetGuard(_exceptions, _resolver);
        INotificationChannel[] channels =
        [
            new Channel("Ntfy", OutboundEndpoint.FromUrl("http://ntfy.lan:8080/alerts-PATHSECRET?auth=QUERYSECRET")!),
            new Channel("Slack", OutboundEndpoint.FromUrl("https://hooks.slack.com/services/T0/B0/X")!),
            new Channel("Webhook", OutboundEndpoint.FromUrl("http://nowhere.invalid/hook")!),
        ];

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IOutboundExceptionService>(_exceptions);
        Services.AddSingleton<IOutboundTargetReport>(new OutboundTargetReport(channels, guard));
        Services.AddSingleton<ICurrentUserService>(_user);
        var store = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(), approvals: new ApprovalGate(new ApprovalStore(), store));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Fact]
    public void The_refused_channels_are_listed_with_scheme_host_port_and_address_only()
    {
        var cut = Render<OutboundExceptionsPanel>();

        cut.WaitForAssertion(() => Assert.Contains("http://ntfy.lan:8080", cut.Find("#outbound-refusals").TextContent));
        var table = cut.Find("#outbound-refusals").TextContent;
        Assert.Contains("Ntfy", table);
        Assert.Contains("10.0.0.5", table);
        Assert.Contains("http://nowhere.invalid:80", table);
        Assert.Contains("OutboundGuard_Unresolved", table);
        Assert.DoesNotContain("Slack", table);
        Assert.DoesNotContain("PATHSECRET", cut.Markup);
        Assert.DoesNotContain("QUERYSECRET", cut.Markup);
    }

    [Fact]
    public async Task An_exception_saved_on_the_panel_goes_through_the_gateway_and_clears_the_refusal()
    {
        var cut = Render<OutboundExceptionsPanel>();
        cut.WaitForAssertion(() => cut.Find("#outbound-refusals"));

        cut.Instance.GetType().GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(cut.Instance, new List<OutboundExceptionEntry> { new() { Host = "ntfy.lan", Port = 8080, Addresses = ["10.0.0.5"] } });
        var save = typeof(OutboundExceptionsPanel).GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)save.Invoke(cut.Instance, null)!);

        Assert.Contains("begin:config.save:", _audit.Events);
        Assert.Single(_exceptions.Current.Entries);
        cut.WaitForAssertion(() => Assert.DoesNotContain("ntfy.lan", cut.Find("#outbound-refusals").TextContent));
    }

    [Fact]
    public void A_non_admin_sees_nothing_and_nothing_is_resolved()
    {
        _user.Role = AppRole.Operator;

        var cut = Render<OutboundExceptionsPanel>();

        Assert.Empty(cut.FindAll("#outbound-refusals"));
        Assert.Empty(_resolver.Asked);
    }

    private sealed class Channel(string name, OutboundEndpoint target) : INotificationChannel
    {
        public string Name => name;
        public IReadOnlyList<OutboundEndpoint> Targets => [target];
        public Task<bool> DeliverAsync(NotificationEvent evt, CancellationToken ct) => Task.FromResult(false);
        public Task SendTestAsync() => Task.CompletedTask;
    }

    private sealed class User : ICurrentUserService
    {
        public AppRole Role { get; set; } = AppRole.Admin;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
