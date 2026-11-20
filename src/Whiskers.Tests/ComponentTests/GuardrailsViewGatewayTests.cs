using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-11, on the real page handlers: the Guardrails page saves and switches the kill switch off through the
/// real Mutation Gateway against a real <see cref="GuardrailStore"/>. The test host has no resource assembly, so
/// texts render as their keys.</summary>
public class GuardrailsViewGatewayTests : BunitContext, IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-r111-ui-" + Guid.NewGuid().ToString("N"));
    private readonly GuardrailStore _store;
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly MutableUser _user = new(AppRole.Admin);

    public GuardrailsViewGatewayTests()
    {
        Directory.CreateDirectory(_dir);
        _store = GuardrailStoreTestExtensions.FirstStartStore(Path.Combine(_dir, "guardrails.json"), new TestServices(_auditLog));
        _store.StartAsync().GetAwaiter().GetResult();

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(new NoopKeyInterceptor());
        Services.AddSingleton<IGuardrailStore>(_store);
        Services.AddSingleton<IAgentToolRegistry>(new NoTools());
        Services.AddSingleton<ICurrentUserService>(_user);
        // Contract 0017: the trusted-script editor lists servers, reads a hash and audits a change of the list.
        Services.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(ProtectionHarness.Local));
        Services.AddSingleton<Whiskers.Services.Server.IHostCommandExecutor>(_host);
        Services.AddSingleton<Whiskers.Services.AuditLog.IAuditLogService>(_auditLog);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(), approvals: new ApprovalGate(new ApprovalStore(), _store));
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), _user));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // MudSwitch pulls in a service that only disposes asynchronously; dispose the container that way first.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Save_goes_through_the_gateway_and_is_audited_before_the_store_changes()
    {
        var cut = Render<GuardrailsView>();
        var before = _store.Current.MaxActionsPerSession;

        Save(cut);

        cut.WaitForAssertion(() => Assert.Contains("complete:Executed", Events()));
        Assert.Contains("begin:guardrails.save:", Events());
        Assert.Equal(before, _store.Current.MaxActionsPerSession);
    }

    [Fact]
    public void A_user_demoted_while_the_page_is_open_cannot_save()
    {
        var cut = Render<GuardrailsView>();
        _user.Role = AppRole.Operator;   // the Save button is still rendered
        cut.Find("input[type=checkbox]").Change(true);   // kill switch on in the edit buffer

        Save(cut);

        cut.WaitForAssertion(() => Assert.Contains("denied:Denied", Events()));
        Assert.False(_store.Current.ReadOnlyMode);
    }

    [Fact]
    public void When_the_audit_cannot_be_written_nothing_is_saved()
    {
        var cut = Render<GuardrailsView>();
        cut.Find("input[type=checkbox]").Change(true);
        _audit.FailNextBegin = true;

        Save(cut);

        cut.WaitForAssertion(() => Assert.False(_audit.FailNextBegin));   // the intent write was attempted and failed
        Assert.False(_store.Current.ReadOnlyMode);
        Assert.DoesNotContain(Events(), e => e.StartsWith("begin:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_the_kill_switch_on_saving_is_refused_and_only_switching_it_off_works()
    {
        var config = _store.Config;
        var active = config.Presets.First(p => p.Name == config.ActivePreset);
        active.Policy.ReadOnlyMode = true;
        active.Policy.MaxActionsPerSession = 3;
        await _store.SaveConfigAsync(config, Admin);
        var cut = Render<GuardrailsView>();

        Save(cut);
        cut.WaitForAssertion(() => Assert.Contains("denied:Denied", Events()));
        Assert.True(_store.Current.ReadOnlyMode);

        cut.FindAll("button").First(b => b.TextContent.Contains("GuardrailsView_KillSwitch_Disable_Button")).Click();

        cut.WaitForAssertion(() => Assert.False(_store.Current.ReadOnlyMode));
        Assert.Equal(3, _store.Current.MaxActionsPerSession);
        Assert.Contains("begin:guardrails.kill_switch_off:", Events());
    }

    [Fact]
    public async Task An_operator_cannot_switch_the_kill_switch_off()
    {
        var config = _store.Config;
        config.Presets.First(p => p.Name == config.ActivePreset).Policy.ReadOnlyMode = true;
        await _store.SaveConfigAsync(config, Admin);
        var cut = Render<GuardrailsView>();
        _user.Role = AppRole.Operator;

        cut.FindAll("button").First(b => b.TextContent.Contains("GuardrailsView_KillSwitch_Disable_Button")).Click();

        cut.WaitForAssertion(() => Assert.Contains("denied:Denied", Events()));
        Assert.True(_store.Current.ReadOnlyMode);
    }

    [Fact]
    public void The_page_lists_every_kill_switch_exemption_and_saves_a_block_through_the_gateway()
    {
        var cut = Render<GuardrailsView>();
        var section = cut.Find("#killswitch-exemptions");
        // Contract 0009 (D6): E1–E3 shown as fixed, E4–E6 each with a toggle.
        foreach (var id in new[] { "E1", "E2", "E3" }.Concat(KillSwitchExemptions.Blockable))
        {
            Assert.Contains($"GuardrailsView_Exemption_{id}_Name", section.TextContent);
            Assert.Contains($"GuardrailsView_Exemption_{id}_Reason", section.TextContent);
        }
        Assert.Equal(3, cut.FindAll("#killswitch-exemptions input[type=checkbox]").Count);

        cut.FindAll("#killswitch-exemptions input[type=checkbox]")[1].Change(true);   // recovery_code
        Assert.Null(_store.Current.KillSwitchBlockedExemptions);                    // the edit buffer only
        Save(cut);

        cut.WaitForAssertion(() => Assert.Equal([KillSwitchExemptions.RecoveryCode], _store.Current.KillSwitchBlockedExemptions));
        Assert.Contains("begin:guardrails.save:", Events());
    }

    // ---- Contract 0017: trusted scripts ----

    private const string Script = "/usr/local/sbin/burgcloud-db-dump.sh";
    private const string ScriptHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void A_trusted_script_is_added_with_the_hash_read_on_the_server_and_its_save_is_audited()
    {
        var cut = Render<GuardrailsView>();
        cut.Find("#trusted-scripts input[placeholder='/usr/local/sbin/backup.sh']").Change(Script);
        cut.FindAll("#trusted-scripts button").Single(b => b.TextContent.Contains("GuardrailsView_TrustedScripts_Add")).Click();

        cut.WaitForAssertion(() => Assert.Contains(ScriptHash[..12], cut.Find("#trusted-scripts").TextContent));
        Assert.Null(_store.Current.TrustedScripts);                       // the edit buffer only
        Assert.Equal([$"local: {TrustedScripts.HashCommand(Script)}"], _host.Commands);
        Save(cut);

        cut.WaitForAssertion(() => Assert.NotNull(_store.Current.TrustedScripts));
        var entry = Assert.Single(_store.Current.TrustedScripts!);
        Assert.Equal(("local", Script, ScriptHash), (entry.ServerId, entry.Path, entry.Sha256));
        cut.WaitForAssertion(() => Assert.Contains(_auditLog.Entries, e => e.Action == "guardrails.trusted_scripts"
            && e.Details!.Contains($"added Standard | local | {Script} | {ScriptHash}", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_interpreter_cannot_be_added_and_nothing_is_read()
    {
        var cut = Render<GuardrailsView>();
        cut.Find("#trusted-scripts input[placeholder='/usr/local/sbin/backup.sh']").Change("/usr/bin/python3");
        cut.FindAll("#trusted-scripts button").Single(b => b.TextContent.Contains("GuardrailsView_TrustedScripts_Add")).Click();

        Assert.Empty(_host.Commands);
        Assert.Empty(cut.FindAll("#trusted-scripts table"));   // nothing listed
        Assert.Contains("GuardrailsView_TrustedScripts_None", cut.Find("#trusted-scripts").TextContent);
    }

    [Fact]
    public void A_script_whose_hash_cannot_be_read_is_not_added()
    {
        // A symlink, an ELF program, a file without #!, a hard link or a writable file: the fixed read prints nothing.
        _host.Fails = true;
        var cut = Render<GuardrailsView>();
        cut.Find("#trusted-scripts input[placeholder='/usr/local/sbin/backup.sh']").Change(Script);
        cut.FindAll("#trusted-scripts button").Single(b => b.TextContent.Contains("GuardrailsView_TrustedScripts_Add")).Click();

        cut.WaitForAssertion(() => Assert.Single(_host.Commands));
        Assert.Empty(cut.FindAll("#trusted-scripts table"));
        Assert.Contains("GuardrailsView_TrustedScripts_None", cut.Find("#trusted-scripts").TextContent);
        Save(cut);
        cut.WaitForAssertion(() => Assert.Contains("complete:Executed", Events()));
        Assert.Null(_store.Current.TrustedScripts);
    }

    [Fact]
    public async Task A_page_loaded_before_an_entry_was_removed_cannot_save_it_back()
    {
        var admin = new Whiskers.Models.Agent.AgentPrincipal(Whiskers.Models.Agent.AgentPrincipalKind.WebUser,
            "other@example.com", Whiskers.Models.McpPermissionLevels.Admin, null, UserEmail: "other@example.com");
        var listed = _store.Config;
        listed.Presets[0].Policy.TrustedScripts = [new TrustedScript { ServerId = "local", Path = Script, Sha256 = ScriptHash }];
        await _store.SaveConfigAsync(listed, admin, TrustedScripts.Additions(_store.Config, listed));
        var cut = Render<GuardrailsView>();                      // loaded with the entry

        var removed = _store.Config;                              // another Admin revokes it meanwhile
        removed.Presets[0].Policy.TrustedScripts = null;
        await _store.SaveConfigAsync(removed, admin, "");
        Save(cut);                                                // the stale page saves

        cut.WaitForAssertion(() => Assert.Contains(Events(), e => e.StartsWith("complete:", StringComparison.Ordinal)));
        Assert.Null(_store.Current.TrustedScripts);
    }

    private readonly HashHost _host = new();
    private readonly RecordingAudit _auditLog = new();

    /// <summary>Answers the fixed hash command for <see cref="Script"/>.</summary>
    private sealed class HashHost : Whiskers.Services.Server.IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];
        public bool Fails { get; set; }

        public Task<Whiskers.Services.Server.CommandResult> ExecuteAsync(string serverId, string command,
            TimeSpan? timeout = null, CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add($"{serverId}: {command}");
            return Task.FromResult(Fails
                ? new Whiskers.Services.Server.CommandResult { ExitCode = 1 }
                : new Whiskers.Services.Server.CommandResult { ExitCode = 0, Output = $"{ScriptHash}  {Script}\n" });
        }
    }

    private sealed class RecordingAudit : Whiskers.Services.AuditLog.IAuditLogService
    {
        public List<(string Action, string? Details)> Entries { get; } = [];

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            lock (Entries) Entries.Add((action, details));
            return Task.CompletedTask;
        }

        public async Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType,
            string targetId, string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            await LogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);
            return true;
        }

        public Task<List<Whiskers.Models.AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0,
            string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(new List<Whiskers.Models.AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    private List<string> Events() { lock (_audit.Events) return _audit.Events.ToList(); }

    private static void Save(IRenderedComponent<GuardrailsView> cut) =>
        cut.FindAll("button").Last(b => b.TextContent.Contains("GuardrailsView_SaveAll_Button")).Click();

    private static readonly AgentPrincipal Admin =
        new(AgentPrincipalKind.WebUser, "admin@x", McpPermissionLevels.Admin, null, UserEmail: "admin@x");

    private sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@x");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }

    private sealed class NoopKeyInterceptor : IKeyInterceptorService, IDisposable
    {
        public Task SubscribeAsync(IKeyInterceptorObserver observer, MudBlazor.Services.KeyInterceptorOptions options) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, IKeyDownObserver? keyDown = null, IKeyUpObserver? keyUp = null) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, Action<KeyboardEventArgs>? keyDown = null, Action<KeyboardEventArgs>? keyUp = null) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, Func<KeyboardEventArgs, Task>? keyDown = null, Func<KeyboardEventArgs, Task>? keyUp = null) => Task.CompletedTask;
        public Task UnsubscribeAsync(IKeyInterceptorObserver observer) => Task.CompletedTask;
        public Task UnsubscribeAsync(string elementId) => Task.CompletedTask;
        public Task UpdateKeyAsync(IKeyInterceptorObserver observer, MudBlazor.Services.KeyOptions option) => Task.CompletedTask;
        public Task UpdateKeyAsync(string elementId, MudBlazor.Services.KeyOptions option) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, MudBlazor.Services.KeyInterceptorOptions options, Action<MudBlazor.Services.KeyMapBuilder> keyMap) => Task.CompletedTask;
        public Task DispatchAsync(string elementId, MudBlazor.Services.KeyEventKind kind, KeyboardEventArgs args) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoTools : IAgentToolRegistry
    {
        public IReadOnlyDictionary<string, AgentToolEntry> Tools { get; } = new Dictionary<string, AgentToolEntry>();
    }
}
