using Bunit;
using Microsoft.AspNetCore.Components.Web;
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
using static Whiskers.Tests.AutomationHandoverWorld;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// Contract 0020: the handover dialog over the real handover service and stores. Eligible items are preselected, the
/// ones it cannot hand over are greyed with the reason, the new owner's check deselects what they cannot hold, Cancel is
/// the default and nothing is bound to Enter, a failed check is the gateway's Denied event before anything runs, and
/// only an Admin sees the button that opens the dialog.
/// </summary>
public sealed class AutomationHandoverDialogTests : BunitContext, IAsyncLifetime
{
    private const string AppA = "0123456789ab";
    private readonly AutomationHandoverWorld _w = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly Person _user = new();

    public AutomationHandoverDialogTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<IAutomationHandover>(_ => _w.Handover());
        var policy = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(policy), _audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), policy),
            targets: TestTargets.Resolver());
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        // The panel's other dependencies.
        Services.AddSingleton<IAutomationOwnerStore>(_w.Fleet);
        Services.AddSingleton<IAutomationOwnerResolver>(_ => _w.Resolver);
        Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<AutoPatchSettings>>(
            new StaticOptionsMonitor<AutoPatchSettings>(new AutoPatchSettings()));
        Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<AutomationOptions>>(
            new StaticOptionsMonitor<AutomationOptions>(new AutomationOptions()));
        Services.AddSingleton<Whiskers.Services.Vpn.IVpnBootstrapState>(new Whiskers.Services.Vpn.VpnBootstrapState());
        Services.AddSingleton<IRoleService>(_w.Roles);
        Services.AddSingleton<IWhitelistService>(_w.Whitelist);
        Services.AddSingleton<ILocalAccountService>(NullProxy<ILocalAccountService>.Create());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _w.Dispose();
    }

    private sealed class Person : ICurrentUserService
    {
        public AppRole Role { get; set; } = AppRole.Admin;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(Admin);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync()
    {
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<AutomationHandoverDialog>("handover",
            new DialogParameters<AutomationHandoverDialog>
            {
                { d => d.FromOwnerId, GoneId },
                { d => d.FromName, Gone },
                { d => d.Candidates, new[] { NewId, StillId, "key:k1" } },
            }));
        provider.WaitForElement("[data-action=cancel]");
        return provider;
    }

    /// <summary>The rendered check box of <paramref name="itemId"/>'s row.</summary>
    private static AngleSharp.Html.Dom.IHtmlInputElement Input(IRenderedComponent<MudDialogProvider> provider, string itemId) =>
        (AngleSharp.Html.Dom.IHtmlInputElement)provider.Find($"[data-item='{itemId}'] input[type=checkbox]");

    /// <summary>The check box of <paramref name="itemId"/>: one per item row, in the rows' order.</summary>
    private static MudCheckBox<bool> BoxOf(IRenderedComponent<MudDialogProvider> provider, string itemId)
    {
        var rows = provider.FindAll("[data-item]").Select(r => r.GetAttribute("data-item")).ToList();
        var boxes = provider.FindComponents<MudCheckBox<bool>>();
        Assert.Equal(rows.Count, boxes.Count);
        return boxes[rows.IndexOf(itemId)].Instance;
    }

    private static Task ClickAsync(IRenderedComponent<MudDialogProvider> provider, string action) =>
        provider.InvokeAsync(() => provider.Find($"[data-action={action}]").Click());

    private static Task PickAsync(IRenderedComponent<MudDialogProvider> provider, string owner) =>
        provider.InvokeAsync(() => provider.FindComponent<MudSelect<string>>().Instance.ValueChanged.InvokeAsync(owner));

    [Fact]
    public async Task Eligible_items_are_preselected_and_can_be_deselected()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedTaskAsync("t2", GoneId);
        var provider = await OpenAsync();

        Assert.True(Input(provider, "t1").IsChecked);
        Assert.True(Input(provider, "t2").IsChecked);
        await provider.InvokeAsync(() => BoxOf(provider, "t1").ValueChanged.InvokeAsync(false));
        await ClickAsync(provider, "next");
        await PickAsync(provider, NewId);
        await ClickAsync(provider, "next");
        await ClickAsync(provider, "handover");

        provider.WaitForElement("[data-result=t2]");
        Assert.Equal(GoneId, (await _w.TaskAsync("t1")).CreatedBy);
        Assert.Equal(NewId, (await _w.TaskAsync("t2")).CreatedBy);
    }

    [Fact]
    public async Task CannotJudge_and_key_provenance_items_are_greyed_and_not_selectable()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedWebhookAsync("h1", GoneId, action: "frobnicate");
        await _w.SeedTaskAsync("keymade", GoneId, via: "key:k1");
        var provider = await OpenAsync();

        Assert.True(Input(provider, "t1").IsChecked);
        foreach (var id in new[] { "h1", "keymade" })
        {
            var box = Input(provider, id);
            Assert.False(box.IsChecked);
            Assert.True(box.IsDisabled);
        }
        Assert.Contains("AutomationHandover_Why_CannotJudge", provider.Find("[data-item=h1]").TextContent);
        Assert.Contains("AutomationHandover_Why_Key", provider.Find("[data-item=keymade]").TextContent);
    }

    [Fact]
    public async Task Items_the_new_owner_cannot_hold_are_greyed_with_the_reason()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        await _w.SeedAppAsync(AppA, GoneId);   // a Git deployment needs an Admin; Still is an Operator
        var provider = await OpenAsync();
        await ClickAsync(provider, "next");

        await PickAsync(provider, StillId);

        Assert.Contains("needs Admin", provider.Find($"[data-refused='{AppA}']").TextContent);
        Assert.Empty(provider.FindAll("[data-refused=t1]"));
        await ClickAsync(provider, "next");
        Assert.Contains("AutomationHandover_Summary", provider.Find("[data-summary]").TextContent);
        await ClickAsync(provider, "handover");
        provider.WaitForElement("[data-result=t1]");
        Assert.Equal(GoneId, (await _w.Git.GetAppAsync(AppA))!.Owner);
        Assert.Equal(StillId, (await _w.TaskAsync("t1")).CreatedBy);
    }

    [Fact]
    public async Task Cancel_is_default_and_Enter_hands_over_nothing()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        var provider = await OpenAsync();
        await ClickAsync(provider, "next");
        await PickAsync(provider, NewId);
        await ClickAsync(provider, "next");
        provider.WaitForElement("[data-action=handover]");

        var buttons = provider.FindAll(".mud-dialog-actions button");
        Assert.Equal("cancel", buttons[0].GetAttribute("data-action"));
        Assert.Contains("mud-button-filled", buttons[0].ClassName);
        Assert.DoesNotContain("mud-button-filled", provider.Find("[data-action=handover]").ClassName);
        Assert.All(buttons, b => Assert.Equal("button", b.GetAttribute("type")));
        Assert.All(buttons, b => Assert.False(b.HasAttribute("autofocus")));
        Assert.Empty(provider.FindAll(".mud-dialog form"));
        foreach (var element in provider.FindAll(".mud-dialog, .mud-dialog-content"))
            try { await element.KeyDownAsync(new KeyboardEventArgs { Key = "Enter" }); }
            catch (MissingEventHandlerException) { /* nothing listens to it */ }

        Assert.Equal(GoneId, (await _w.TaskAsync("t1")).CreatedBy);
        Assert.DoesNotContain(_audit.Events, e => e.StartsWith("begin:automation.handover", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_check_is_refused_before_RunAsync_with_a_denied_event()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        var provider = await OpenAsync();
        await ClickAsync(provider, "next");
        await PickAsync(provider, NewId);
        await ClickAsync(provider, "next");
        _w.Roles.Map[NewOwner] = AppRole.Viewer;   // demoted while the summary is open

        await ClickAsync(provider, "handover");

        Assert.Equal(["denied:Denied"], _audit.Events);
        Assert.Equal(GoneId, (await _w.TaskAsync("t1")).CreatedBy);
    }

    [Fact]
    public async Task Non_admins_see_no_hand_over_button()
    {
        await _w.SeedTaskAsync("t1", GoneId);
        _user.Role = AppRole.Operator;
        var asOperator = Render<AutomationOwnersPanel>();
        asOperator.WaitForElement($"[data-owner='{GoneId}']");
        Assert.Empty(asOperator.FindAll("[data-action=handover-open]"));

        _user.Role = AppRole.Admin;
        var asAdmin = Render<AutomationOwnersPanel>();
        asAdmin.WaitForElement("[data-action=handover-open]");
    }
}
