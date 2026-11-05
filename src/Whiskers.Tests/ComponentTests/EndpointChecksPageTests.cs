using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.Mutations;
using Whiskers.Services.ServerConfig;
using static Whiskers.Tests.EndpointCheckTestDoubles;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// R1-57 review n1/n4: the /checks page. A run's detail names the addresses the host resolved — for an excepted target
/// internal ones — so a Viewer or Operator sees them masked, an Admin sees them; a switched-off or suspended check offers
/// no "Run now". The test host has no resource assembly, so texts render as their keys.
/// </summary>
public sealed class EndpointChecksPageTests : BunitContext, IAsyncLifetime
{
    private readonly Db _db = new();
    private readonly User _user = new();
    private readonly FakeServerConfig _servers = new(SshServer());
    private readonly EndpointCheckStore _store;

    public EndpointChecksPageTests()
    {
        _store = new EndpointCheckStore(_db.Scopes, _servers);
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IEndpointCheckStore>(_store);
        Services.AddSingleton<IEndpointCheckRunner>(ServersKubeconfigSaveTests.NullProxy<IEndpointCheckRunner>.Create());
        Services.AddSingleton<IEndpointCheckSlots>(new EndpointCheckSlots());
        Services.AddSingleton<IServerConfigService>(_servers);
        Services.AddSingleton<ICurrentUserService>(_user);
        var policy = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(policy),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            approvals: new ApprovalGate(new ApprovalStore(), policy));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _db.Dispose();
    }

    private async Task<EndpointCheckDefinition> CheckWithInternalResult()
    {
        var check = await _store.SaveAsync(Draft(EndpointCheckKind.Tcp, "db.lan", 5432,
            exceptions: [new OutboundExceptionEntry { Host = "db.lan", Port = 5432, Addresses = ["10.0.0.5"] }]), AdminId, default);
        await _store.RecordAsync(new EndpointCheckRun(check.Id, DateTime.UtcNow, EndpointCheckOutcome.Passed,
            "10.0.0.5: TCP connect succeeded", AnsweredAddress: "10.0.0.5", AddressesTried: 1));
        return check;
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task Below_admin_the_detail_shows_no_internal_address(AppRole role)
    {
        await CheckWithInternalResult();
        _user.Role = role;

        var cut = Render<EndpointChecks>();

        Assert.Contains("TCP connect succeeded", cut.Markup);
        Assert.DoesNotContain("10.0.0.5", cut.Markup);
        Assert.Contains("[internal address]", cut.Markup);
    }

    [Fact]
    public async Task An_admin_sees_the_address()
    {
        await CheckWithInternalResult();
        _user.Role = AppRole.Admin;

        var cut = Render<EndpointChecks>();

        Assert.Contains("10.0.0.5: TCP connect succeeded", cut.Markup);
    }

    [Fact]
    public async Task Run_now_is_offered_only_for_a_check_that_is_on()
    {
        await _store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443, name: "a-on"), AdminId, default);
        await _store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 444, name: "b-off") with { Enabled = false },
            AdminId, default);
        var suspended = await _store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 445, name: "c-suspended"), AdminId, default);
        Assert.True(await _store.SuspendAsync(suspended.Id, AdminId, "Suspended: the owner lost the Admin role."));
        _user.Role = AppRole.Admin;

        var cut = Render<EndpointChecks>();

        var buttons = cut.FindAll("button.sw-check-run-now");
        Assert.Equal(3, buttons.Count);                       // rows in name order: on, off, suspended
        Assert.False(buttons[0].HasAttribute("disabled"));
        Assert.True(buttons[1].HasAttribute("disabled"));
        Assert.True(buttons[2].HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(AppRole.Viewer, false)]
    [InlineData(AppRole.Operator, false)]
    [InlineData(AppRole.Admin, true)]
    public async Task The_suspension_reason_naming_the_former_owner_is_shown_to_admins_only(AppRole role, bool shown)
    {
        var check = await _store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443), AdminId, default);
        Assert.True(await _store.SuspendAsync(check.Id, AdminId, $"Suspended: {AdminId} is no longer Admin."));
        _user.Role = role;

        var cut = Render<EndpointChecks>();

        Assert.Contains("Checks_State_Suspended", cut.Markup);
        Assert.Equal(shown, cut.Markup.Contains(Admin, StringComparison.Ordinal));
    }

    private sealed class User : ICurrentUserService
    {
        public AppRole Role { get; set; } = AppRole.Admin;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Admin);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}
