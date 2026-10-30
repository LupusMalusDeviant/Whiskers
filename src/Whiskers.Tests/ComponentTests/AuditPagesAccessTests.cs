using System.Collections.Concurrent;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Auth;
using Whiskers.Services.Observability;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-23 / INV-50: the Audit log and Agent History pages show audit data to Admins only — for any other role
/// they render the Admin-only notice and never ask the stores for a row.</summary>
public class AuditPagesAccessTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private static bool ShowsNotice(string markup) => markup.Contains("AdminOnly") || markup.Contains("administrators");

    private readonly Recording<IAuditLogService> _audit = Recording<IAuditLogService>.Create();
    private readonly Recording<IMcpCallLogStore> _calls = Recording<IMcpCallLogStore>.Create();
    private readonly Recording<IAuditChain> _chain = Recording<IAuditChain>.Create();

    private void Setup(bool admin)
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton((IAuditLogService)(object)_audit);
        Services.AddSingleton((IMcpCallLogStore)(object)_calls);
        Services.AddSingleton((IAuditChain)(object)_chain);
        Services.AddSingleton<ICurrentUserService>(new User(admin ? AppRole.Admin : AppRole.Operator));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void An_operator_sees_the_notice_and_no_audit_row_is_read()
    {
        Setup(admin: false);

        var page = Render<AuditLog>();

        page.WaitForAssertion(() => Assert.True(ShowsNotice(page.Markup), page.Markup));
        Assert.Empty(_audit.Calls);
        Assert.Empty(_chain.Calls);
        Assert.Empty(page.FindAll("table"));
    }

    [Fact]
    public void An_operator_sees_the_notice_and_no_agent_call_is_read()
    {
        Setup(admin: false);

        var page = Render<AgentHistory>();

        page.WaitForAssertion(() => Assert.True(ShowsNotice(page.Markup), page.Markup));
        Assert.Empty(_calls.Calls);
    }

    [Fact]
    public void An_admin_gets_the_rows()
    {
        Setup(admin: true);

        var audit = Render<AuditLog>();
        var history = Render<AgentHistory>();

        audit.WaitForAssertion(() => Assert.Contains(nameof(IAuditLogService.GetRecentAsync), _audit.Calls));
        history.WaitForAssertion(() => Assert.NotEmpty(_calls.Calls));
        Assert.False(ShowsNotice(audit.Markup));
    }

    private sealed class User(AppRole role) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("someone@local.test");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    /// <summary>Records every call; answers with empty lists and default values.</summary>
    public class Recording<T> : DispatchProxy where T : class
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public static Recording<T> Create() => (Recording<T>)(object)DispatchProxy.Create<T, Recording<T>>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Enqueue(method!.Name);
            var type = method.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                object? value = inner.IsValueType ? Activator.CreateInstance(inner)
                    : inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(List<>) ? Activator.CreateInstance(inner)
                    : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
