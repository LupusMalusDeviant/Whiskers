using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Components.Shared;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>INV-17/INV-18 on the shared in-app notification log (U-39, user decision 2026-11-07: Operator). The
/// Notifications page and the bell mark the log read and clear it through the real Mutation Gateway
/// (<c>notifications.mark_read</c>, <c>notifications.clear</c>): a Viewer — also an Operator demoted while the page is
/// open — is refused at the click, the refusal is one Denied audit event and the store is never written; an Operator's
/// click runs after its audit intent. Hiding the buttons is presentation only.</summary>
public class NotificationsRoleTests : BunitContext, IAsyncLifetime
{
    private readonly RecordingStore _store = new();
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly ServersPauseResumeRoleTests.MutableUser _user = new(AppRole.Viewer);
    private readonly GuardrailPolicy _policy = GuardrailPolicy.SafeDefault();

    public NotificationsRoleTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<IInAppNotificationStore>(_store);
        var policy = new AgentToolTestHelpers.FixedGuardrailStore(_policy);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(policy), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(), approvals: new ApprovalGate(new ApprovalStore(), policy));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // MudBlazor services dispose only asynchronously; dispose the container that way.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Fact]
    public async Task A_viewer_can_neither_mark_the_log_read_nor_clear_it_and_each_refusal_is_recorded()
    {
        var page = Render<NotificationsLog>();   // as a Viewer: neither button is rendered, the handlers run anyway

        await Invoke(page, "MarkAllRead");
        await Invoke(page, "ClearAll");

        Assert.Empty(Writes());
        Assert.Equal(["denied:Denied", "denied:Denied"], Events());
    }

    [Fact]
    public void The_buttons_are_shown_from_operator_on()
    {
        Assert.DoesNotContain("NotificationsLog_Button_ClearAll", Render<NotificationsLog>().Markup);
        _user.Role = AppRole.Operator;
        Assert.Contains("NotificationsLog_Button_ClearAll", Render<NotificationsLog>().Markup);
    }

    [Fact]
    public async Task An_operator_demoted_while_the_page_is_open_cannot_clear_the_log()
    {
        _user.Role = AppRole.Operator;
        var page = Render<NotificationsLog>();
        _user.Role = AppRole.Viewer;   // the buttons are still on screen

        await Button(page, "NotificationsLog_Button_ClearAll").ClickAsync(new MouseEventArgs());
        await Button(page, "NotificationsLog_Button_MarkAllRead").ClickAsync(new MouseEventArgs());

        Assert.Equal(["denied:Denied", "denied:Denied"], Events());
        Assert.Empty(Writes());
    }

    [Fact]
    public async Task An_operator_marks_the_log_read_and_clears_it_inside_the_gateway()
    {
        _user.Role = AppRole.Operator;
        var page = Render<NotificationsLog>();

        await Button(page, "NotificationsLog_Button_MarkAllRead").ClickAsync(new MouseEventArgs());
        await Button(page, "NotificationsLog_Button_ClearAll").ClickAsync(new MouseEventArgs());

        Assert.Equal(["mark_read", "clear"], Writes());
        Assert.Equal(["begin:notifications.mark_read:", "complete:Executed", "begin:notifications.clear:", "complete:Executed"],
            Events());
    }

    [Fact]
    public async Task Opening_the_bell_with_nothing_unread_writes_nothing()
    {
        _user.Role = AppRole.Operator;
        var bell = Render<NotificationBell>();

        await Invoke(bell, "OnOpenChanged", true);

        Assert.Empty(Writes());
        Assert.Empty(Events());
    }

    [Fact]
    public async Task With_the_kill_switch_on_the_automatic_mark_is_refused_silently_and_an_explicit_clear_says_why()
    {
        _user.Role = AppRole.Operator;
        _policy.ReadOnlyMode = true;
        _store.Items.Add(new InAppNotification("n1", DateTime.UtcNow, "container_down", "web down", "", "Error"));
        var bell = Render<NotificationBell>();

        await Invoke(bell, "OnOpenChanged", true);
        Assert.Equal(["denied:Denied"], Events());
        Assert.Empty(Snackbars());   // nobody clicked: recorded, not shown

        await Invoke(bell, "Clear");
        Assert.Equal(["denied:Denied", "denied:Denied"], Events());
        Assert.Single(Snackbars(), s => s.Severity == Severity.Warning);
        Assert.Empty(Writes());
    }

    [Fact]
    public async Task A_viewer_cannot_clear_the_log_from_the_bell_and_opening_it_marks_nothing_read()
    {
        _store.Items.Add(new InAppNotification("n1", DateTime.UtcNow, "container_down", "web down", "", "Error"));
        var bell = Render<NotificationBell>();
        Assert.DoesNotContain("NotificationBell_Clear", bell.Markup);

        await Invoke(bell, "OnOpenChanged", true);
        await Invoke(bell, "Clear");

        Assert.Empty(Writes());
        Assert.Equal(["denied:Denied"], Events());   // the clear attempt; opening attempted nothing
    }

    [Fact]
    public async Task An_operator_opening_the_bell_marks_the_log_read_inside_the_gateway_and_may_clear_it()
    {
        _user.Role = AppRole.Operator;
        _store.Items.Add(new InAppNotification("n1", DateTime.UtcNow, "container_down", "web down", "", "Error"));
        var bell = Render<NotificationBell>();

        await Invoke(bell, "OnOpenChanged", true);
        await Invoke(bell, "Clear");

        Assert.Equal(["mark_read", "clear"], Writes());
        Assert.Equal(["begin:notifications.mark_read:", "complete:Executed", "begin:notifications.clear:", "complete:Executed"],
            Events());
    }

    private List<string> Events() { lock (_audit.Events) return _audit.Events.ToList(); }

    private List<string> Writes() { lock (_store.Writes) return _store.Writes.ToList(); }

    private List<Snackbar> Snackbars() => Services.GetRequiredService<ISnackbar>().ShownSnackbars.ToList();

    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> page, string text) where T : Microsoft.AspNetCore.Components.IComponent =>
        page.FindAll("button").First(b => b.TextContent.Contains(text));

    private static Task Invoke<T>(IRenderedComponent<T> component, string handler, params object[] args)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        var method = typeof(T).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return component.InvokeAsync(() => (Task)method.Invoke(component.Instance, args)!);
    }

    /// <summary>A notification log that remembers every write that reached it.</summary>
    private sealed class RecordingStore : IInAppNotificationStore
    {
        public List<InAppNotification> Items { get; } = [];
        public List<string> Writes { get; } = [];
        public IReadOnlyList<InAppNotification> Recent => Items.ToList();
        public int UnreadCount => Items.Count;
        public void Add(NotificationEvent evt) => throw new NotSupportedException();
        public void MarkAllRead() { lock (Writes) Writes.Add("mark_read"); }
        public void Clear() { lock (Writes) Writes.Add("clear"); }
        public event Action? Changed { add { } remove { } }
        public event Action<InAppNotification>? Added { add { } remove { } }
        public Task<List<InAppNotification>> QueryAsync(string? severity, string? eventType, int skip, int take,
            CancellationToken ct = default) => Task.FromResult(Items.ToList());
        public Task<int> CountAsync(string? severity, string? eventType, CancellationToken ct = default) =>
            Task.FromResult(Items.Count);
    }
}
