using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>INV-17 on the firewall (U-15): the add-rule dialog's submit has no role check of its own — only the page
/// that opens it checks — so these tests open the real dialog and click its real submit button as a role below Admin
/// (the catalog level of <c>firewall.rule_add</c>). The real Mutation Gateway refuses at the click, records a Denied
/// event and <see cref="IFirewallService"/> is never called; an Admin's click adds the rule after its audit intent.
/// Removing a rule on the page is refused below Admin by the page's own check, recorded as the gateway's Denied event.</summary>
public class FirewallRuleRoleTests : BunitContext, IAsyncLifetime
{
    private readonly RecordingFirewall _firewall = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly ServersPauseResumeRoleTests.MutableUser _user = new(AppRole.Admin);

    public FirewallRuleRoleTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<IFirewallService>(_firewall);
        Services.AddSingleton(NullProxy<Whiskers.Services.ServerConfig.IServerConfigService>.Create());
        Services.AddSingleton<Whiskers.Modules.IModuleRegistry>(new HostManagementEnabled());
        var store = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), store));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task Below_admin_the_dialog_submit_is_refused_recorded_and_adds_no_rule(AppRole role)
    {
        _user.Role = AppRole.Admin;   // the dialog is open, as if an Admin had opened it
        var dialog = await OpenDialog();
        _user.Role = role;            // and the role changed before the click

        await Submit(dialog, "8080");

        dialog.WaitForAssertion(() => Assert.Equal(["denied:Denied"], Events()), TimeSpan.FromSeconds(10));
        Assert.Empty(Calls());
    }

    [Fact]
    public async Task An_admin_adds_the_rule_through_the_gateway_after_its_audit_intent()
    {
        var dialog = await OpenDialog();

        await Submit(dialog, "8080");

        dialog.WaitForAssertion(() => Assert.Equal(["add:srv-1:8080:tcp:allow:"], Calls()), TimeSpan.FromSeconds(10));
        Assert.Equal(["begin:firewall.rule_add:", "complete:Executed"], Events());
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task Below_admin_removing_a_rule_is_refused_recorded_and_removes_nothing(AppRole role)
    {
        _user.Role = role;
        var page = Render<Firewall>(p => p.Add(x => x.ServerId, "srv-1"));
        var method = typeof(Firewall).GetMethod("DeleteRule", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await page.InvokeAsync(() => (Task)method.Invoke(page.Instance, [1])!);

        Assert.Equal(["denied:Denied"], Events());
        Assert.Empty(Calls());
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenDialog()
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<AddFirewallRuleDialog> { { x => x.ServerId, "srv-1" } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<AddFirewallRuleDialog>("add", parameters));
        // Wait for the submit button itself: the selects and the popover provider still re-render after the first input.
        provider.WaitForAssertion(() => Assert.NotNull(SubmitButton(provider)), TimeSpan.FromSeconds(10));
        return provider;
    }

    private static AngleSharp.Dom.IElement? SubmitButton(IRenderedComponent<MudDialogProvider> dialog) =>
        dialog.FindAll("button").LastOrDefault(b => b.TextContent.Contains("AddFirewallRuleDialog_Button_Submit"));

    /// <summary>Types the port and clicks submit. Each element is found again right before its event, and both events
    /// are awaited: a re-render between finding and firing leaves a stale event-handler id, which bUnit rejects at once
    /// (seen once on a loaded full-suite run); such an attempt is retried, it never reached the dialog.</summary>
    private static async Task Submit(IRenderedComponent<MudDialogProvider> dialog, string port)
    {
        await Retrying(() => dialog.FindAll("input").First().ChangeAsync(new ChangeEventArgs { Value = port }));
        await Retrying(() => SubmitButton(dialog)!.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
    }

    private static async Task Retrying(Func<Task> fire)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await fire(); return; }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 5) { await Task.Delay(50); }
        }
    }

    private List<string> Calls() { lock (_firewall.Calls) return _firewall.Calls.ToList(); }

    private List<string> Events() { lock (_audit.Events) return _audit.Events.ToList(); }

    /// <summary>The firewall page sits behind the host-management module guard.</summary>
    private sealed class HostManagementEnabled : Whiskers.Modules.IModuleRegistry
    {
        public IReadOnlyList<Whiskers.Modules.NavItem> NavItems => [];
        public IReadOnlyList<Type> McpToolTypes => [];
        public bool IsEnabled(string moduleId) => moduleId == "host-management";
    }

    /// <summary>Remembers every write that reached the firewall.</summary>
    private sealed class RecordingFirewall : IFirewallService
    {
        public List<string> Calls { get; } = [];
        public Task<FirewallStatus> GetStatusAsync(string serverId) =>
            Task.FromResult(new FirewallStatus { Active = true, Rules = [new FirewallRule { Number = 1, Port = "22" }] });
        public Task<CommandResult> AddRuleAsync(string serverId, string port, string protocol = "tcp", string action = "allow", string? from = null)
        {
            lock (Calls) Calls.Add($"add:{serverId}:{port}:{protocol}:{action}:{from}");
            return Task.FromResult(new CommandResult());
        }
        public Task<CommandResult> RemoveRuleAsync(string serverId, int ruleNumber)
        {
            lock (Calls) Calls.Add($"remove:{serverId}:{ruleNumber}");
            return Task.FromResult(new CommandResult());
        }
        public Task<CommandResult> SetStatusAsync(string serverId, bool enable)
        {
            lock (Calls) Calls.Add($"status:{serverId}:{enable}");
            return Task.FromResult(new CommandResult());
        }
    }
}
