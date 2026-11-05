using System.Reflection;
using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Shared;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-49: the Admin-only local-accounts panel in Settings. Adding an account runs through the gateway and shows
/// the invitation code once (gone after dismissing, never part of what is recorded); the own account has no reset or
/// delete; deleting the last Admin who can sign in is refused; a non-admin sees nothing; the MFA posture is stated. Runs
/// on the real lifecycle service, session validator and gateway. The test host has no resource assembly, so texts
/// render as their keys.</summary>
public class LocalAccountsPanelTests : BunitContext, IAsyncLifetime
{
    private LocalAccountWorld _world = null!;
    private LocalAccountWorld.Acting? _acting;

    async Task IAsyncLifetime.InitializeAsync() => _world = await LocalAccountWorld.CreateAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _acting?.Dispose();
        await _world.DisposeAsync();
    }

    private IRenderedComponent<LocalAccountsPanel> RenderAs(ClaimsPrincipal principal)
    {
        _acting = _world.As(principal);
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<ILocalAccountService>(_acting.Accounts);
        Services.AddSingleton<ICurrentUserService>(_acting.User);
        Services.AddSingleton<IUiMutationRunner>(_acting.Mutations);
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<LocalAccountsPanel>();
    }

    private static T Field<T>(IRenderedComponent<LocalAccountsPanel> cut, string name) =>
        (T)typeof(LocalAccountsPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;

    private static void Set(IRenderedComponent<LocalAccountsPanel> cut, string name, object value) =>
        typeof(LocalAccountsPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, value);

    private static Task Invoke(IRenderedComponent<LocalAccountsPanel> cut, string method, params object[] args) =>
        cut.InvokeAsync(() => typeof(LocalAccountsPanel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cut.Instance, args) as Task ?? Task.CompletedTask);

    private static int Occurrences(string text, string part) =>
        (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    [Fact]
    public async Task An_added_account_goes_through_the_gateway_and_its_code_is_shown_once()
    {
        var cut = RenderAs(await _world.SessionOfAsync(LocalAccountWorld.Root));
        Assert.Contains("local-accounts-mfa", cut.Markup);            // the MFA posture is stated where Admins decide
        Assert.Contains("LocalAccounts_OwnAccount", cut.Markup);      // the own row has no reset or delete
        Assert.DoesNotContain("LocalAccounts_Button_Reset", cut.Markup);

        Set(cut, "_newEmail", "new@local.test");
        Set(cut, "_newRole", AppRole.Operator);
        await Invoke(cut, "Create");

        Assert.Contains("begin:account.create", _world.Sink.Events);
        var code = Field<object>(cut, "_issued").GetType().GetProperty("Code")!.GetValue(Field<object>(cut, "_issued")) as string;
        Assert.False(string.IsNullOrEmpty(code));
        cut.Render();
        Assert.Equal(1, Occurrences(cut.Markup, code!));
        Assert.Contains("new@local.test", cut.Markup);
        Assert.Equal(AppRole.Operator, _world.Roles.GetRole("new@local.test"));
        Assert.DoesNotContain(code!, _world.Sink.Text, StringComparison.OrdinalIgnoreCase);

        await Invoke(cut, "DismissCode");
        cut.Render();
        Assert.DoesNotContain(code!, cut.Markup);
    }

    [Fact]
    public async Task Deleting_the_last_admin_who_can_sign_in_is_refused_on_the_panel()
    {
        await _world.Roles.SetRoleAsync("gone@local.test", AppRole.Admin);   // an Admin by role with nothing to sign in with
        // An Admin still waiting for a recovery code (no password, no Google/OIDC binding) cannot sign in either.
        await _world.AddUserAsync("pending@local.test", AppRole.Admin, password: null);
        var cut = RenderAs(await _world.SessionOfAsync("pending@local.test"));
        Assert.Contains("LocalAccounts_Button_Delete", cut.Markup);

        await Invoke(cut, "Delete", LocalAccountWorld.Root);

        Assert.NotNull(await _world.FindAsync(LocalAccountWorld.Root));
        Assert.Contains("complete:Failed", _world.Sink.Events);
        Assert.Null(Field<object?>(cut, "_issued"));
    }

    [Fact]
    public async Task A_non_admin_sees_nothing()
    {
        await _world.AddUserAsync("viewer@local.test", AppRole.Viewer);
        var cut = RenderAs(await _world.SessionOfAsync("viewer@local.test"));

        Assert.DoesNotContain("local-accounts-table", cut.Markup);
        Assert.DoesNotContain(LocalAccountWorld.Root, cut.Markup);
    }
}
