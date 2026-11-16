using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Docker;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.Metrics;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// Review of 1.0.0 (U1): click handlers and renders that let an exception escape end the Blazor circuit (no
/// ErrorBoundary). Each test failed on the released code; the page must report the error and stay usable.
/// </summary>
public sealed class UiCrashRegressionTests : BunitContext, IAsyncLifetime
{
    private readonly ServersPauseResumeRoleTests.MutableUser _user = new(AppRole.Admin);

    public UiCrashRegressionTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddOptions();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(Null<IKeyInterceptorService>());
        Services.AddSingleton<ICurrentUserService>(_user);
        Services.AddSingleton<IUiMutationRunner>(new PassThrough());
        Services.AddSingleton(Stub<Whiskers.Modules.IModuleRegistry>().On("IsEnabled", _ => true).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private static T Null<T>() where T : class => new UiCreatedResourceAuditTests.Stub<T>().Object;
    private static UiCreatedResourceAuditTests.Stub<T> Stub<T>() where T : class => new();

    private static void Set(object instance, string field, object? value) =>
        instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static T Get<T>(object instance, string field) =>
        (T)instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static Task Invoke<TComponent>(IRenderedComponent<TComponent> page, string handler) where TComponent : IComponent
    {
        var method = typeof(TComponent).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!;
        return page.InvokeAsync(() => method.Invoke(page.Instance, null) as Task ?? Task.CompletedTask);
    }

    private static Task InvokeWith<TComponent>(IRenderedComponent<TComponent> page, string handler, params object?[] args)
        where TComponent : IComponent
    {
        var method = typeof(TComponent).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == handler && m.GetParameters().Length == args.Length);
        return page.InvokeAsync(() => method.Invoke(page.Instance, args) as Task ?? Task.CompletedTask);
    }

    private static string Describe(Exception? ex)
    {
        while (ex is TargetInvocationException or AggregateException && ex.InnerException is not null) ex = ex.InnerException;
        return ex is null ? "" : ex.GetType().Name + ": " + ex.Message;
    }

    private List<Snackbar> Snackbars() => Services.GetRequiredService<ISnackbar>().ShownSnackbars.ToList();

    private void TryAdd<T>(T instance) where T : class
    {
        if (Services.All(d => d.ServiceType != typeof(T))) Services.AddSingleton(instance);
    }

    // ------------------------------------------------------------------ servers

    private async Task<string> ServersServices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "whiskers-uicrash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, storePath: Path.Combine(dir, "servers.json"));
        await servers.InitializeAsync();
        Services.AddSingleton<IServerConfigService>(servers);
        Services.AddSingleton(Null<Whiskers.Services.Vault.IVaultService>());
        Services.AddSingleton(Null<IDockerConnectionManager>());
        Services.AddSingleton(Null<Whiskers.Services.Onboarding.IOnboardingService>());
        Services.AddSingleton(Null<Whiskers.Services.AuditLog.IAuditLogService>());
        Services.AddSingleton(Null<Whiskers.Services.Workloads.IWorkloadProviderFactory>());
        Services.AddSingleton(Null<Whiskers.Services.Workloads.Kubernetes.IKubernetesClientCache>());
        Services.AddSingleton(Null<Whiskers.Services.Observability.ILoopSuspensionService>());
        Services.AddSingleton(Null<Whiskers.Services.Docker.Budget.IServerCircuitBreaker>());
        Services.AddSingleton(ServersKubeconfigSaveTests.EmptyClientCredentialStore());
        Services.AddSingleton(Null<Whiskers.Services.Certificates.IMtlsCertificateService>());
        return dir;
    }

    [Fact]
    public async Task Save_and_onboard_reports_an_invalid_host_instead_of_throwing()
    {
        var dir = await ServersServices();
        // The Tailscale question is answered with "continue".
        Services.AddSingleton(Stub<IDialogService>().On(nameof(IDialogService.ShowMessageBoxAsync), _ => Task.FromResult<bool?>(true)).Object);
        var page = Render<Servers>();
        Set(page.Instance, "_form", new ServerConfig { Name = "edge", ConnectionType = ConnectionType.SSH, SshHost = "bad host;", SshUser = "root", SshPort = 22 });

        var ex = await Record.ExceptionAsync(() => Invoke(page, "SaveAndOnboard"));

        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        Assert.True(ex is null, "'Save & Onboard' with an invalid SSH host lets the validation error escape the click handler " +
            "(no ErrorBoundary: the circuit ends): " + Describe(ex));
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Error);
        Assert.False(Get<bool>(page.Instance, "_saving"));
    }

    [Fact]
    public async Task An_ssh_key_file_over_the_limit_is_refused_with_a_message()
    {
        var dir = await ServersServices();
        var page = Render<Servers>();
        // The dialog's own InputFile is only rendered inside the dialog; this one hands the same browser file to the
        // page's handler.
        Exception? escaped = null;
        var handled = false;
        var input = Render<InputFile>(p => p.Add(i => i.OnChange, EventCallback.Factory.Create<InputFileChangeEventArgs>(this, async e =>
        {
            try { await InvokeWith(page, "OnSshKeyFileChanged", e); }
            catch (Exception ex) { escaped = ex; }
            finally { handled = true; }
        })));

        input.UploadFiles(InputFileContent.CreateFromBinary(new byte[200 * 1024], "id_wrong_file"));
        input.WaitForState(() => handled, TimeSpan.FromSeconds(5));

        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        Assert.True(escaped is null, "Choosing a 200 KB file as SSH key (limit 100 KB) escapes the change handler: " + Describe(escaped));
        Assert.Null(Get<string?>(page.Instance, "_sshKeyFileName"));
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Error);
    }

    // ------------------------------------------------------------------ dashboard

    [Fact]
    public async Task The_dashboard_renders_an_expanded_server_whose_memory_history_cannot_be_read()
    {
        var info = new ServerSystemInfo { ServerId = "local", ServerName = "Local", IsReachable = true };
        Services.AddSingleton(Stub<IDockerService>()
            .On(nameof(IDockerService.ListAllContainersAsync), _ => Task.FromResult<IList<ContainerInfo>>(
                [new ContainerInfo { Id = "c1", Name = "web", State = "running", ServerId = "local", ServerName = "Local" }]))
            .On(nameof(IDockerService.GetAllServerSystemInfoAsync), _ => Task.FromResult(new Dictionary<string, ServerSystemInfo> { ["local"] = info })).Object);
        Services.AddSingleton(Stub<Whiskers.Services.Observability.IFleetSnapshotCache>().On("GetAll", _ =>
            (IReadOnlyDictionary<string, Whiskers.Services.Observability.CachedServerInfo>)new Dictionary<string, Whiskers.Services.Observability.CachedServerInfo>
            {
                ["local"] = new(info, DateTime.UtcNow, TimeSpan.Zero),
            }).Object);
        Services.AddSingleton(Stub<IMetricsQueryService>()
            .On(nameof(IMetricsQueryService.GetServerCpuHistoryAsync), _ => Task.FromResult(new List<MetricPoint> { new() { Timestamp = DateTime.UtcNow, Value = 12 } }))
            .On(nameof(IMetricsQueryService.GetServerMemoryHistoryAsync), _ => Task.FromException<List<MetricPoint>>(new TimeoutException("The database did not answer."))).Object);
        Services.AddSingleton(Null<Whiskers.Services.ImageUpdate.IImageUpdateStore>());
        Services.AddSingleton(Null<Whiskers.Services.AutoUpdate.IAutoUpdateService>());
        Services.AddSingleton(Stub<Whiskers.Services.Cve.ICveFindingsStore>().On("SummarizeServer",
            _ => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Whiskers.Services.Cve.ICveFindingsStore).GetMethod("SummarizeServer")!.ReturnType)).Object);
        Services.AddSingleton(Null<IEndpointCheckStore>());
        Services.AddSingleton(Null<IServerConfigService>());
        Services.AddSingleton(Null<Whiskers.Services.Workloads.IWorkloadProviderFactory>());
        Services.AddSingleton(Null<Whiskers.Services.Observability.ILoopSuspensionService>());
        Services.AddSingleton(Null<Whiskers.Services.Metrics.DiskHealth.IDiskHealthSettingsService>());
        Services.AddSingleton(Null<Whiskers.Services.Metrics.DiskHealth.IDiskHealthService>());
        var page = Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains("Local", page.Markup));

        await InvokeWith(page, "ToggleServerExpand", "local");   // the expand arrow: loads the CPU history, then the memory history fails
        await Task.Delay(200);
        var ex = Record.Exception(() => page.Render());            // what every later render (the 5 s refresh) does

        Assert.True(ex is null, "With a CPU history but no memory history for the expanded server, rendering the dashboard throws: " + Describe(ex));
    }

    // ------------------------------------------------------------------ webhooks / scheduled tasks

    [Fact]
    public async Task The_create_webhook_dialog_opens_when_the_container_list_fails()
    {
        Services.AddSingleton(Null<Whiskers.Services.Webhooks.IWebhookService>());
        Services.AddSingleton(Stub<IDockerService>().On(nameof(IDockerService.ListContainersAsync),
            _ => Task.FromException<IList<ContainerInfo>>(new HttpRequestException("host unreachable"))).Object);
        Services.AddSingleton(Null<Whiskers.Services.GitDeploy.IGitDeployService>());
        Services.AddSingleton(Null<IServerConfigService>());
        Services.AddSingleton(Null<IComposePlanner>());
        Services.AddSingleton(Null<IAutomationOwnerResolver>());
        var page = Render<WebhooksView>();

        var ex = await Record.ExceptionAsync(() => Invoke(page, "ShowCreateDialog"));

        Assert.True(ex is null, "'Create webhook' with an unreachable default server lets the listing error escape the click handler: " + Describe(ex));
        Assert.True(Get<bool>(page.Instance, "_showDialog"));
    }

    [Fact]
    public async Task The_create_task_dialog_opens_when_the_container_list_fails()
    {
        Services.AddSingleton(Null<Whiskers.Services.Scheduler.ISchedulerService>());
        Services.AddSingleton(Stub<IDockerService>().On(nameof(IDockerService.ListContainersAsync),
            _ => Task.FromException<IList<ContainerInfo>>(new HttpRequestException("host unreachable"))).Object);
        Services.AddSingleton(Null<IServerConfigService>());
        Services.AddSingleton(Null<IAutomationOwnerResolver>());
        Services.AddSingleton(Null<IEffectiveRequirementResolver>());
        var page = Render<ScheduledTasksView>();

        var ex = await Record.ExceptionAsync(() => Invoke(page, "ShowCreateDialog"));

        Assert.True(ex is null, "'Create task' with an unreachable default server lets the listing error escape the click handler: " + Describe(ex));
        Assert.True(Get<bool>(page.Instance, "_showDialog"));
    }

    // ------------------------------------------------------------------ handlers that caught only MutationRefusedException

    private IRenderedComponent<Settings> SettingsPage(Action<IServiceCollection>? more = null)
    {
        Services.AddSingleton(Null<Whiskers.Services.Cve.ICveMonitorService>());
        Services.AddSingleton(Null<Whiskers.Services.Cve.ICveFindingsStore>());
        Services.AddSingleton(Null<IWhitelistService>());
        Services.AddSingleton(Null<Whiskers.Services.Vault.IVaultService>());
        Services.AddSingleton(Null<Whiskers.Services.Mcp.IMcpPermissionService>());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        more?.Invoke(Services);
        TryAdd(Null<INotificationService>());
        TryAdd(Null<IMatrixNotificationService>());
        TryAdd(Null<IRoleService>());
        TryAdd(Null<Whiskers.Services.Persistence.IAppSettingsStore>());
        // Rendered for a Viewer (the access-denied panel), as SettingsHandlerRoleTests does; the handlers decide by the
        // role held at the click.
        _user.Role = AppRole.Viewer;
        var page = Render<Settings>();
        _user.Role = AppRole.Admin;
        return page;
    }

    [Fact]
    public async Task Settings_reports_a_metric_alert_save_that_cannot_write()
    {
        var page = SettingsPage(s => s.AddSingleton(Stub<Whiskers.Services.Persistence.IAppSettingsStore>()
            .On("SaveSectionAsync", _ => Task.FromException(new IOException("The disk is full."))).Object));

        var ex = await Record.ExceptionAsync(() => Invoke(page, "SaveMetricAlert"));

        Assert.True(ex is null, "Saving 'Metric alerts' when app-settings.json cannot be written escapes the click handler: " + Describe(ex));
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Error);
    }

    // ------------------------------------------------------------------ failures shown without secrets (UiFailure)

    // A store failure whose message carries a secret-looking value next to a path and an address.
    private const string LeakySecret = "Hunter2Secret";
    private static IOException LeakyFailure() =>
        new($"Cannot write /data/whitelist.json for admin@example.com: password={LeakySecret}");

    // The real texts (as the app registers localization), so the snackbar carries the message the page put in.
    private static IServiceCollection WithResources(IServiceCollection services) =>
        services.Configure<Microsoft.Extensions.Localization.LocalizationOptions>(o => o.ResourcesPath = "Resources");

    private void AssertShownRedacted(string handler)
    {
        var error = Assert.Single(Snackbars(), s => s.Severity == Severity.Error);
        var text = error.Message.ToString() ?? "";
        Assert.False(text.Contains(LeakySecret, StringComparison.Ordinal),
            $"{handler} shows the store's error message unredacted: {text}");
        Assert.Contains("password=***", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_shows_a_failed_mcp_permission_save_without_its_secret()
    {
        var page = SettingsPage(s => WithResources(s).AddSingleton(Stub<Whiskers.Services.Mcp.IMcpPermissionService>()
            .On("SavePermissionDataAsync", _ => Task.FromException(LeakyFailure())).Object));

        var ex = await Record.ExceptionAsync(() => Invoke(page, "SaveMcpPermissions"));

        Assert.True(ex is null, "Saving MCP permissions when the store fails escapes the click handler: " + Describe(ex));
        AssertShownRedacted("SaveMcpPermissions");
    }

    [Fact]
    public async Task Settings_shows_a_failed_whitelist_save_without_its_secret()
    {
        var page = SettingsPage(s => WithResources(s).AddSingleton(Stub<IWhitelistService>()
            .On("SaveWhitelistAsync", _ => Task.FromException(LeakyFailure())).Object));

        var ex = await Record.ExceptionAsync(() => Invoke(page, "SaveWhitelist"));

        Assert.True(ex is null, "Saving the whitelist when the store fails escapes the click handler: " + Describe(ex));
        AssertShownRedacted("SaveWhitelist");
    }

    [Fact]
    public async Task Endpoint_checks_reports_a_delete_the_store_fails()
    {
        Services.AddSingleton(Stub<IEndpointCheckStore>()
            .On(nameof(IEndpointCheckStore.List), _ => (IReadOnlyList<EndpointCheckDefinition>)[])
            .On(nameof(IEndpointCheckStore.LastResults), _ => (IReadOnlyDictionary<string, EndpointCheckResultEntity>)new Dictionary<string, EndpointCheckResultEntity>())
            .On(nameof(IEndpointCheckStore.DeleteAsync), _ => Task.FromException(new TimeoutException("The database did not answer."))).Object);
        Services.AddSingleton(Null<IEndpointCheckRunner>());
        Services.AddSingleton<IEndpointCheckSlots>(new EndpointCheckSlots());
        Services.AddSingleton(Null<IServerConfigService>());
        var page = Render<EndpointChecks>();
        var check = (EndpointCheckDefinition)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(EndpointCheckDefinition)) with
        {
            Id = "c1", ServerId = "local", Name = "web",
        };

        var ex = await Record.ExceptionAsync(() => InvokeWith(page, "DeleteAsync", check));

        Assert.True(ex is null, "Deleting an endpoint check while the store fails escapes the click handler: " + Describe(ex));
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Error);
    }

    [Fact]
    public async Task Ai_operations_reports_a_preset_the_store_cannot_save()
    {
        Services.AddSingleton(Null<Whiskers.Services.Mcp.IMcpPermissionService>());
        Services.AddSingleton(Stub<Whiskers.Services.Guardrails.IGuardrailStore>()
            .On("get_Config", _ => new GuardrailConfig())
            .On("SaveConfigAsync", _ => Task.FromException(new IOException("The disk is full."))).Object);
        var page = Render<AiOperationsView>();

        var ex = await Record.ExceptionAsync(() => InvokeWith(page, "ActivatePreset", AiOperationsPresets.SafeOperations));

        Assert.True(ex is null, "'Activate' on a preset while guardrails.json cannot be written escapes the click handler: " + Describe(ex));
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Error);
    }

    [Fact]
    public async Task The_notification_log_reports_a_mark_read_that_ran_out_of_time()
    {
        Services.AddSingleton<IUiMutationRunner>(new FailingRunner());
        Services.AddSingleton(Null<IInAppNotificationStore>());
        var page = Render<NotificationsLog>();

        var ex = await Record.ExceptionAsync(() => Invoke(page, "MarkAllRead"));

        Assert.True(ex is null, "'Mark all read' when the gateway call fails (MutationFailedException) escapes the click handler: " + Describe(ex));
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Error);
    }

    // ------------------------------------------------------------------ pages stuck on "loading"

    [Fact]
    public async Task Agent_history_reports_a_query_that_fails()
    {
        var calls = 0;
        Services.AddSingleton(Stub<Whiskers.Services.Observability.IMcpCallLogStore>()
            .On("CountAsync", _ => ++calls == 1 ? Task.FromResult(1) : Task.FromException<int>(new TimeoutException("The database did not answer.")))
            .On("GetRecentAsync", _ => Task.FromResult(new List<McpToolCallEntity> { new() })).Object);
        var page = Render<AgentHistory>();
        page.WaitForAssertion(() => Assert.Single(Get<List<McpToolCallEntity>>(page.Instance, "_entries")));   // the first load's hit

        var ex = await Record.ExceptionAsync(() => Invoke(page, "Reload"));

        Assert.True(ex is null && !Get<bool>(page.Instance, "_loading"),
            $"A filter change while the call log cannot be read escapes the handler ({Describe(ex)}); _loading stays {Get<bool>(page.Instance, "_loading")}.");
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Error);
        // The previous filter's hits are not shown under the new one.
        Assert.Empty(Get<List<McpToolCallEntity>>(page.Instance, "_entries"));
        Assert.Equal(0, Get<int>(page.Instance, "_totalCount"));
    }

    [Fact]
    public async Task The_notification_log_reports_a_query_that_fails()
    {
        var calls = 0;
        Services.AddSingleton(Stub<IInAppNotificationStore>()
            .On("CountAsync", _ => ++calls == 1 ? Task.FromResult(1) : Task.FromException<int>(new TimeoutException("The database did not answer.")))
            .On("QueryAsync", _ => Task.FromResult(new List<InAppNotification> { new("n1", DateTime.UtcNow, "container_event", "web restarted", "", "Info") })).Object);
        var page = Render<NotificationsLog>();
        page.WaitForAssertion(() => Assert.Single(Get<List<InAppNotification>>(page.Instance, "_entries")));   // the first load's hit

        var ex = await Record.ExceptionAsync(() => Invoke(page, "LoadData"));

        Assert.True(ex is null && !Get<bool>(page.Instance, "_loading"),
            $"A filter or page change while the notification log cannot be read escapes the handler ({Describe(ex)}); _loading stays {Get<bool>(page.Instance, "_loading")}.");
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Error);
        Assert.Empty(Get<List<InAppNotification>>(page.Instance, "_entries"));
        Assert.Equal(0, Get<int>(page.Instance, "_totalCount"));
    }

    [Fact]
    public async Task The_audit_log_reports_a_query_that_fails()
    {
        var calls = 0;
        Services.AddSingleton(Stub<Whiskers.Services.AuditLog.IAuditLogService>()
            .On("GetTotalCountAsync", _ => ++calls == 1 ? Task.FromResult(1) : Task.FromException<int>(new TimeoutException("The database did not answer.")))
            .On("GetRecentAsync", _ => Task.FromResult(new List<AuditLogEntity> { new() })).Object);
        Services.AddSingleton(Null<Whiskers.Services.AuditLog.IAuditChain>());
        var page = Render<AuditLog>();
        page.WaitForAssertion(() => Assert.Single(Get<List<AuditLogEntity>>(page.Instance, "_entries")));   // the first load's hit

        var ex = await Record.ExceptionAsync(() => Invoke(page, "LoadData"));

        Assert.True(ex is null && !Get<bool>(page.Instance, "_loading"),
            $"A filter or page change while the audit log cannot be read escapes the handler ({Describe(ex)}); _loading stays {Get<bool>(page.Instance, "_loading")}.");
        Assert.Contains(Snackbars(), s => s.Severity == Severity.Error);
        Assert.Empty(Get<List<AuditLogEntity>>(page.Instance, "_entries"));
        Assert.Equal(0, Get<int>(page.Instance, "_totalCount"));
    }

    // ------------------------------------------------------------------ runners

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

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default) => action(ct);

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>A gateway call that ends as the runner's own failure (its deadline), as <see cref="UiMutationRunner"/> throws it.</summary>
    private sealed class FailingRunner : IUiMutationRunner
    {
        private static MutationFailedException Failure() => new("The operation ran out of time.", null);

        public Task<T> RunAsync<TTarget, T>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            Task.FromException<T>(Failure());

        public Task RunAsync<TTarget>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            Task.FromException(Failure());

        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default) => Task.FromException<T>(Failure());

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default) => Task.FromException(Failure());

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
