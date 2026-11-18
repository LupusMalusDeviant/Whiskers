using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Shared;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>Contract 0019 (D2, D5, INV-65): the "Approval needed" dialog, shown by the real
/// <see cref="MudInlineApprovalPrompt"/> through MudBlazor's dialog service, on an approval raised by the real
/// <see cref="ApprovalGate"/> into the real <see cref="ApprovalStore"/>. Approving is a separate act: no button is the
/// default, the approve button is armed only after the delay, the summary is the stored card (payloads withheld), and
/// the requester's own input is shown from memory only.</summary>
public sealed class InlineApprovalDialogTests : BunitContext, IAsyncLifetime
{
    private const string Admin = InlineApprovalRunnerTests.AdminEmail;
    private const string Secret = "select owner_secret_7f3a from accounts";

    private readonly Clock _time = new();
    private readonly GuardrailPolicy _policy = GuardrailPolicy.SafeDefault();
    private readonly ApprovalStore _store;
    private readonly InlineApprovalCircuit _circuit = new();
    private readonly InlineApprovalRunnerTests.Person _user = new();
    private readonly CapturingLogs _logs = new();

    public InlineApprovalDialogTests()
    {
        _store = new ApprovalStore(time: _time, policy: new AgentToolTestHelpers.FixedGuardrailStore(_policy));
        Services.AddLogging(b => b.AddProvider(_logs));
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<IApprovalStore>(_store);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _circuit.OnCircuitOpenedAsync(null!, default).GetAwaiter().GetResult();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    /// <summary>A db.query on the control plane, raised by the gate for the web UI with payloads withheld.</summary>
    private async Task<string> RaiseAsync()
    {
        var gate = new ApprovalGate(_store, time: _time);
        var args = new Dictionary<string, JsonElement>
        {
            ["containerId"] = JsonSerializer.SerializeToElement("whiskers"),
            ["query"] = JsonSerializer.SerializeToElement(Secret),
        };
        var raised = await gate.CheckAsync(new ApprovalSubject("db.query", McpPermissionLevels.Admin,
                "Human:user:" + Admin, Admin, "web", args, OperationId: "op-1")
            {
                Binding = "binding-1", Requesters = [PrincipalIds.User(Admin)], WithholdPayloads = true,
                TargetServer = "local", TargetWorkload = "whiskers",
            }, "'whiskers' is a protected resource.", ["protected-resource"]);
        return raised.ApprovalId!;
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, Task<InlineApprovalResult> Asked)> OpenAsync(
        string approvalId, IReadOnlyDictionary<string, JsonElement>? payloads = null)
    {
        var provider = Render<MudDialogProvider>();
        var prompt = new MudInlineApprovalPrompt(Services.GetRequiredService<IDialogService>(), _store, _circuit, time: _time);
        Task<InlineApprovalResult> asked = null!;
        await provider.InvokeAsync(() =>
        {
            asked = prompt.AskAsync(new InlineApprovalRequest(approvalId, "op-1", "db.query", payloads), CancellationToken.None);
        });
        provider.WaitForElement("[data-testid=inline-approval-approve]");
        return (provider, asked);
    }

    private async Task TickAsync(IRenderedComponent<MudDialogProvider> provider, TimeSpan advance)
    {
        _time.Now += advance;
        await provider.InvokeAsync(_time.FireTimers);
    }

    private static bool Disabled(IRenderedComponent<MudDialogProvider> provider, string id) =>
        provider.Find($"[data-testid={id}]").HasAttribute("disabled");

    [Fact]
    public async Task No_button_is_default_and_Enter_approves_nothing()
    {
        var id = await RaiseAsync();
        var (provider, asked) = await OpenAsync(id);
        await TickAsync(provider, TimeSpan.FromSeconds(2));

        var buttons = provider.FindAll(".mud-dialog button");
        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.Equal("button", b.GetAttribute("type")));
        Assert.All(buttons, b => Assert.False(b.HasAttribute("autofocus")));
        Assert.Empty(provider.FindAll(".mud-dialog form"));
        // Enter on the dialog: no element handles it as an approval.
        foreach (var element in provider.FindAll(".mud-dialog, .mud-dialog-content"))
            try { await element.KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" }); }
            catch (Bunit.MissingEventHandlerException) { /* nothing listens to it */ }

        Assert.Equal(ApprovalStatus.Pending, _store.Get(id)!.Status);
        Assert.False(asked.IsCompleted);
    }

    [Fact]
    public async Task Approve_is_armed_only_after_the_delay()
    {
        var id = await RaiseAsync();
        var (provider, asked) = await OpenAsync(id);

        Assert.True(Disabled(provider, "inline-approval-approve"));
        await TickAsync(provider, TimeSpan.FromSeconds(1));
        Assert.True(Disabled(provider, "inline-approval-approve"));
        Assert.Equal(ApprovalStatus.Pending, _store.Get(id)!.Status);

        await TickAsync(provider, TimeSpan.FromSeconds(0.6));
        Assert.False(Disabled(provider, "inline-approval-approve"));
        Assert.Contains(provider.FindAll("[data-testid=inline-approval-self]"), _ => true);
        await provider.Find("[data-testid=inline-approval-approve]").ClickAsync(new());

        var result = await asked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(InlineApprovalOutcome.ApprovedHere, result.Outcome);
        var approval = _store.Get(id)!;
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Equal(ApprovalChannels.Dialog, approval.DecidedVia);
        Assert.True(approval.SelfApproved);
    }

    [Fact]
    public async Task The_summary_comes_from_the_store_with_payloads_withheld()
    {
        var id = await RaiseAsync();
        var (provider, _) = await OpenAsync(id);

        var stored = _store.Get(id)!;
        Assert.Equal("db.query", provider.Find("[data-testid=inline-approval-tool]").TextContent);
        Assert.Contains("local / whiskers", provider.Find("[data-testid=inline-approval-target]").TextContent);
        Assert.Equal(stored.ParamsJson, provider.Find("[data-testid=inline-approval-params]").TextContent);
        Assert.DoesNotContain("owner_secret_7f3a", provider.Markup);
        Assert.Empty(provider.FindAll("[data-testid=inline-approval-your-input]"));
    }

    [Fact]
    public async Task The_requesters_own_query_is_shown_in_the_dialog_and_never_persisted_or_logged()
    {
        var id = await RaiseAsync();
        var payloads = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement(Secret) };
        var (provider, asked) = await OpenAsync(id, payloads);

        Assert.Contains(Secret, provider.Find("[data-testid=inline-approval-your-input]").TextContent);
        Assert.DoesNotContain("owner_secret_7f3a", provider.Find("[data-testid=inline-approval-params]").TextContent);
        await TickAsync(provider, TimeSpan.FromSeconds(2));
        await provider.Find("[data-testid=inline-approval-approve]").ClickAsync(new());
        await asked.WaitAsync(TimeSpan.FromSeconds(5));

        var row = (await _store.GetHistoryAsync(0, 10)).Single(a => a.Id == id);
        Assert.DoesNotContain("owner_secret_7f3a", JsonSerializer.Serialize(row));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("owner_secret_7f3a", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Another_admins_decision_updates_the_dialog(bool approve)
    {
        _policy.RequireSecondApprover = true;
        var id = await RaiseAsync();
        var (provider, asked) = await OpenAsync(id);
        await TickAsync(provider, TimeSpan.FromSeconds(2));

        // Four eyes: the requester cannot approve; the reason is shown.
        Assert.True(Disabled(provider, "inline-approval-approve"));
        Assert.Contains(ApprovalDecisionMessages.Key(ApprovalDecisionResult.RequesterCannotDecide),
            provider.Find("[data-testid=inline-approval-refusal]").TextContent);

        Assert.Equal(ApprovalDecisionResult.Decided, await _store.DecideAsync(id, approve,
            ApprovalResolver.User("second@example.com", AppRole.Admin), ApprovalChannel.Page));
        if (approve)
        {
            provider.WaitForElement("[data-testid=inline-approval-run-now]");
            Assert.Contains("InlineApproval_ApprovedBy", provider.Find("[data-testid=inline-approval-approved-elsewhere]").TextContent);
            Assert.Equal("second@example.com", _store.Get(id)!.ResolvedBy);
            Assert.False(asked.IsCompleted);   // nothing runs before "Run now" is clicked
            await provider.Find("[data-testid=inline-approval-run-now]").ClickAsync(new());
            Assert.Equal(InlineApprovalOutcome.ApprovedElsewhereRun, (await asked.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        }
        else
        {
            provider.WaitForElement("[data-testid=inline-approval-rejected]");
            Assert.Contains("InlineApproval_RejectedBy", provider.Find("[data-testid=inline-approval-rejected]").TextContent);
            Assert.Equal("second@example.com", _store.Get(id)!.ResolvedBy);
            await provider.Find("[data-testid=inline-approval-close]").ClickAsync(new());
            Assert.Equal(InlineApprovalOutcome.Rejected, (await asked.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        }
    }

    [Fact]
    public async Task The_approval_hands_back_the_circuit_token_and_a_drop_cancels_it()
    {
        var id = await RaiseAsync();
        var (provider, asked) = await OpenAsync(id);
        await TickAsync(provider, TimeSpan.FromSeconds(2));
        await provider.Find("[data-testid=inline-approval-approve]").ClickAsync(new());
        var result = await asked.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(InlineApprovalOutcome.ApprovedHere, result.Outcome);
        Assert.True(result.Circuit.CanBeCanceled);
        Assert.False(result.Circuit.IsCancellationRequested);
        await _circuit.OnConnectionDownAsync(null!, default);
        Assert.True(result.Circuit.IsCancellationRequested);   // the runner then runs nothing
    }

    [Fact]
    public async Task Expiry_closes_as_expired()
    {
        var id = await RaiseAsync();
        var (provider, asked) = await OpenAsync(id);

        await TickAsync(provider, TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        Assert.Equal(InlineApprovalOutcome.Expired, (await asked.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Empty(provider.FindAll("[data-testid=inline-approval-approve]"));
        Assert.NotEqual(ApprovalStatus.Approved, _store.Get(id)!.Status);
    }

    [Fact]
    public async Task Closing_leaves_the_approval_pending_and_outside_a_circuit_nothing_is_shown()
    {
        var id = await RaiseAsync();
        var (provider, asked) = await OpenAsync(id);
        await provider.Find("[data-testid=inline-approval-close]").ClickAsync(new());
        Assert.Equal(InlineApprovalOutcome.LeftPending, (await asked.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Equal(ApprovalStatus.Pending, _store.Get(id)!.Status);

        // Without a live circuit the prompt shows nothing and leaves the approval pending (fail closed).
        var closed = new MudInlineApprovalPrompt(Services.GetRequiredService<IDialogService>(), _store,
            new InlineApprovalCircuit(), time: _time);
        var result = await closed.AskAsync(new InlineApprovalRequest(id, "op-1", "db.query"), CancellationToken.None);
        Assert.Equal(InlineApprovalOutcome.LeftPending, result.Outcome);
    }

    /// <summary>A clock whose timers fire only when the test says so.</summary>
    private sealed class Clock : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _timers = [];
        public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Task.Delay's one-shot timers (the prompt's expiry bound) still run on the real clock's scale here.
            if (period == Timeout.InfiniteTimeSpan) return TimeProvider.System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
            lock (_timers) _timers.Add((callback, state));
            return new NoTimer();
        }

        public void FireTimers()
        {
            List<(TimerCallback, object?)> timers;
            lock (_timers) timers = [.. _timers];
            foreach (var (callback, state) in timers) callback(state);
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines) owner.Lines.Add(formatter(state, exception) + exception);
            }
        }
    }
}
