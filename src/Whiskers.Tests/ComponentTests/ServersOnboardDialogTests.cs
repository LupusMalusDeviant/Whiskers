using System.Reflection;
using Bunit;
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
using Whiskers.Services.Onboarding;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;
using Whiskers.Services.Workloads.Kubernetes;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// Contract 0018 UI steps 3 and 7: the onboarding plan shows the bound VM as the provider reports it now (name, created,
/// labels, IP); a VM name that differs from the record keeps Start disabled until the person ticks the confirmation; a
/// run that ends with a standing bootstrap key says so and offers "Remove bootstrap access".
/// </summary>
public class ServersOnboardDialogTests : BunitContext, IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-0018-onboard-" + Guid.NewGuid().ToString("N"));
    private readonly ServerConfigService _servers;
    private readonly ScriptedProxy<IOnboardingService> _onboarding;
    private CloudBinding _vm = new(CloudProvider.Hetzner, 20, "web-new", "2026-01-02T03:04:05+00:00",
        new Dictionary<string, string> { ["env"] = "test" }, "running") { PublicIp = "203.0.113.20", Type = "cx23" };

    public ServersOnboardDialogTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        _servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, storePath: Path.Combine(_dir, "servers.json"), dataPaths: paths,
            sealedKeys: new SealedKeyFiles(TestVault.Usable, paths));
        _servers.InitializeAsync().GetAwaiter().GetResult();
        var (onboarding, script) = ScriptedProxy<IOnboardingService>.Create();
        _onboarding = script;
        script.On(nameof(IOnboardingService.OnboardServerAsync), _ => OnboardingResult.Ok([OnboardingStep.HostKey]));
        var (reader, readerScript) = ScriptedProxy<ICloudVmReader>.Create();
        readerScript.On(nameof(ICloudVmReader.BoundVmAsync), _ => _vm);

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IServerConfigService>(_servers);
        Services.AddSingleton(NullProxy<IVaultService>.Create());
        Services.AddSingleton<ICurrentUserService>(new Admin());
        Services.AddSingleton<IUiMutationRunner>(new PassThrough());
        Services.AddSingleton(NullProxy<Whiskers.Services.Docker.IDockerConnectionManager>.Create());
        Services.AddSingleton(onboarding);
        Services.AddSingleton(reader);
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

    private async Task<ServerConfig> FreshHetznerAsync(string name = "web-new")
    {
        await _servers.AddServerAsync(new ServerConfig
        {
            Id = "web-new", Name = name, ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.20", SshUser = "root", SshPort = 22,
            CloudProvider = CloudProvider.Hetzner, CloudServerId = 20, CloudKeyRef = "cloudkey:other:ref",
        });
        return _servers.GetServer("web-new")!;
    }

    private (IRenderedComponent<Servers> Page, IRenderedComponent<MudDialogProvider> Dialog) Open()
    {
        Render<MudPopoverProvider>();
        var dialog = Render<MudDialogProvider>();
        return (Render<Servers>(), dialog);
    }

    private static Task InvokeAsync(IRenderedComponent<Servers> page, string method, params object[] args) =>
        page.InvokeAsync(() => (Task)typeof(Servers).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page.Instance, args)!);

    private static AngleSharp.Dom.IElement Start(IRenderedComponent<MudDialogProvider> dialog) => dialog.Find("#plan-start");

    [Fact]
    public async Task Onboard_dialog_shows_vm_name_created_labels_and_ip()
    {
        var server = await FreshHetznerAsync();
        var (page, dialog) = Open();

        await InvokeAsync(page, "StartOnboarding", server, false);

        dialog.WaitForAssertion(() =>
        {
            var vm = dialog.Find("#plan-vm").TextContent;
            Assert.Contains("web-new", vm);
            Assert.Contains("id 20", vm);
            Assert.Contains("2026-01-02T03:04:05+00:00", vm);
            Assert.Contains("env=test", vm);
            Assert.Contains("203.0.113.20", vm);
        }, TimeSpan.FromSeconds(10));
        Assert.False(Start(dialog).HasAttribute("disabled"));   // the names agree: nothing to confirm
        Assert.Empty(dialog.FindAll("#plan-name-differs"));
    }

    [Fact]
    public async Task Differing_vm_name_keeps_confirm_disabled_until_ticked()
    {
        _vm = _vm with { CloudServerName = "other-vm" };
        var server = await FreshHetznerAsync();
        var (page, dialog) = Open();

        await InvokeAsync(page, "StartOnboarding", server, false);

        dialog.WaitForAssertion(() => Assert.Contains("other-vm", dialog.Find("#plan-vm").TextContent), TimeSpan.FromSeconds(10));
        Assert.Single(dialog.FindAll("#plan-name-differs"));
        Assert.True(Start(dialog).HasAttribute("disabled"));

        var tick = dialog.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.UserAttributes.TryGetValue("id", out var id) && (string?)id == "plan-confirm-vm");
        await dialog.InvokeAsync(() => tick.Instance.ValueChanged.InvokeAsync(true));

        dialog.WaitForAssertion(() => Assert.False(Start(dialog).HasAttribute("disabled")), TimeSpan.FromSeconds(10));
        await Start(dialog).ClickAsync(new MouseEventArgs());
        // The confirmed name is what the run carries.
        page.WaitForAssertion(() => Assert.Contains(nameof(IOnboardingService.OnboardServerAsync), _onboarding.Calls), TimeSpan.FromSeconds(10));
        Assert.Equal("other-vm", Get<OnboardingOptions>(page, "_lastOnboardOptions").ConfirmVmName);
        Assert.Equal(BootstrapMethod.HetznerRescueKey, Get<OnboardingOptions>(page, "_lastOnboardOptions").Bootstrap);
    }

    [Fact]
    public async Task Standing_key_warning_offers_remove_bootstrap_access()
    {
        var server = await FreshHetznerAsync();
        var warning = BootstrapCleanup.StandingKeyWarning("SHA256:injected", server.Name);
        _onboarding.On(nameof(IOnboardingService.OnboardServerAsync), _ =>
            OnboardingResult.Fail([OnboardingStep.CloudBootstrap, OnboardingStep.HostKey], OnboardingStep.Bootstrap, "failed")
                with { StandingBootstrapKey = true, CleanupWarnings = [warning] });
        var (page, dialog) = Open();

        await InvokeAsync(page, "RunOnboarding", server, new OnboardingOptions(BootstrapMethod.HetznerRescueKey));

        dialog.WaitForAssertion(() => Assert.Contains(warning, dialog.Find("#onboard-standing-key").TextContent), TimeSpan.FromSeconds(10));
        var remove = dialog.Find("#remove-bootstrap-access");
        await remove.ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => Assert.True(Get<OnboardingOptions>(page, "_lastOnboardOptions").AbandonBootstrap), TimeSpan.FromSeconds(10));
    }

    private static T Get<T>(IRenderedComponent<Servers> page, string field) =>
        (T)typeof(Servers).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page.Instance)!;

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
