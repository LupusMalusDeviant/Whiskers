using System.Reflection;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cloud;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Cloud.Providers;
using Whiskers.Services.Deployment;
using Whiskers.Services.Docker;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests.ComponentTests;

/// <summary>
/// R1-21, on the pages' own handlers: what an action created (a container, a network, a saved app) is part of the
/// gateway's one event, and a cloud action the provider reports as failed is a failed event, not a success message.
/// </summary>
public sealed class UiCreatedResourceAuditTests : BunitContext, IAsyncLifetime
{
    private readonly ApprovalDatabase _db = new();
    private readonly Stub<IDeploymentService> _deploy = new();
    private readonly Stub<IDockerService> _docker = new();
    private readonly Stub<IGitDeployService> _gitDeploy = new();

    public UiCreatedResourceAuditTests()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AuditLogMutationSink(_db.Scopes), [], NullLogger<MutationGateway>.Instance,
            decisions: new DbPolicyDecisionStore(_db.Scopes),
            approvals: new ApprovalGate(new ApprovalStore(policy: store, persistence: _db.Persistence()), store),
            journal: new DbOperationJournal(_db.Scopes));

        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(new Stub<IKeyInterceptorService>().Object);
        Services.AddSingleton<ICurrentUserService>(new AdminUser());
        Services.AddSingleton<IUiMutationRunner>(new UiMutationRunner(gateway, new CapabilityCatalog(), new AdminUser()));
        Services.AddSingleton(_deploy.Object);
        Services.AddSingleton(_docker.Object);
        Services.AddSingleton(_gitDeploy.Object);
        Services.AddSingleton(new Stub<IServerConfigService>().Object);
        Services.AddSingleton(new Stub<Whiskers.Services.Vault.IVaultService>().Object);
        Services.AddSingleton(new Stub<IAutomationOwnerResolver>().Object);
        Services.AddSingleton(new Stub<Whiskers.Modules.IModuleRegistry>().On("IsEnabled", _ => true).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _db.Dispose();
    }

    private async Task<AuditLogEntity> TheEventAsync(string capabilityId)
    {
        using var scope = _db.Scopes.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(r => r.Phase == "outcome").ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(capabilityId, row.Action);
        Assert.Equal("Executed", row.Outcome);
        return row;
    }

    [Fact]
    public async Task A_form_deploy_names_the_container_it_created()
    {
        _deploy.On(nameof(IDeploymentService.DeployFromFormAsync), _ => Task.FromResult("c0ffee42"));
        var page = Render<Deploy>();
        Field<DeploymentRequest>(page, "_formRequest").Image = "nginx:1.27";

        await Invoke(page, "DeployFromForm");

        Assert.Contains("c0ffee42", (await TheEventAsync("app.deploy")).Details);
    }

    [Fact]
    public async Task A_compose_deploy_names_the_containers_it_created()
    {
        _deploy.On(nameof(IDeploymentService.ValidateCompose), _ => new DeploymentValidationResult
        {
            IsValid = true,
            Services = [new DeploymentRequest { ContainerName = "api" }, new DeploymentRequest { ContainerName = "db" }],
        });
        _deploy.On(nameof(IDeploymentService.DeployFromComposeAsync),
            _ => Task.FromResult<IList<string>>(["id-api", "id-db"]));
        var page = Render<Deploy>();
        SetField(page, "_composeYaml", "services:\n  api:\n    image: nginx:1.27\n");

        await Invoke(page, "DeployFromCompose");

        var details = (await TheEventAsync("compose.deploy")).Details;
        Assert.Contains("2 container(s)", details);
        Assert.Contains("id-api", details);
        Assert.Contains("id-db", details);
    }

    [Fact]
    public async Task A_created_network_is_named_in_the_event()
    {
        _docker.On(nameof(IDockerService.CreateNetworkAsync), _ => Task.FromResult("net-9f8e7d"));
        var page = Render<Networks>();
        SetField(page, "_newNetworkName", "backend");

        await Invoke(page, "CreateNetwork");

        Assert.Contains("net-9f8e7d", (await TheEventAsync("network.create")).Details);
    }

    [Fact]
    public async Task A_saved_git_deploy_app_is_named_in_the_event()
    {
        _gitDeploy.On(nameof(IGitDeployService.SaveAppAsync),
            args => Task.FromResult(new GitDeployApp { Id = "app-new-1", Name = ((GitDeployApp)args[0]!).Name }));
        var page = Render<GitDeployView>();
        SetField(page, "_form", new GitDeployApp { Name = "shop", ServerId = "local", Branch = "main" });

        await Invoke(page, "SaveApp");

        Assert.Contains("app-new-1", (await TheEventAsync("gitdeploy.save")).Details);
    }

    [Theory]
    [InlineData("power on")]
    [InlineData("snapshot")]
    public async Task A_cloud_action_hetzner_reports_as_failed_is_a_failed_event(string verb)
    {
        var failed = new HetznerAction { Status = "error", Error = new HetznerActionError { Code = "locked", Message = "server is locked" } };
        var hetzner = new Stub<IHetznerService>()
            .On(nameof(IHetznerService.PowerOnAsync), _ => Task.FromResult<HetznerAction?>(failed))
            .On(nameof(IHetznerService.CreateSnapshotAsync),
                _ => Task.FromResult<HetznerActionResponse?>(new HetznerActionResponse { Action = failed }));
        var provider = new HetznerCloudProvider(hetzner.Object);
        var target = new CloudServerInfo { WhiskersId = "local", Name = "web-1", Provider = CloudProvider.Hetzner, CloudId = 7 };
        var runner = Services.GetRequiredService<IUiMutationRunner>();
        var capability = verb == "snapshot" ? "cloud.snapshot_create" : "cloud.power_on";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(capability,
            new { server = "local" },
            _ => verb == "snapshot" ? provider.CreateSnapshotAsync(target, "token", "pre-change") : provider.PowerOnAsync(target, "token")));

        Assert.Contains("server is locked", ex.Message);
        using var scope = _db.Scopes.CreateScope();
        var row = Assert.Single(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(r => r.Phase == "outcome").ToListAsync());
        Assert.Equal("Failed", row.Outcome);
        Assert.False(row.Success);
        Assert.Contains("server is locked", row.Details);
    }

    [Fact]
    public async Task A_finished_hetzner_action_is_still_a_success()
    {
        var hetzner = new Stub<IHetznerService>()
            .On(nameof(IHetznerService.PowerOnAsync), _ => Task.FromResult<HetznerAction?>(new HetznerAction { Status = "running" }));
        var target = new CloudServerInfo { Name = "web-1", Provider = CloudProvider.Hetzner, CloudId = 7 };

        Assert.Contains("running", await new HetznerCloudProvider(hetzner.Object).PowerOnAsync(target, "token"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Task Invoke<T>(IRenderedComponent<T> page, string handler) where T : Microsoft.AspNetCore.Components.IComponent
    {
        var method = typeof(T).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!;
        return page.InvokeAsync(() => (Task)method.Invoke(page.Instance, null)!);
    }

    private static void SetField<T>(IRenderedComponent<T> page, string field, object? value) where T : Microsoft.AspNetCore.Components.IComponent =>
        typeof(T).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page.Instance, value);

    private static TField Field<TField>(IRenderedComponent<Deploy> page, string field) =>
        (TField)typeof(Deploy).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page.Instance)!;

    private sealed class AdminUser : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("ops@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Admin);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(true);
    }

    /// <summary>An implementation of <typeparamref name="T"/> answering the configured methods; every other method
    /// returns an empty list, a completed task or the default.</summary>
    public sealed class Stub<T> where T : class
    {
        private readonly Dictionary<string, Func<object?[], object?>> _answers = new();
        public T Object { get; }

        public Stub()
        {
            Object = DispatchProxy.Create<T, Proxy>();
            ((Proxy)(object)Object).Answers = _answers;
        }

        public Stub<T> On(string method, Func<object?[], object?> answer)
        {
            _answers[method] = answer;
            return this;
        }

        public class Proxy : DispatchProxy
        {
            internal Dictionary<string, Func<object?[], object?>> Answers = new();

            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (Answers.TryGetValue(method!.Name, out var answer)) return answer(args ?? []);
                var type = method.ReturnType;
                if (type == typeof(void)) return null;
                if (type == typeof(Task)) return Task.CompletedTask;
                if (type == typeof(ValueTask)) return ValueTask.CompletedTask;
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var inner = type.GetGenericArguments()[0];
                    return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [Empty(inner)]);
                }
                return Empty(type);
            }

            private static object? Empty(Type type)
            {
                if (type.IsValueType) return Activator.CreateInstance(type);
                if (type.IsGenericType && type.GetGenericArguments().Length == 1 &&
                    typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
                {
                    var list = typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]);
                    if (type.IsAssignableFrom(list)) return Activator.CreateInstance(list);
                }
                return null;
            }
        }
    }
}
