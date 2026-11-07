using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>INV-17/INV-18, approval decisions on the real Approvals page handler. The handler decides as the person
/// signed in now (<see cref="ApprovalResolver.CurrentAsync"/>), and the store refuses a resolver below the approval's
/// level for approving and rejecting alike (R1-15): a Viewer — also an Operator demoted while the page is open —
/// leaves the approval pending and releases nothing waiting on it; an Operator decides a write-level approval.
/// The refusal is shown to the user; it is logged as a warning, not written as an audit event: an approval decision is
/// the approval store's own record, not a gateway capability (U-33). The store-level rules themselves are pinned by
/// <c>ApprovalSeparationOfDutyTests</c>.</summary>
public class ApprovalDecisionRoleTests : BunitContext, IAsyncLifetime
{
    private readonly ApprovalStore _store = new();
    private readonly List<bool> _released = [];
    private readonly ServersPauseResumeRoleTests.MutableUser _user = new(AppRole.Viewer);
    private Approval _approval = null!;

    public ApprovalDecisionRoleTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<IApprovalStore>(_store);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _approval = _store.CreateAsync(
            new ApprovalRequest("s1", "call-1", "key:k1", "mcp-direct", "stop_container", McpPermissionLevels.Write, null,
                "needs a human", Requesters: ["key:k1"]),
            d => { lock (_released) _released.Add(d); return Task.CompletedTask; }).GetAwaiter().GetResult();
    }

    // MudBlazor services dispose only asynchronously; dispose the container that way.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_viewer_can_neither_approve_nor_reject_and_nothing_is_released(bool approve)
    {
        var page = Render<ApprovalsView>();

        await Resolve(page, approve);

        Assert.Equal(ApprovalStatus.Pending, _store.Get(_approval.Id)!.Status);
        Assert.Empty(_released);
        Assert.Contains(Snackbars(), s => s.Message == ApprovalDecisionMessages.Key(ApprovalDecisionResult.InsufficientLevel));
    }

    [Fact]
    public async Task An_operator_demoted_while_the_page_is_open_cannot_reject()
    {
        _user.Role = AppRole.Operator;
        var page = Render<ApprovalsView>();   // the decision buttons are on screen
        _user.Role = AppRole.Viewer;

        await Resolve(page, approve: false);

        Assert.Equal(ApprovalStatus.Pending, _store.Get(_approval.Id)!.Status);
        Assert.Empty(_released);
        Assert.Contains(Snackbars(), s => s.Message == ApprovalDecisionMessages.Key(ApprovalDecisionResult.InsufficientLevel));
    }

    [Fact]
    public async Task An_operator_rejects_a_write_level_approval()
    {
        _user.Role = AppRole.Operator;
        var page = Render<ApprovalsView>();

        await Resolve(page, approve: false);

        Assert.Equal(ApprovalStatus.Rejected, _store.Get(_approval.Id)!.Status);
        Assert.Equal([false], _released);
        Assert.Empty(Snackbars());
    }

    private List<Snackbar> Snackbars() => Services.GetRequiredService<ISnackbar>().ShownSnackbars.ToList();

    private Task Resolve(IRenderedComponent<ApprovalsView> page, bool approve) => page.InvokeAsync(() => (Task)typeof(ApprovalsView)
        .GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, [_approval.Id, approve])!);
}
