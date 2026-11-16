using System.Reflection;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;
using Whiskers.Services.Workloads.Kubernetes;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// Contract 0018 part A (PR 1): the server form on the Servers page, driven through the rendered dialog — typed fields,
/// the type select, the key upload and the save buttons — against the real registry with sealed SSH keys.
/// A1 an uploaded key is what the saved record names (edit, and a second save after a failed probe); A2 an SSH server
/// without host or user cannot be saved; A3 switching the type fills an empty host and loses nothing; A4 a key upload
/// clears a held password, and the edit dialog shows a held password by its switch, never by its value.
/// </summary>
public class ServersFormTests : BunitContext, IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-0018-form-" + Guid.NewGuid().ToString("N"));
    private readonly ServerConfigService _servers;

    /// <summary>A transient bootstrap value as a failed onboarding leaves it in memory; made per run, never a literal.</summary>
    private static string HeldValue() => "held-" + Guid.NewGuid().ToString("N");

    public ServersFormTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        _servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, storePath: Path.Combine(_dir, "servers.json"), dataPaths: paths,
            sealedKeys: new SealedKeyFiles(TestVault.Usable, paths));
        _servers.InitializeAsync().GetAwaiter().GetResult();

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IServerConfigService>(_servers);
        Services.AddSingleton(NullProxy<IVaultService>.Create());
        Services.AddSingleton<ICurrentUserService>(new Admin());
        Services.AddSingleton<IUiMutationRunner>(new PassThrough());
        // The probe after a save fails: the connection manager hands out no client.
        Services.AddSingleton(NullProxy<Whiskers.Services.Docker.IDockerConnectionManager>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Onboarding.IOnboardingService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.AuditLog.IAuditLogService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Workloads.IWorkloadProviderFactory>.Create());
        Services.AddSingleton(NullProxy<IKubernetesClientCache>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Observability.ILoopSuspensionService>.Create());
        Services.AddSingleton(NullProxy<Whiskers.Services.Docker.Budget.IServerCircuitBreaker>.Create());
        Services.AddSingleton(EmptyClientCredentialStore());
        Services.AddSingleton(NullProxy<Whiskers.Services.Certificates.IMtlsCertificateService>.Create());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- A1 (F3) ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Edit_with_key_upload_keeps_the_new_file_name()
    {
        var server = await AddSshServerAsync(keyFile: "id_old");

        var (page, dialog) = OpenDialog();
        await page.InvokeAsync(() => Call(page, "EditServer", _servers.GetServer(server.Id)!));
        Upload(dialog, "id_new");
        await ClickAsync(dialog, "Servers_Button_Save");

        var saved = _servers.GetServer(server.Id)!;
        Assert.Equal("id_new", saved.SshKeyFileName);
        Assert.NotNull(_servers.GetSshKeyPath(saved));
    }

    [Fact]
    public async Task Second_save_after_failed_probe_keeps_the_key()
    {
        var (page, dialog) = OpenDialog();
        await page.InvokeAsync(() => Call(page, "ShowAddDialog"));
        await SelectTypeAsync(dialog, ConnectionType.SSH);
        Type(dialog, "Servers_Field_Name", "edge");
        Type(dialog, "Servers_Field_Host", "10.0.0.5");
        Type(dialog, "Servers_Field_SshUser", "root");
        Upload(dialog, "id_new");

        await ClickAsync(dialog, "Servers_Button_Save");   // saved; the probe fails, the dialog stays open
        var id = Assert.Single(_servers.GetServers()).Id;
        Assert.False(string.IsNullOrEmpty(Get<string>(page, "_connectError")));

        Type(dialog, "Servers_Field_Name", "edge (fixed)");
        await ClickAsync(dialog, "Servers_Button_Save");   // the retry is an update of the same record

        var saved = Assert.Single(_servers.GetServers());
        Assert.Equal(id, saved.Id);
        Assert.Equal("edge (fixed)", saved.Name);
        Assert.Equal("id_new", saved.SshKeyFileName);
        Assert.NotNull(_servers.GetSshKeyPath(saved));
    }

    // ---- A2 (F2) ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ssh_without_host_or_user_cannot_be_saved()
    {
        var (page, dialog) = OpenDialog();
        await page.InvokeAsync(() => Call(page, "ShowAddDialog"));
        await SelectTypeAsync(dialog, ConnectionType.SSH);
        Type(dialog, "Servers_Field_Name", "edge");

        Assert.True(Disabled(dialog, "Servers_Button_Save"));
        Assert.True(Disabled(dialog, "Servers_Button_SaveAndOnboard"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PersistAsync(page));

        Type(dialog, "Servers_Field_Host", "10.0.0.5");   // a host, still no user
        Assert.True(Disabled(dialog, "Servers_Button_Save"));
        Assert.True(Disabled(dialog, "Servers_Button_SaveAndOnboard"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PersistAsync(page));
        Assert.Empty(_servers.GetServers());

        Type(dialog, "Servers_Field_SshUser", "root");
        Assert.False(Disabled(dialog, "Servers_Button_Save"));
        Assert.False(Disabled(dialog, "Servers_Button_SaveAndOnboard"));
    }

    // ---- A3 (F1) ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Switching_type_fills_empty_ssh_host_from_tcp_host()
    {
        var (page, dialog) = OpenDialog();
        await page.InvokeAsync(() => Call(page, "ShowAddDialog"));   // opens as TCP
        Type(dialog, "Servers_Field_Name", "edge");
        Type(dialog, "Servers_Field_Host", "10.0.0.5");

        await SelectTypeAsync(dialog, ConnectionType.SSH);

        Assert.Equal("10.0.0.5", FieldValue(dialog, "Servers_Field_Host"));
        Assert.Equal("10.0.0.5", Get<ServerConfig>(page, "_form").SshHost);
    }

    [Fact]
    public async Task Switching_type_back_and_forth_loses_nothing()
    {
        var (page, dialog) = OpenDialog();
        await page.InvokeAsync(() => Call(page, "ShowAddDialog"));
        Type(dialog, "Servers_Field_Name", "edge");
        Type(dialog, "Servers_Field_Host", "10.0.0.5");            // TCP host

        await SelectTypeAsync(dialog, ConnectionType.SSH);
        Type(dialog, "Servers_Field_Host", "10.0.0.6");            // the SSH host differs: overwrite the copied value
        Type(dialog, "Servers_Field_SshUser", "root");

        await SelectTypeAsync(dialog, ConnectionType.TCP);
        Assert.Equal("10.0.0.5", FieldValue(dialog, "Servers_Field_Host"));   // a filled field is never overwritten

        await SelectTypeAsync(dialog, ConnectionType.SSH);
        Assert.Equal("10.0.0.6", FieldValue(dialog, "Servers_Field_Host"));
        Assert.Equal("root", FieldValue(dialog, "Servers_Field_SshUser"));
        Assert.Equal("edge", FieldValue(dialog, "Servers_Field_Name"));

        await ClickAsync(dialog, "Servers_Button_Save");
        var saved = Assert.Single(_servers.GetServers());
        Assert.Equal(("10.0.0.6", "root", "10.0.0.5"), (saved.SshHost, saved.SshUser, saved.TcpHost));
    }

    // ---- A4 (F4, R2) ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Key_upload_clears_the_password()
    {
        var server = await AddSshServerAsync(keyFile: "id_old");
        await HoldAsync(server.Id, HeldValue());

        var (page, dialog) = OpenDialog();
        await page.InvokeAsync(() => Call(page, "EditServer", _servers.GetServer(server.Id)!));
        await SwitchPasswordAsync(dialog, false);   // the upload button is shown with the switch off
        Upload(dialog, "id_new");
        await ClickAsync(dialog, "Servers_Button_Save");

        var saved = _servers.GetServer(server.Id)!;
        Assert.Null(saved.SshPassword);
        Assert.Equal("id_new", saved.SshKeyFileName);

        // The store's own key write never leaves a held password beside the key either.
        await HoldAsync(server.Id, HeldValue());
        await _servers.SaveSshKeyAsync(server.Id, "id_third", Encoding.UTF8.GetBytes(SealedKeyFilesTests.NewPem()));
        Assert.Null(_servers.GetServer(server.Id)!.SshPassword);
        Assert.Equal("id_third", _servers.GetServer(server.Id)!.SshKeyFileName);
    }

    [Fact]
    public async Task Password_typed_after_a_failed_key_probe_is_kept()
    {
        // Review of 0b85caed: the upload stayed pending after it was stored, so the next save dropped a password typed
        // since and uploaded the old key again. A stored upload is done; a password turned on and typed wins.
        var counting = KeyUploadCounter.Over(_servers);
        Services.AddSingleton(counting.Service);
        var typed = HeldValue();

        var (page, dialog) = OpenDialog();
        await page.InvokeAsync(() => Call(page, "ShowAddDialog"));
        await SelectTypeAsync(dialog, ConnectionType.SSH);
        Type(dialog, "Servers_Field_Name", "edge");
        Type(dialog, "Servers_Field_Host", "10.0.0.5");
        Type(dialog, "Servers_Field_SshUser", "root");
        Upload(dialog, "id_new");
        await ClickAsync(dialog, "Servers_Button_Save");   // key stored; the probe fails, the dialog stays open
        Assert.Equal(1, counting.Uploads);

        await SwitchPasswordAsync(dialog, true);
        Type(dialog, "Servers_Field_SshPassword", typed);
        await ClickAsync(dialog, "Servers_Button_Save");

        var saved = Assert.Single(_servers.GetServers());
        Assert.Equal(typed, saved.SshPassword);           // held for the bootstrap, which uses it before the key
        Assert.Equal("id_new", saved.SshKeyFileName);     // the stored key stays named (A1)
        Assert.Equal(1, counting.Uploads);                // not uploaded again
    }

    [Fact]
    public async Task Edit_dialog_shows_password_switch_on_when_record_has_one()
    {
        var held = HeldValue();
        var server = await AddSshServerAsync(keyFile: "id_old");
        await HoldAsync(server.Id, held);

        var (page, dialog) = OpenDialog();
        await page.InvokeAsync(() => Call(page, "EditServer", _servers.GetServer(server.Id)!));

        dialog.WaitForAssertion(() => Assert.True(Get<bool>(page, "_useSshPassword")));
        Assert.Equal("", FieldValue(dialog, "Servers_Field_SshPassword"));   // never prefilled
        Assert.DoesNotContain(held, dialog.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(held[5..13], dialog.Markup, StringComparison.Ordinal);   // nor a part of it

        // A record without a held value opens with the switch off.
        var plain = await AddSshServerAsync(keyFile: "id_plain", id: "plain");
        await page.InvokeAsync(() => Call(page, "EditServer", _servers.GetServer(plain.Id)!));
        dialog.WaitForAssertion(() => Assert.False(Get<bool>(page, "_useSshPassword")));
        dialog.WaitForAssertion(() => Assert.DoesNotContain(dialog.FindComponents<MudTextField<string>>(),
            f => f.Instance.Label == "Servers_Field_SshPassword"));
    }

    [Fact]
    public async Task Saving_with_the_switch_on_and_a_blank_field_keeps_the_held_password()
    {
        var held = HeldValue();
        var server = await AddSshServerAsync(keyFile: "id_old");
        await HoldAsync(server.Id, held);

        var (page, dialog) = OpenDialog();
        await page.InvokeAsync(() => Call(page, "EditServer", _servers.GetServer(server.Id)!));
        Type(dialog, "Servers_Field_Name", "edge (renamed)");
        await ClickAsync(dialog, "Servers_Button_Save");

        var saved = _servers.GetServer(server.Id)!;
        Assert.Equal("edge (renamed)", saved.Name);
        Assert.Equal(held, saved.SshPassword);

        // Switched off: the held value goes, the key stays.
        await page.InvokeAsync(() => Call(page, "EditServer", _servers.GetServer(server.Id)!));
        await SwitchPasswordAsync(dialog, false);
        await ClickAsync(dialog, "Servers_Button_Save");
        saved = _servers.GetServer(server.Id)!;
        Assert.Null(saved.SshPassword);
        Assert.Equal("id_old", saved.SshKeyFileName);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<ServerConfig> AddSshServerAsync(string keyFile, string id = "edge")
    {
        var server = new ServerConfig
        {
            Id = id, Name = id, ConnectionType = ConnectionType.SSH, SshHost = "10.0.0.5", SshUser = "root", SshPort = 22,
        };
        await _servers.AddServerAsync(server);
        await _servers.SaveSshKeyAsync(id, keyFile, Encoding.UTF8.GetBytes(SealedKeyFilesTests.NewPem()));
        return _servers.GetServer(id)!;
    }

    /// <summary>What a failed onboarding leaves: the transient value in the store's in-memory record.</summary>
    private async Task HoldAsync(string id, string value)
    {
        var record = _servers.GetServer(id)!.Clone();
        record.SshPassword = value;
        await _servers.UpdateServerAsync(record);
    }

    private (IRenderedComponent<Servers> Page, IRenderedComponent<MudDialogProvider> Dialog) OpenDialog()
    {
        Render<MudPopoverProvider>();
        var dialog = Render<MudDialogProvider>();
        var page = Render<Servers>();
        return (page, dialog);
    }

    /// <summary>Runs a page handler (open the add or edit dialog) and renders, as a click on the page would.</summary>
    private static void Call(IRenderedComponent<Servers> page, string method, params object[] args)
    {
        typeof(Servers).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, args);
        typeof(Microsoft.AspNetCore.Components.ComponentBase)
            .GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, null);
    }

    private static T Get<T>(IRenderedComponent<Servers> page, string field) =>
        (T)typeof(Servers).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page.Instance)!;

    private static Task PersistAsync(IRenderedComponent<Servers> page)
    {
        var method = typeof(Servers).GetMethod("PersistServerCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var arguments = typeof(Servers).GetMethod("SaveArguments", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return page.InvokeAsync(() => (Task)method.Invoke(page.Instance,
            [TestTargets.Of<ResolvedTarget>("server.save", arguments.Invoke(page.Instance, null)!)])!);
    }

    private static IRenderedComponent<MudTextField<string>> Field(IRenderedComponent<MudDialogProvider> dialog, string label)
    {
        IRenderedComponent<MudTextField<string>>? found = null;
        dialog.WaitForAssertion(() =>
        {
            found = dialog.FindComponents<MudTextField<string>>().SingleOrDefault(f => f.Instance.Label == label);
            Assert.NotNull(found);
        }, TimeSpan.FromSeconds(10));
        return found!;
    }

    private static string FieldValue(IRenderedComponent<MudDialogProvider> dialog, string label) =>
        Field(dialog, label).Find("input").GetAttribute("value") ?? "";

    /// <summary>Types into a field as the browser does (a change event). Found again right before the event: a re-render
    /// in between leaves a stale handler id, which bUnit rejects; such an attempt never reached the field.</summary>
    private static void Type(IRenderedComponent<MudDialogProvider> dialog, string label, string value)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Field(dialog, label).Find("input").Change(value); return; }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 5) { Thread.Sleep(50); }
        }
    }

    private static Task SelectTypeAsync(IRenderedComponent<MudDialogProvider> dialog, ConnectionType type)
    {
        var select = dialog.FindComponent<MudSelect<ConnectionType>>();
        return dialog.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(type));
    }

    private static Task SwitchPasswordAsync(IRenderedComponent<MudDialogProvider> dialog, bool on)
    {
        var toggle = dialog.FindComponent<MudSwitch<bool>>();
        return dialog.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(on));
    }

    private static void Upload(IRenderedComponent<MudDialogProvider> dialog, string fileName) =>
        dialog.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(SealedKeyFilesTests.NewPem(), fileName));

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<MudDialogProvider> dialog, string text) =>
        dialog.FindAll("button").Single(b => b.TextContent.Trim() == text);

    private static bool Disabled(IRenderedComponent<MudDialogProvider> dialog, string text) =>
        Button(dialog, text).HasAttribute("disabled");

    private static async Task ClickAsync(IRenderedComponent<MudDialogProvider> dialog, string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await Button(dialog, text).ClickAsync(new MouseEventArgs()); break; }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 5) { await Task.Delay(50); }
        }
        // The save runs to its end (store write and the failing probe) before the test reads the store.
        dialog.WaitForAssertion(() => Assert.DoesNotContain(dialog.FindAll("button"),
            b => b.TextContent.Contains("Servers_Button_Connecting", StringComparison.Ordinal)), TimeSpan.FromSeconds(20));
    }

    /// <summary>Forwards everything to the real registry and counts the SSH key uploads that reach it.</summary>
    public class KeyUploadCounter : DispatchProxy
    {
        private IServerConfigService _inner = null!;
        private int _uploads;
        public int Uploads => Volatile.Read(ref _uploads);
        public IServerConfigService Service => (IServerConfigService)(object)this;

        public static KeyUploadCounter Over(IServerConfigService inner)
        {
            var counter = (KeyUploadCounter)(object)Create<IServerConfigService, KeyUploadCounter>();
            counter._inner = inner;
            return counter;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IServerConfigService.SaveSshKeyAsync)) Interlocked.Increment(ref _uploads);
            try { return method.Invoke(_inner, args); }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    private sealed class Admin : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@x");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Admin);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(true);
    }

    private sealed class PassThrough : IUiMutationRunner
    {
        public Task<T> RunAsync<TTarget, T>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            action(TestTargets.Of<TTarget>(capabilityId, arguments), ct);

        public Task RunAsync<TTarget>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            action(TestTargets.Of<TTarget>(capabilityId, arguments), ct);

        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default) => action(ct);

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default) => action(ct);
    }
}
